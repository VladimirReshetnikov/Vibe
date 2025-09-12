using System.Windows;

namespace Vibe.Gui;

/// <summary>
/// Modal dialog prompting the user to supply an OpenAI API key or continue
/// without one.
/// </summary>
public partial class MissingApiKeyWindow : Window
{
    /// <summary>Gets the API key entered by the user, if any.</summary>
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
