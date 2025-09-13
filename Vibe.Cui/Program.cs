using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Terminal.Gui;
using Terminal.Gui.Trees;
using Vibe.Decompiler;

/// <summary>
/// Entry point and console user interface for exploring DLL exports and
/// viewing decompiled output in a terminal environment.
/// </summary>
public class Program
{
    static readonly DllAnalyzer Analyzer = new();
    static readonly List<LoadedDll> LoadedDlls = new();
    static readonly Dictionary<ModuleDefinition, LoadedDll> ModuleToDll = new();
    static readonly Dictionary<object, List<object>> ChildCache = new();

    static TreeView<object> DllTree = null!;
    static TextView CodeView = null!;

    /// <summary>
    /// Launches the terminal UI and initializes application state.
    /// </summary>
    public static void Main()
    {
        Application.Init();
        var top = Application.Top;

        var menu = new MenuBar(new[]
        {
            new MenuBarItem("_File", new MenuItem[]
            {
                new MenuItem("_Open", "Ctrl+O", OpenFile, null, null, Key.CtrlMask | Key.O),
                new MenuItem("_Quit", string.Empty, () =>
                {
                    foreach (var dll in LoadedDlls)
                        dll.Dispose();
                    Application.RequestStop();
                })
            })
        });
        top.Add(menu);

        var statusBar = new StatusBar(new StatusItem[]
        {
            new StatusItem(Key.F9, "~F9~ Menu", () => menu.OpenMenu())
        });
        top.Add(statusBar);

        var win = new Window("Vibe Console Interface")
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1)
        };

        DllTree = new TreeView<object>()
        {
            Width = Dim.Percent(30),
            Height = Dim.Fill(),
            CanFocus = true
        };

        DllTree.AspectGetter = o => o switch
        {
            LoadedDll dll => Path.GetFileName(dll.Pe.FilePath),
            NamespaceNode ns => ns.Name,
            TypeDefinition t => t.Name,
            MethodDefinition m => FormatMethodSignature(m),
            ExportItem e => e.Name,
            _ => o?.ToString() ?? string.Empty
        };

        DllTree.TreeBuilder = new DelegateTreeBuilder<object>(GetChildren, HasChildren);
        DllTree.ObjectActivated += OnObjectActivated;

        CodeView = new TextView()
        {
            ReadOnly = true,
            WordWrap = false,
            X = Pos.Right(DllTree),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        win.Add(DllTree, CodeView);
        top.Add(win);

        Application.Run();
        foreach (var dll in LoadedDlls)
            dll.Dispose();
        Application.Shutdown();
    }

    static IEnumerable<object> GetChildren(object node)
    {
        if (ChildCache.TryGetValue(node, out var cached))
            return cached;

        List<object> result;
        switch (node)
        {
            case LoadedDll dll:
                if (dll.IsManaged)
                {
                    var types = Analyzer.GetManagedTypesAsync(dll, dll.Cts.Token).GetAwaiter().GetResult();
                    result = types
                        .GroupBy(t => string.IsNullOrEmpty(t.Namespace) ? "(global)" : t.Namespace)
                        .OrderBy(g => g.Key, StringComparer.Ordinal)
                        .Select(g => (object)new NamespaceNode(g.Key, g.OrderBy(t => t.Name, StringComparer.Ordinal).ToList()))
                        .ToList();
                }
                else
                {
                    var names = Analyzer.GetExportNamesAsync(dll, dll.Cts.Token).GetAwaiter().GetResult();
                    result = names.Select(n => (object)new ExportItem { Dll = dll, Name = n }).ToList();
                }
                break;
            case NamespaceNode ns:
                result = ns.Types.Cast<object>().ToList();
                break;
            case TypeDefinition type:
                result = type.Methods.Cast<object>().ToList();
                break;
            default:
                result = new List<object>();
                break;
        }

        ChildCache[node] = result;
        return result;
    }

    static bool HasChildren(object node) => node switch
    {
        LoadedDll => true,
        NamespaceNode ns => ns.Types.Count > 0,
        TypeDefinition t => t.Methods.Any(),
        _ => false
    };

    static async void OnObjectActivated(ObjectActivatedEventArgs<object> args)
    {
        switch (args.ActivatedObject)
        {
            case LoadedDll dll:
                CodeView.Text = Analyzer.GetSummary(dll);
                break;
            case ExportItem export:
                CodeView.Text = "Loading...";
                var code = await Analyzer.GetDecompiledExportAsync(
                    export.Dll,
                    export.Name,
                    new Progress<string>(p => Application.MainLoop.Invoke(() => CodeView.Text = p)),
                    export.Dll.Cts.Token).ConfigureAwait(false);
                Application.MainLoop.Invoke(() => CodeView.Text = code);
                break;
            case MethodDefinition method:
                if (method.Module == null || !ModuleToDll.TryGetValue(method.Module, out var mdll))
                {
                    CodeView.Text = "// Unable to locate DLL";
                    return;
                }
                CodeView.Text = "Loading...";
                var body = await Analyzer.GetManagedMethodBodyAsync(
                    mdll,
                    method,
                    new Progress<string>(p => Application.MainLoop.Invoke(() => CodeView.Text = p)),
                    mdll.Cts.Token).ConfigureAwait(false);
                Application.MainLoop.Invoke(() => CodeView.Text = body);
                break;
        }
    }

    static void OpenFile()
    {
        var dialog = new OpenDialog("Open DLL", "Select a DLL")
        {
            AllowsMultipleSelection = false,
            CanChooseDirectories = false
        };
        Application.Run(dialog);
        if (dialog.Canceled || string.IsNullOrEmpty(dialog.FilePath.ToString()))
            return;

        var dll = Analyzer.Load(dialog.FilePath.ToString()!);
        LoadedDlls.Add(dll);
        if (dll.ManagedModule is { } module)
            ModuleToDll[module] = dll;

        CodeView.Text = Analyzer.GetSummary(dll);
        DllTree.AddObject(dll);
    }

    static string FormatMethodSignature(MethodDefinition method)
    {
        var parameters = string.Join(", ", method.Parameters.Select(p => FormatTypeName(p.ParameterType)));
        if (method.IsConstructor)
            return $"{method.DeclaringType.Name}({parameters})";
        return $"{FormatTypeName(method.ReturnType)} {method.Name}({parameters})";
    }

    static string FormatTypeName(TypeReference type)
    {
        if (type is GenericInstanceType git)
        {
            var name = git.ElementType.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
                name = name[..tick];
            var args = string.Join(", ", git.GenericArguments.Select(FormatTypeName));
            return $"{name}<{args}>";
        }

        if (type is ArrayType at)
            return $"{FormatTypeName(at.ElementType)}[{new string(',', at.Rank - 1)}]";

        return type.FullName switch
        {
            "System.Void" => "void",
            "System.Object" => "object",
            "System.String" => "string",
            "System.Boolean" => "bool",
            "System.Byte" => "byte",
            "System.SByte" => "sbyte",
            "System.Int16" => "short",
            "System.UInt16" => "ushort",
            "System.Int32" => "int",
            "System.UInt32" => "uint",
            "System.Int64" => "long",
            "System.UInt64" => "ulong",
            "System.Char" => "char",
            "System.Single" => "float",
            "System.Double" => "double",
            "System.Decimal" => "decimal",
            "System.IntPtr" => "nint",
            "System.UIntPtr" => "nuint",
            _ => type.Name
        };
    }

    sealed class NamespaceNode
    {
        public string Name { get; }
        public List<TypeDefinition> Types { get; }

        public NamespaceNode(string name, List<TypeDefinition> types)
        {
            Name = name;
            Types = types;
        }

        public override string ToString() => Name;
    }

    sealed class ExportItem
    {
        public required LoadedDll Dll { get; init; }
        public required string Name { get; init; }
    }
}

