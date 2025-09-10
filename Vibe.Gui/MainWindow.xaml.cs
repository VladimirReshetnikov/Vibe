using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Vibe.Decompiler;

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
    }

    private void OpenDll_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "DLL files (*.dll)|*.dll|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var pe = new PEReaderLite(dlg.FileName);
                var root = new TreeViewItem { Header = System.IO.Path.GetFileName(dlg.FileName), Tag = pe };
                foreach (var name in pe.EnumerateExportNames().OrderBy(n => n))
                {
                    root.Items.Add(new TreeViewItem { Header = name, Tag = new ExportItem { Pe = pe, Name = name } });
                }
                DllTree.Items.Add(root);
                root.IsExpanded = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void DllTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DllTree.SelectedItem is not TreeViewItem item)
            return;

        if (item.Tag is not ExportItem exp)
        {
            OutputBox.Text = "Select an export function to decompile.";
            return;
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;

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
            if (maxLen <= 0)
            {
                OutputBox.Text = $"No data available at RVA 0x{export.FunctionRva:X}";
                return;
            }
            var bytes = new byte[maxLen];
            Array.Copy(pe.Data, off, bytes, 0, maxLen);
            var engine = new Engine();
            OutputBox.Text = "Decompiling...";
            var code = await Task.Run(() => engine.ToPseudoCode(bytes, new Engine.Options
            {
                BaseAddress = pe.ImageBase + export.FunctionRva,
                FunctionName = name
            }));
            OutputBox.Text = code;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
