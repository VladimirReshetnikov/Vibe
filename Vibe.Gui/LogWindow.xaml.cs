using System.Collections.ObjectModel;
using System.Windows;

namespace Vibe.Gui;

/// <summary>
/// Simple window that displays log messages produced during the session.
/// </summary>
public partial class LogWindow : Window
{
    /// <summary>Creates the window bound to the provided message collection.</summary>
    public LogWindow(ObservableCollection<string> messages)
    {
        InitializeComponent();
        DataContext = messages;
    }
}
