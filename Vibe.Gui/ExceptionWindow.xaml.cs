using System.Collections.ObjectModel;
using System.Windows;

namespace Vibe.Gui;

public partial class ExceptionWindow : Window
{
    public ExceptionWindow(ObservableCollection<string> exceptions)
    {
        InitializeComponent();
        DataContext = exceptions;
    }
}
