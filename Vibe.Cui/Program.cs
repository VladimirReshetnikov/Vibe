using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Terminal.Gui;
using Vibe.Cui;

public class Program
{
    static readonly DllAnalyzer Analyzer = new();
    static LoadedDll? Dll;
    static List<TypeDefinition> ManagedTypes = new();
    static ListView ItemList = null!;
    static TextView CodeView = null!;
    static bool ShowingExports = true;

    public static void Main()
    {
        Application.Init();
        var top = Application.Top;

        var menu = new MenuBar(new[]
        {
            new MenuBarItem("_File", new MenuItem[]
            {
                new MenuItem("_Open", string.Empty, OpenFile),
                new MenuItem("_Quit", string.Empty, () => { Dll?.Dispose(); Application.RequestStop(); })
            }),
            new MenuBarItem("_View", new MenuItem[]
            {
                new MenuItem("_Exports", string.Empty, LoadExports, () => Dll != null),
                new MenuItem("_Managed Types", string.Empty, LoadManagedTypes, () => Dll?.IsManaged ?? false)
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

        var leftPane = new FrameView("Items")
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(30),
            Height = Dim.Fill()
        };

        var rightPane = new FrameView("Details")
        {
            X = Pos.Right(leftPane),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        ItemList = new ListView(new List<string>())
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true
        };

        CodeView = new TextView()
        {
            ReadOnly = true,
            WordWrap = false,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        ItemList.OpenSelectedItem += async args =>
        {
            if (Dll == null) return;
            var selected = (string)args.Value;
            if (ShowingExports)
            {
                CodeView.Text = "Loading...";
                var code = await Analyzer.GetDecompiledExportAsync(
                    Dll,
                    selected,
                    new Progress<string>(p => Application.MainLoop.Invoke(() => CodeView.Text = p)),
                    Dll.Cts.Token);
                Application.MainLoop.Invoke(() => CodeView.Text = code);
            }
            else
            {
                var type = ManagedTypes.First(t => t.FullName == selected);
                if (!type.Methods.Any())
                {
                    CodeView.Text = "// Type has no methods";
                    return;
                }

                var methods = type.Methods.Select(m => m.FullName).ToList();
                var methodList = new ListView(methods)
                {
                    Width = Dim.Fill(),
                    Height = Dim.Fill()
                };

                var close = new Button("Close", is_default: true);
                var dlg = new Dialog("Select Method", 60, 20, close);
                dlg.Add(methodList);

                methodList.OpenSelectedItem += args2 =>
                {
                    var methodName = (string)args2.Value;
                    var method = type.Methods.First(m => m.FullName == methodName);
                    var body = Analyzer.GetManagedMethodBody(method);
                    CodeView.Text = body;
                    Application.RequestStop();
                };
                close.Clicked += () => Application.RequestStop();

                Application.Run(dlg);
            }
        };

        leftPane.Add(ItemList);
        rightPane.Add(CodeView);
        win.Add(leftPane, rightPane);
        top.Add(win);

        Application.Run();
        Dll?.Dispose();
        Application.Shutdown();
    }

    static async void OpenFile()
    {
        var dialog = new OpenDialog("Open DLL", "Select a DLL")
        {
            AllowsMultipleSelection = false,
            CanChooseDirectories = false
        };
        Application.Run(dialog);
        if (dialog.Canceled || string.IsNullOrEmpty(dialog.FilePath.ToString()))
            return;

        Dll?.Dispose();
        Dll = Analyzer.Load(dialog.FilePath.ToString()!);
        CodeView.Text = Analyzer.GetSummary(Dll);

        await LoadExportsAsync();
    }

    static async void LoadExports() => await LoadExportsAsync();

    static async Task LoadExportsAsync()
    {
        if (Dll == null) return;
        var exports = await Analyzer.GetExportNamesAsync(Dll, Dll.Cts.Token);
        Application.MainLoop.Invoke(() =>
        {
            ItemList.SetSource(exports);
            ShowingExports = true;
        });
    }

    static async void LoadManagedTypes()
    {
        if (Dll == null || !Dll.IsManaged) return;
        ManagedTypes = await Analyzer.GetManagedTypesAsync(Dll, Dll.Cts.Token);
        Application.MainLoop.Invoke(() =>
        {
            ItemList.SetSource(ManagedTypes.Select(t => t.FullName).ToList());
            ShowingExports = false;
        });
    }
}

