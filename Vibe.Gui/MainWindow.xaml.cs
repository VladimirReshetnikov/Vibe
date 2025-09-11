using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Vibe.Decompiler;
using ICSharpCode.AvalonEdit;

namespace Vibe.Gui;

public partial class MainWindow : Window
{
    private sealed class ExportItem
    {
        public required PEReaderLite Pe { get; init; }
        public required string Name { get; init; }
    }

    public MainWindow()
    {
        InitializeComponent();
        OutputBox.TextArea.TextView.LineTransformers.Add(new PseudoCodeColorizer());
        LoadCommonDlls();
    }

    private readonly Lazy<ILlmProvider> _provider = new(() =>
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY environment variable is not set.");
        return new OpenAiLlmProvider(apiKey);
    });

    private CancellationTokenSource? _decompileCts;

    private void LoadDll(string path, bool showErrors)
    {
        try
        {
            var pe = new PEReaderLite(path);

            var dllIcon = (ImageSource)FindResource("DllIconImage");
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new Image { Source = dllIcon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0) });
            headerPanel.Children.Add(new TextBlock { Text = Path.GetFileName(path) });

            var root = new TreeViewItem { Header = headerPanel, Tag = pe };
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
        if (root.Tag is not PEReaderLite pe)
            return;

        // Only load once when the placeholder is present
        if (root.Items.Count != 1 || root.Items[0] is not TreeViewItem placeholder || !Equals(placeholder.Tag, "Loading"))
            return;

        root.Items.Clear();
        var funcIcon = (ImageSource)FindResource("ExportedFunctionIconImage");
        var names = await Task.Run(() => pe.EnumerateExportNames().OrderBy(n => n).ToList());

        int i = 0;
        foreach (var name in names)
        {
            var funcHeader = new StackPanel { Orientation = Orientation.Horizontal };
            funcHeader.Children.Add(new Image { Source = funcIcon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0) });
            funcHeader.Children.Add(new TextBlock { Text = name });
            root.Items.Add(new TreeViewItem { Header = funcHeader, Tag = new ExportItem { Pe = pe, Name = name } });

            if (++i % 20 == 0)
                await Dispatcher.Yield();
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
            case PEReaderLite pe:
                _decompileCts?.Cancel();
                OutputBox.Text = pe.GetSummary();
                return;
            case ExportItem exp:
                _decompileCts?.Cancel();
                var cts = new CancellationTokenSource();
                _decompileCts = cts;
                BusyBar.Visibility = Visibility.Visible;
                try
                {
                    var pe2 = exp.Pe;
                    var name = exp.Name;
                    var export = pe2.FindExport(name);
                    if (export.IsForwarder)
                    {
                        OutputBox.Text = $"{name} -> {export.ForwarderString}";
                        return;
                    }

                    var code = await Task.Run(() =>
                    {
                        int off = pe2.RvaToOffsetChecked(export.FunctionRva);
                        int maxLen = Math.Min(4096, pe2.Data.Length - off);
                        var bytes = new byte[maxLen];
                        Array.Copy(pe2.Data, off, bytes, 0, maxLen);
                        var engine = new Engine();
                        return engine.ToPseudoCode(bytes, new Engine.Options
                        {
                            BaseAddress = pe2.ImageBase + export.FunctionRva,
                            FunctionName = name
                        });
                    }, cts.Token);

                    string refined = await _provider.Value.RefineAsync(code, null, cts.Token);
                    if (!cts.IsCancellationRequested && _decompileCts == cts)
                        OutputBox.Text = refined;
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellations
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    if (_decompileCts == cts)
                        BusyBar.Visibility = Visibility.Collapsed;
                }
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_provider.IsValueCreated)
            _provider.Value.Dispose();
        base.OnClosed(e);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
