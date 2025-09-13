using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Terminal.Gui;
using Vibe.Decompiler;

/// <summary>
/// Entry point and console user interface for exploring DLL exports and
/// viewing decompiled output in a terminal environment.
/// </summary>
public class Program
{
    static readonly DllAnalyzer Analyzer = new();
    static LoadedDll? Dll;
    static List<TypeDefinition> ManagedTypes = new();
    static ListView ItemList = null!;
    static TextView CodeView = null!;
    static bool ShowingExports = true;

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
                    Dll.Cts.Token).ConfigureAwait(false);
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

                methodList.OpenSelectedItem += async args2 =>
                {
                    var methodName = (string)args2.Value;
                    var method = type.Methods.First(m => m.FullName == methodName);
                    CodeView.Text = "Loading...";
                    var body = await Analyzer.GetManagedMethodBodyAsync(
                        Dll!,
                        method,
                        new Progress<string>(p => Application.MainLoop.Invoke(() => CodeView.Text = p)),
                        Dll.Cts.Token).ConfigureAwait(false);
                    Application.MainLoop.Invoke(() =>
                    {
                        CodeView.Text = body;
                        Application.RequestStop();
                    });
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

    /// <summary>
    /// Displays a file picker and loads the selected DLL into the analyzer.
    /// </summary>
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

        await LoadExportsAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Initiates asynchronous loading of export names from the current DLL.
    /// </summary>
    static async void LoadExports() => await LoadExportsAsync().ConfigureAwait(false);

    /// <summary>
    /// Retrieves export names from the loaded DLL and populates the left pane.
    /// </summary>
    static async Task LoadExportsAsync()
    {
        if (Dll == null) return;
        var exports = await Analyzer.GetExportNamesAsync(Dll, Dll.Cts.Token).ConfigureAwait(false);
        Application.MainLoop.Invoke(() =>
        {
            ItemList.SetSource(exports);
            ShowingExports = true;
        });
    }

    /// <summary>
    /// Loads the list of managed types from the currently opened DLL.
    /// </summary>
    static async void LoadManagedTypes()
    {
        if (Dll == null || !Dll.IsManaged) return;
        ManagedTypes = await Analyzer.GetManagedTypesAsync(Dll, Dll.Cts.Token).ConfigureAwait(false);
        Application.MainLoop.Invoke(() =>
        {
            ItemList.SetSource(ManagedTypes.Select(t => t.FullName).ToList());
            ShowingExports = false;
        });
    }
}

