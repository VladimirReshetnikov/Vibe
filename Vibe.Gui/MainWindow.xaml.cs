using System;
using System.Collections.Generic;
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
using System.Security.Cryptography;

namespace Vibe.Gui;

public partial class MainWindow : Window
{
    private sealed class DllItem : IDisposable
    {
        public required PEReaderLite Pe { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required string FileHash { get; init; }

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

    private readonly ILlmProvider? _provider;
    // Quick-search state (type-to-select export by prefix)
    private string _searchText = string.Empty;
    private DateTime _lastKeyTime;
    // Cancellation for the currently running decompile/refine task
    private CancellationTokenSource? _currentRequestCts;
    private const string RecentFilesKey = @"Software\\Vibe\\RecentFiles";
    private readonly List<string> _recentFiles;

    public MainWindow()
    {
        InitializeComponent();
        OutputBox.TextArea.TextView.LineTransformers.Add(new PseudoCodeColorizer());
        _recentFiles = LoadRecentFiles();
        UpdateRecentFilesMenu();
        var apiKey = App.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var cfg = AppConfig.Current;
            string model = string.IsNullOrWhiteSpace(cfg.LlmVersion) ? "gpt-4o-mini" : cfg.LlmVersion;
            _provider = new OpenAiLlmProvider(apiKey, model, reasoningEffort: cfg.LlmReasoningEffort);
        }
        LoadCommonDlls();
    }

    private void LoadDll(string path, bool showErrors)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(fs));
            var dll = new DllItem { Pe = new PEReaderLite(path), Cts = new CancellationTokenSource(), FileHash = hash };

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
        [
            "kernel32.dll",
            "user32.dll",
            "dbghelp.dll"
        ];

