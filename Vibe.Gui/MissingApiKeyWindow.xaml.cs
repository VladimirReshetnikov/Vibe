using System.Windows;

namespace Vibe.Gui;

public partial class MissingApiKeyWindow : Window
{
    public string? ApiKey { get; private set; }

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

    private void UseKey_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show("Please enter an API key or choose Ignore.", "Invalid key", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApiKey = key;
        DialogResult = true;
    }
}
