using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Windows.Input;
using Microsoft.Win32;
using ICSharpCode.AvalonEdit;
using Mono.Cecil;
using Vibe.Decompiler;
using Xceed.Wpf.AvalonDock.Layout;
using Xceed.Wpf.AvalonDock.Layout.Serialization;

namespace Vibe.Gui;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand ToggleExplorerCommand = new("Explorer", nameof(ToggleExplorerCommand), typeof(MainWindow), new InputGestureCollection { new KeyGesture(Key.E, ModifierKeys.Control | ModifierKeys.Alt) });
    public static readonly RoutedUICommand ToggleOutputCommand = new("Output", nameof(ToggleOutputCommand), typeof(MainWindow), new InputGestureCollection { new KeyGesture(Key.O, ModifierKeys.Control | ModifierKeys.Alt) });
    public static readonly RoutedUICommand ToggleSearchCommand = new("Search Results", nameof(ToggleSearchCommand), typeof(MainWindow), new InputGestureCollection { new KeyGesture(Key.S, ModifierKeys.Control | ModifierKeys.Alt) });
    public static readonly RoutedUICommand ResetLayoutCommand = new("Reset Window Layout", nameof(ResetLayoutCommand), typeof(MainWindow), new InputGestureCollection { new KeyGesture(Key.R, ModifierKeys.Control | ModifierKeys.Alt) });

    private readonly string _layoutFile;
    private readonly Grid _decompilerContent;
    private readonly TextEditor OutputBox;
    private readonly Border RewriteOverlay;
    private readonly TextBox _outputLog;
    private readonly ListBox _searchResults;
    private readonly TreeView DllTree;
    private readonly DllAnalyzer _dllAnalyzer;
    // Quick-search state (type-to-select export by prefix)
    private string _searchText = string.Empty;
    private DateTime _lastKeyTime;
    // Cancellation for the currently running decompile/refine task
    private CancellationTokenSource? _currentRequestCts;
    private const string RecentFilesKey = @"Software\\Vibe\\RecentFiles";
    private const string OpenDllsKey = @"Software\\Vibe\\OpenDlls";
    private readonly List<string> _recentFiles;

    private sealed class ExportItem
    {
        public required LoadedDll Dll { get; init; }
        public required string Name { get; init; }
    }

    private void ShowLlmOverlay()
    {
        RewriteOverlay.Opacity = 1;
        RewriteOverlay.Visibility = Visibility.Visible;
        OutputBox.Opacity = 0.5;
        OutputBox.Effect = new BlurEffect { Radius = 1 };
        if (FindResource("StripeAnimation") is Storyboard sb)
            sb.Begin(RewriteOverlay, true);
    }

    private void HideLlmOverlay()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
        fade.Completed += (_, _) => RewriteOverlay.Visibility = Visibility.Collapsed;
        RewriteOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
        OutputBox.Opacity = 1;
        OutputBox.Effect = null;
        if (FindResource("StripeAnimation") is Storyboard sb)
            sb.Stop(RewriteOverlay);
    }

    public MainWindow()
    {
        InitializeComponent();

        DllTree = (TreeView)FindResource("ExplorerControl");
        _decompilerContent = (Grid)FindResource("DecompilerContent");
        OutputBox = (TextEditor)_decompilerContent.Children[0];
        RewriteOverlay = (Border)_decompilerContent.Children[1];
        _outputLog = (TextBox)FindResource("OutputControl");
        _searchResults = (ListBox)FindResource("SearchResultsControl");

        CommandBindings.Add(new CommandBinding(ToggleExplorerCommand, (_, _) => ToggleAnchorable("Explorer")));
        CommandBindings.Add(new CommandBinding(ToggleOutputCommand, (_, _) => ToggleAnchorable("Output")));
        CommandBindings.Add(new CommandBinding(ToggleSearchCommand, (_, _) => ToggleAnchorable("SearchResults")));
        CommandBindings.Add(new CommandBinding(ResetLayoutCommand, (_, _) => ResetLayout()));

        OutputBox.TextArea.TextView.LineTransformers.Add(new PseudoCodeColorizer());

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vibe");
        Directory.CreateDirectory(appData);
        _layoutFile = Path.Combine(appData, "layout.config");

        _recentFiles = LoadRecentFiles();
        _dllAnalyzer = new DllAnalyzer();

        LoadLayout();

        var opened = LoadOpenDlls();
        if (opened.Count > 0)
        {
            foreach (var path in opened)
                LoadDll(path, showErrors: false);
        }
        else
        {
            LoadCommonDlls();
        }
        UpdateRecentFilesMenu();

        Closing += (_, _) => SaveLayout();
    }

    private void LoadLayout()
    {
        if (File.Exists(_layoutFile))
        {
            try
            {
                var serializer = new XmlLayoutSerializer(DockManager);
                serializer.LayoutSerializationCallback += Serializer_LayoutSerializationCallback;
                using var reader = new StreamReader(_layoutFile);
                serializer.Deserialize(reader);
                return;
            }
            catch
            {
            }
        }
        ResetLayout();
    }

    private void SaveLayout()
    {
        var serializer = new XmlLayoutSerializer(DockManager);
        using var writer = new StreamWriter(_layoutFile);
        serializer.Serialize(writer);
    }

    private void ResetLayout()
    {
        var serializer = new XmlLayoutSerializer(DockManager);
        serializer.LayoutSerializationCallback += Serializer_LayoutSerializationCallback;
        using var stream = Application.GetResourceStream(new Uri("DefaultLayout.config", UriKind.Relative))?.Stream;
        if (stream != null)
            serializer.Deserialize(stream);
    }

    private void Serializer_LayoutSerializationCallback(object? sender, LayoutSerializationCallbackEventArgs e)
    {
        switch (e.Model.ContentId)
        {
            case "Explorer":
                e.Content = DllTree;
                break;
            case "DecompilerView":
                e.Content = _decompilerContent;
                break;
            case "Output":
                e.Content = _outputLog;
                break;
            case "SearchResults":
                e.Content = _searchResults;
                break;
        }
    }

    private void ToggleAnchorable(string id)
    {
        var anchor = DockManager.Layout?.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == id);
        if (anchor == null)
            return;
        if (anchor.IsHidden || anchor.IsAutoHidden)
            anchor.Show();
        else
            anchor.Hide();
    }

    private void LoadDll(string path, bool showErrors)
    {
        try
        {
            var dll = _dllAnalyzer.Load(path);

            var dllIcon = (ImageSource)FindResource("DllIconImage");
            var root = CreateTreeViewItemWithIcon(Path.GetFileName(path), dllIcon, dll);
            // Add a dummy child so the expand arrow appears and load exports on demand
            root.Items.Add(new TreeViewItem { Header = "Loading...", Tag = "Loading" });
            root.Expanded += DllRoot_Expanded;
            DllTree.Items.Add(root);
            root.IsExpanded = false;
            SaveOpenDlls();
        }
        catch (Exception ex)
        {
            if (showErrors)
                ExceptionManager.Handle(ex);
        }
    }

    private TreeViewItem CreateTreeViewItemWithIcon(string text, ImageSource icon, object tag)
    {
        var textBlock = new TextBlock { Text = text };
        var border = new Border { Padding = new Thickness(1, 0, 1, 0), Child = textBlock };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0) });
        panel.Children.Add(border);

        var item = new TreeViewItem { Header = panel, Tag = tag };

        // Remove default highlight so only the text background changes when selected
        var textBrush = (Brush)FindResource("TextBrush");
        item.Resources[SystemColors.HighlightBrushKey] = Brushes.Transparent;
        item.Resources[SystemColors.ControlBrushKey] = Brushes.Transparent;
        item.Resources[SystemColors.HighlightTextBrushKey] = textBrush;
        item.Resources[SystemColors.ControlTextBrushKey] = textBrush;

        var accentBrush = (Brush)FindResource("AccentBrush");
        var accentTextBrush = new SolidColorBrush((Color)FindResource("Color.Foreground.OnAccent"));
        item.Selected += (_, _) =>
        {
            border.Background = accentBrush;
            textBlock.Foreground = accentTextBrush;
        };
        item.Unselected += (_, _) =>
        {
            border.Background = Brushes.Transparent;
            textBlock.Foreground = textBrush;
        };

        return item;
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

    private List<string> LoadOpenDlls()
    {
        var list = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(OpenDllsKey);
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

    private void SaveOpenDlls()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(OpenDllsKey);
            if (key != null)
            {
                foreach (var name in key.GetValueNames())
                    key.DeleteValue(name, false);
                int i = 0;
                foreach (TreeViewItem item in DllTree.Items)
                    if (item.Tag is LoadedDll dll)
                        key.SetValue($"File{i++}", dll.Pe.FilePath);
            }
        }
        catch
        {
        }
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
        SaveOpenDlls();
    }

    private void CancelCurrentRequest()
    {
        _currentRequestCts?.Cancel();
        _currentRequestCts?.Dispose();
        _currentRequestCts = null;

        BusyBar.Visibility = Visibility.Collapsed;
        HideLlmOverlay();
    }

    private async void DllRoot_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { Tag: LoadedDll dll } root)
            return;
        var token = dll.Cts.Token;

        // Only load once when the placeholder is present
        if (root.Items is not [TreeViewItem { Tag: "Loading" }])
            return;

        root.Items.Clear();
        var funcIcon = (ImageSource)FindResource("ExportedFunctionIconImage");
        try
        {
            if (dll.IsManaged)
            {
                var types = await _dllAnalyzer.GetManagedTypesAsync(dll, token);
                foreach (var type in types)
                {
                    token.ThrowIfCancellationRequested();
                    var typeItem = new TreeViewItem { Header = type.FullName, Tag = type };
                    foreach (var method in type.Methods)
                    {
                        var methodItem = CreateTreeViewItemWithIcon(method.Name, funcIcon, method);
                        typeItem.Items.Add(methodItem);
                    }
                    root.Items.Add(typeItem);
                    await Dispatcher.Yield();
                }
            }
            else
            {
                var names = await _dllAnalyzer.GetExportNamesAsync(dll, token);

                int i = 0;
                foreach (var name in names)
                {
                    token.ThrowIfCancellationRequested();
                    var funcItem = CreateTreeViewItemWithIcon(name, funcIcon, new ExportItem { Dll = dll, Name = name });
                    root.Items.Add(funcItem);

                    if (++i % 20 == 0)
                        await Dispatcher.Yield();
                }
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
                        OpenDll(file);
                        return true;
                    }
                }
            }

            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim('"');
                if (IsDll(text))
                {
                    OpenDll(text);
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
                OpenDll(file);
    }

    private async void DllTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        CancelCurrentRequest();

        if (DllTree.SelectedItem is not TreeViewItem item)
            return;

        switch (item.Tag)
        {
            case LoadedDll dll:
                OutputBox.Text = _dllAnalyzer.GetSummary(dll);
                return;
            case ExportItem exp:
                OutputBox.Text = string.Empty;
                BusyBar.Visibility = Visibility.Visible;
                var dllItem = exp.Dll;
                _currentRequestCts = CancellationTokenSource.CreateLinkedTokenSource(dllItem.Cts.Token);
                var token = _currentRequestCts.Token;
                try
                {
                    var progress = new Progress<string>(t =>
                    {
                        OutputBox.Text = t;
                        if (_dllAnalyzer.HasLlmProvider)
                            ShowLlmOverlay();
                    });
                    var output = await _dllAnalyzer.GetDecompiledExportAsync(dllItem, exp.Name, progress, token);
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
                        CancelCurrentRequest();
                }
                break;
            case TypeDefinition td:
                OutputBox.Text = $"Type: {td.FullName}";
                return;
            case MethodDefinition md:
                OutputBox.Text = _dllAnalyzer.GetManagedMethodBody(md);
                return;
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
            LoadedDll dll;
            switch (item.Tag)
            {
                case LoadedDll di:
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
            SaveOpenDlls();
            UpdateRecentFilesMenu();
            CancelCurrentRequest();
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
        SaveOpenDlls();
        // Dispose all DLL items to clean up CancellationTokenSource objects
        foreach (TreeViewItem item in DllTree.Items)
            if (item.Tag is LoadedDll dll)
                dll.Dispose();

        _dllAnalyzer.Dispose();
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
