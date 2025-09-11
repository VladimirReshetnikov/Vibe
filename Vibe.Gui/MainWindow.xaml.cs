using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    private void LoadDll(string path, bool showErrors)
    {
        try
        {
            var pe = new PEReaderLite(path);

            var icon = (ImageSource)FindResource("DllIcon");
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0) });
            headerPanel.Children.Add(new TextBlock { Text = Path.GetFileName(path) });

            var root = new TreeViewItem { Header = headerPanel, Tag = pe };
            foreach (var name in pe.EnumerateExportNames().OrderBy(n => n))
            {
                root.Items.Add(new TreeViewItem { Header = name, Tag = new ExportItem { Pe = pe, Name = name } });
            }
            DllTree.Items.Add(root);
            root.IsExpanded = true;
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
            "gdi32.dll",
            "advapi32.dll",
            "shell32.dll",
            "ntdll.dll",
            "ole32.dll",
            "oleaut32.dll",
            "dbghelp.dll"
        };

        foreach (var name in dlls)
        {
            var path = Path.Combine(systemDir, name);
            if (File.Exists(path))
                LoadDll(path, showErrors: false);
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

    private void DllTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DllTree.SelectedItem is not TreeViewItem item)
            return;

        switch (item.Tag)
        {
            case PEReaderLite pe:
                OutputBox.Text = pe.GetSummary();
                return;
            case ExportItem exp:
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

                    int off = pe2.RvaToOffsetChecked(export.FunctionRva);
                    int maxLen = Math.Min(4096, pe2.Data.Length - off);
                    var bytes = new byte[maxLen];
                    Array.Copy(pe2.Data, off, bytes, 0, maxLen);
                    var engine = new Engine();
                    var code = engine.ToPseudoCode(bytes, new Engine.Options
                    {
                        BaseAddress = pe2.ImageBase + export.FunctionRva,
                        FunctionName = name
                    });
                    OutputBox.Text = code;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                break;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
