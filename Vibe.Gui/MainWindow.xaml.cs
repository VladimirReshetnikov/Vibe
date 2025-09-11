using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using Microsoft.Win32;
using Vibe.Decompiler;
using ICSharpCode.AvalonEdit;

namespace Vibe.Gui;

public partial class MainWindow : Window
{
    private sealed class DllItem : IDisposable
    {
        public required PEReaderLite Pe { get; init; }
        public required CancellationTokenSource Cts { get; init; }

        public void Dispose()
        {
            Cts.Dispose();
        }
    }

    private sealed class ExportItem
    {
        public required DllItem Dll { get; init; }
        public required string Name { get; init; }
    }

    private readonly AppConfig _config;
    private readonly ILlmProvider? _provider;
    private CancellationTokenSource? _currentRequestCts;

    public MainWindow()
    {
        InitializeComponent();
        OutputBox.TextArea.TextView.LineTransformers.Add(new PseudoCodeColorizer());
        _config = AppConfig.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            string model = string.IsNullOrWhiteSpace(_config.LlmVersion) ? "gpt-4o-mini" : _config.LlmVersion;
            _provider = new OpenAiLlmProvider(apiKey, model);
        }
        LoadCommonDlls();
    }

    private void LoadDll(string path, bool showErrors)
    {
        try
        {
            var dll = new DllItem { Pe = new PEReaderLite(path), Cts = new CancellationTokenSource() };

            var dllIcon = (ImageSource)FindResource("DllIconImage");
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new Image { Source = dllIcon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0) });
            headerPanel.Children.Add(new TextBlock { Text = Path.GetFileName(path) });

            var root = new TreeViewItem { Header = headerPanel, Tag = dll };
            // Add a dummy child so the expand arrow appears and load exports on demand
            root.Items.Add(new TreeViewItem { Header = "Loading...", Tag = "Loading" });
            root.Expanded += DllRoot_Expanded;
            DllTree.Items.Add(root);
            root.IsExpanded = false;
        }
        catch (Exception ex)
        {
            if (showErrors)
                ExceptionManager.Handle(ex);
        }
    }

    private void LoadCommonDlls()
    {
        var systemDir = Environment.SystemDirectory;
        string[] dlls =
        {
            "kernel32.dll",
            "user32.dll",
            "dbghelp.dll"
        };

        foreach (var name in dlls)
        {
            var path = Path.Combine(systemDir, name);
            if (File.Exists(path))
                LoadDll(path, showErrors: false);
        }
    }

    private void CancelCurrentRequest()
    {
        _currentRequestCts?.Cancel();
        _currentRequestCts?.Dispose();
        _currentRequestCts = null;
    }

    private async void DllRoot_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem root)
            return;
        if (root.Tag is not DllItem dll)
            return;
        var pe = dll.Pe;
        var token = dll.Cts.Token;

        // Only load once when the placeholder is present
        if (root.Items.Count != 1 || root.Items[0] is not TreeViewItem placeholder || !Equals(placeholder.Tag, "Loading"))
            return;

        root.Items.Clear();
        var funcIcon = (ImageSource)FindResource("ExportedFunctionIconImage");
        try
        {
            var names = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return pe.EnumerateExportNames().OrderBy(n => n).ToList();
            }, token);

            int i = 0;
            foreach (var name in names)
            {
                token.ThrowIfCancellationRequested();
                var funcHeader = new StackPanel { Orientation = Orientation.Horizontal };
                funcHeader.Children.Add(new Image { Source = funcIcon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0) });
                funcHeader.Children.Add(new TextBlock { Text = name });
                root.Items.Add(new TreeViewItem { Header = funcHeader, Tag = new ExportItem { Dll = dll, Name = name } });

                if (++i % 20 == 0)
                    await Dispatcher.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
    }

    private void OpenDll_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "DLL files (*.dll)|*.dll|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true)
        {
            LoadDll(dlg.FileName, showErrors: true);
        }
    }

    private async void DllTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        CancelCurrentRequest();
        BusyBar.Visibility = Visibility.Collapsed;

        if (DllTree.SelectedItem is not TreeViewItem item)
            return;

        switch (item.Tag)
        {
            case DllItem dll:
                OutputBox.Text = dll.Pe.GetSummary();
                return;
            case ExportItem exp:
                OutputBox.Text = string.Empty;
                BusyBar.Visibility = Visibility.Visible;
                var dllItem = exp.Dll;
                var pe2 = dllItem.Pe;
                _currentRequestCts = CancellationTokenSource.CreateLinkedTokenSource(dllItem.Cts.Token);
                var token = _currentRequestCts.Token;
                try
                {
                    var name = exp.Name;
                    var export = pe2.FindExport(name);
                    if (export.IsForwarder)
                    {
                        OutputBox.Text = $"{name} -> {export.ForwarderString}";
                        return;
                    }

                    var code = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        int off = pe2.RvaToOffsetChecked(export.FunctionRva);
                        int maxLen = Math.Min(_config.MaxDataSizeBytes, pe2.Data.Length - off);
                        var engine = new Engine();
                        return engine.ToPseudoCode(pe2.Data.AsMemory(off, maxLen), new Engine.Options
                        {
                            BaseAddress = pe2.ImageBase + export.FunctionRva,
                            FunctionName = name
                        });
                    }, token);

                    if (_provider != null && _config.MaxLlmCodeLength > 0 && code.Length > _config.MaxLlmCodeLength)
                        code = code.Substring(0, _config.MaxLlmCodeLength);
                    string output = code;
                    if (_provider != null)
                        output = await _provider.RefineAsync(code, null, token);
                    OutputBox.Text = output;
                }
                catch (OperationCanceledException)
                {
                    // Operation canceled, ignore
                }
                catch (Exception ex)
                {
                    ExceptionManager.Handle(ex);
                }
                finally
                {
                    if (_currentRequestCts?.Token == token)
                    {
                        BusyBar.Visibility = Visibility.Collapsed;
                        CancelCurrentRequest();
                    }
                }
                break;
        }
    }

    private static TreeViewItem GetRootItem(TreeViewItem item)
    {
        var parent = ItemsControl.ItemsControlFromItemContainer(item) as TreeViewItem;
        return parent is null ? item : GetRootItem(parent);
    }

    private void DllTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;
        if (DllTree.SelectedItem is not TreeViewItem item)
            return;

        TreeViewItem root;
        DllItem? dll;
        switch (item.Tag)
        {
            case DllItem di:
                dll = di;
                root = item;
                break;
            case ExportItem exp:
                dll = exp.Dll;
                root = GetRootItem(item);
                break;
            default:
                return;
        }

        dll.Cts.Cancel();
        dll.Dispose();
        DllTree.Items.Remove(root);
        CancelCurrentRequest();
        BusyBar.Visibility = Visibility.Collapsed;
        if (ReferenceEquals(item, DllTree.SelectedItem))
            OutputBox.Text = string.Empty;
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelCurrentRequest();
        // Dispose all DLL items to clean up CancellationTokenSource objects
        foreach (TreeViewItem item in DllTree.Items)
        {
            if (item.Tag is DllItem dll)
                dll.Dispose();
        }

        _provider?.Dispose();
        base.OnClosed(e);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
