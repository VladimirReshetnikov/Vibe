using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Vibe.Gui;

public partial class ExceptionWindow : Window
{
    public ExceptionWindow(ObservableCollection<string> exceptions)
    {
        InitializeComponent();
        DataContext = exceptions;
    }

    private void ExceptionsList_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            var selectedItems = ExceptionsList.SelectedItems.Cast<string>();
            if (selectedItems.Any())
            {
                Clipboard.SetText(string.Join(Environment.NewLine, selectedItems));
                e.Handled = true;
            }
        }
    }
}
