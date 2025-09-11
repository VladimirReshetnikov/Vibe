using System.Windows;

namespace Vibe.Gui;

public partial class MissingApiKeyWindow : Window
{
    public MissingApiKeyWindow()
    {
        InitializeComponent();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
