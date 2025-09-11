using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
            var root = new TreeViewItem { Header = Path.GetFileName(path), Tag = pe };
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
        if (DllTree.SelectedItem is not TreeViewItem item || item.Tag is not ExportItem exp)
            return;

        try
        {
            var pe = exp.Pe;
            var name = exp.Name;
            var export = pe.FindExport(name);
            if (export.IsForwarder)
            {
                OutputBox.Text = $"{name} -> {export.ForwarderString}";
                return;
            }

            int off = pe.RvaToOffsetChecked(export.FunctionRva);
            int maxLen = Math.Min(4096, pe.Data.Length - off);
            var bytes = new byte[maxLen];
            Array.Copy(pe.Data, off, bytes, 0, maxLen);
            var engine = new Engine();
            var code = engine.ToPseudoCode(bytes, new Engine.Options
            {
                BaseAddress = pe.ImageBase + export.FunctionRva,
                FunctionName = name
            });
            OutputBox.Text = code;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