        foreach (var name in dlls)
        {
            var path = Path.Combine(systemDir, name);
            if (File.Exists(path))
                LoadDll(path, showErrors: false);
        }
    }

    private List<string> LoadRecentFiles()
    {
        var list = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RecentFilesKey);
            if (key != null)
            {
                foreach (var name in key.GetValueNames().OrderBy(n => n))
                {
                    if (key.GetValue(name) is string path && File.Exists(path))
                        list.Add(path);
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private void SaveRecentFiles()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RecentFilesKey);
            if (key != null)
            {
                foreach (var name in key.GetValueNames())
                    key.DeleteValue(name, false);
                for (int i = 0; i < _recentFiles.Count; i++)
                    key.SetValue($"File{i}", _recentFiles[i]);
            }
        }
        catch
        {
        }
    }

    private void AddRecentFile(string path)
    {
        _recentFiles.Remove(path);
        _recentFiles.Insert(0, path);
        var cfg = AppConfig.Current;
        if (_recentFiles.Count > cfg.MaxRecentFiles)
            _recentFiles.RemoveRange(cfg.MaxRecentFiles, _recentFiles.Count - cfg.MaxRecentFiles);
        SaveRecentFiles();
        UpdateRecentFilesMenu();
    }

    private void UpdateRecentFilesMenu()
    {
        RecentFilesMenu.Items.Clear();
        if (_recentFiles.Count == 0)
        {
            RecentFilesMenu.IsEnabled = false;
            return;
        }
        RecentFilesMenu.IsEnabled = true;
        foreach (var file in _recentFiles)
        {
            var item = new MenuItem { Header = file, Tag = file };
            item.Click += RecentFile_Click;
            RecentFilesMenu.Items.Add(item);
        }
    }

    private void RecentFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
            OpenDll(path);
    }

    private void OpenDll(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"File not found: {path}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            _recentFiles.Remove(path);
            UpdateRecentFilesMenu();
            SaveRecentFiles();
            return;
        }
        LoadDll(path, showErrors: true);
        AddRecentFile(path);
    }

    private void CancelCurrentRequest()
    {
        _currentRequestCts?.Cancel();
        _currentRequestCts?.Dispose();
        _currentRequestCts = null;
    }

    private async void DllRoot_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { Tag: DllItem dll } root)
            return;
        var pe = dll.Pe;
        var token = dll.Cts.Token;

        // Only load once when the placeholder is present
        if (root.Items is not [TreeViewItem { Tag: "Loading" }])
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
        if (dlg.ShowDialog() == true) OpenDll(dlg.FileName);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && TryOpenFromClipboard())
            e.Handled = true;
    }

    private bool TryOpenFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (string file in Clipboard.GetFileDropList())
                {
                    if (IsDll(file))
                    {
                        LoadDll(file, showErrors: true);
                        return true;
                    }
                }
            }

            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim('"');
                if (IsDll(text))
                {
                    LoadDll(text, showErrors: true);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            ExceptionManager.Handle(ex);
        }

        return false;
    }

    private static bool IsDll(string path)
    {
        return File.Exists(path) &&
               string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var file in files)
            if (IsDll(file))
                LoadDll(file, showErrors: true);
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
                var hash = exp.Dll.FileHash;
                var cached = DecompiledCodeCache.TryGet(hash, exp.Name);
                if (cached != null)
                {
                    OutputBox.Text = cached;
                    return;
                }
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
                        var forwarderText = $"{name} -> {export.ForwarderString}";
                        DecompiledCodeCache.Save(hash, name, forwarderText);
                        OutputBox.Text = forwarderText;
                        return;
                    }

                    var code = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        int off = pe2.RvaToOffsetChecked(export.FunctionRva);
                        int maxLen = Math.Min(AppConfig.Current.MaxDataSizeBytes, pe2.Data.Length - off);
                        var engine = new Engine();
                        return engine.ToPseudoCode(pe2.Data.AsMemory(off, maxLen), new Engine.Options
                        {
                            BaseAddress = pe2.ImageBase + export.FunctionRva,
                            FunctionName = name
                        });
                    }, token);

                    if (_provider != null && AppConfig.Current.MaxLlmCodeLength > 0 && code.Length > AppConfig.Current.MaxLlmCodeLength)
                        code = code[..AppConfig.Current.MaxLlmCodeLength];
                    string output = code;
                    OutputBox.Text = output;
                    if (_provider != null)
                        output = await _provider.RefineAsync(code, null, token);
                    DecompiledCodeCache.Save(hash, name, output);
                    OutputBox.Text = output;
                }
                catch (OperationCanceledException ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        OutputBox.Text = $"Operation canceled: {ex.Message}";
                        ExceptionManager.Handle(ex);
                    }
                }
                catch (Exception ex)
                {
                    OutputBox.Text = $"Error: {ex.Message}";
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
        while (ItemsControl.ItemsControlFromItemContainer(item) is TreeViewItem parent)
            item = parent;
        return item;
    }

    private void DllTree_KeyDown(object sender, KeyEventArgs e)
    {
        // Delete: remove the selected DLL (or an export under it)
        if (e.Key == Key.Delete)
        {
            if (DllTree.SelectedItem is not TreeViewItem item)
                return;

            TreeViewItem root;
            DllItem dll;
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
            return;
        }

        // Type-to-search: select first export under the current DLL whose name starts with the typed prefix
        var ch = KeyToChar(e.Key);
        if (ch is null)
            return;

        var now = DateTime.UtcNow;
        if ((now - _lastKeyTime).TotalSeconds > 1)
            _searchText = string.Empty;
        _lastKeyTime = now;
        _searchText += ch.Value;

        TreeViewItem? rootItem = null;
        if (DllTree.SelectedItem is TreeViewItem current)
            rootItem = GetRootItem(current);
        else if (DllTree.Items.Count > 0)
            rootItem = DllTree.Items[0] as TreeViewItem;

        if (rootItem is null)
            return;

        var match = rootItem.Items
            .OfType<TreeViewItem>()
            .FirstOrDefault(t => t.Tag is ExportItem exp &&
                                 exp.Name.StartsWith(_searchText, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            match.IsSelected = true;
            match.BringIntoView();
        }

        e.Handled = true;
    }

    private static char? KeyToChar(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
            return (char)('a' + (key - Key.A));
        if (key >= Key.D0 && key <= Key.D9)
            return (char)('0' + (key - Key.D0));
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return (char)('0' + (key - Key.NumPad0));
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelCurrentRequest();
        // Dispose all DLL items to clean up CancellationTokenSource objects
        foreach (TreeViewItem item in DllTree.Items)
            if (item.Tag is DllItem dll)
                dll.Dispose();

        _provider?.Dispose();
        base.OnClosed(e);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutWindow { Owner = this };
        dlg.ShowDialog();
    }
}
