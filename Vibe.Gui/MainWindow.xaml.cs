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
    private readonly Lazy<ILlmProvider> _provider;

    public MainWindow()
    {
        InitializeComponent();
        OutputBox.TextArea.TextView.LineTransformers.Add(new PseudoCodeColorizer());
        _config = AppConfig.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));
        _provider = new(() =>
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("OPENAI_API_KEY environment variable is not set.");
            string model = string.IsNullOrWhiteSpace(_config.LlmVersion) ? "gpt-4o-mini" : _config.LlmVersion;
            return new OpenAiLlmProvider(apiKey, model, _config.LlmMaxTokens);
        });
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
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (DllTree.SelectedItem is not TreeViewItem item)
            return;

        switch (item.Tag)
        {
            case DllItem dll:
                OutputBox.Text = dll.Pe.GetSummary();
                return;
            case ExportItem exp:
                BusyBar.Visibility = Visibility.Visible;
                try
                {
                    var dllItem = exp.Dll;
                    var pe2 = dllItem.Pe;
                    var token = dllItem.Cts.Token;
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

                    if (_config.MaxLlmCodeLength > 0 && code.Length > _config.MaxLlmCodeLength)
                        code = code.Substring(0, _config.MaxLlmCodeLength);
                    string refined = await _provider.Value.RefineAsync(code, null, token);
                    OutputBox.Text = refined;
                }
                catch (OperationCanceledException)
                {
                    // Operation canceled, ignore
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    BusyBar.Visibility = Visibility.Collapsed;
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
        if (ReferenceEquals(item, DllTree.SelectedItem))
            OutputBox.Text = string.Empty;
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Dispose all DLL items to clean up CancellationTokenSource objects
        foreach (TreeViewItem item in DllTree.Items)
        {
            if (item.Tag is DllItem dll)
                dll.Dispose();
        }

        if (_provider.IsValueCreated)
            _provider.Value.Dispose();
        base.OnClosed(e);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
