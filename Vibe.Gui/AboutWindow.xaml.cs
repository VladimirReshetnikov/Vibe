using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;
using Vibe.Utils;

namespace Vibe.Gui;

/// <summary>
/// Displays application version information and links to configuration files
/// and project resources.
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>Initializes the window and populates product and version labels.</summary>
    public AboutWindow()
    {
        InitializeComponent();

        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var name = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
                   ?? assembly.GetName().Name;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? assembly.GetName().Version?.ToString();

        ProgramName.Text = name ?? "";
        VersionText.Text = $"Version {version}";
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ConfigHyperlink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            var text = File.ReadAllText(path);
            var dialog = new MemoDialog(
                text,
                800,
                600,
                Brushes.Black,
                new SolidColorBrush(Color.FromRgb(255, 255, 240)))
            {
                Owner = this
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
            MessageBox.Show(this, $"Unable to open config.json: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        e.Handled = true;
    }
}

