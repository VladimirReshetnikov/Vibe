using System.Collections.ObjectModel;
using System.Windows;

namespace Vibe.Gui;

public partial class LogWindow : Window
{
    public LogWindow(ObservableCollection<string> messages)
    {
        InitializeComponent();
        DataContext = messages;
    }
}
