using System;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using Vibe.Decompiler;

namespace Vibe.Gui;

public partial class MainWindow : Window
{
    private PEReaderLite? _pe;

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
                _pe = new PEReaderLite(dlg.FileName);
                var names = _pe.EnumerateExportNames().OrderBy(n => n).ToList();
                ExportList.ItemsSource = names;
                OutputBox.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void DecompileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pe == null)
        {
            MessageBox.Show(this, "Please open a DLL file first.", "No File Loaded", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ExportList.SelectedItem is not string name)
        {
            MessageBox.Show(this, "Please select an export to decompile.", "No Export Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var exp = _pe.FindExport(name);
            if (exp.IsForwarder)
            {
                OutputBox.Text = $"{name} -> {exp.ForwarderString}";
                return;
            }

            int off = _pe.RvaToOffsetChecked(exp.FunctionRva);
            int maxLen = Math.Min(4096, _pe.Data.Length - off);
            var bytes = new byte[maxLen];
            Array.Copy(_pe.Data, off, bytes, 0, maxLen);
            var engine = new Engine();
            var code = engine.ToPseudoCode(bytes, new Engine.Options
            {
                BaseAddress = _pe.ImageBase + exp.FunctionRva,
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
