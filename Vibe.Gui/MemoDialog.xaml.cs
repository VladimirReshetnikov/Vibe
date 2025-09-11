using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Vibe.Gui;

public partial class MemoDialog : Window
{
    /// <summary>
    /// Create the dialog.
    /// </summary>
    /// <param name="text">Text to display (can be long).</param>
    /// <param name="initialWidth">Initial window width in device-independent units.</param>
    /// <param name="initialHeight">Initial window height in device-independent units.</param>
    /// <param name="foreground">Text color (e.g., Brushes.Lime).</param>
    /// <param name="background">Background color (e.g., Brushes.Black).</param>
    public MemoDialog(string text,
        double initialWidth,
        double initialHeight,
        Brush foreground,
        Brush background)
    {
        InitializeComponent();

        // Title must be empty per spec
        Title = string.Empty;

        // Initial size provided by caller
        Width  = initialWidth;
        Height = initialHeight;

        // Content + colors provided by caller
        Memo.Text       = text ?? string.Empty;
        Memo.Foreground = foreground ?? SystemColors.ControlTextBrush;
        Memo.Background = background ?? SystemColors.WindowBrush;

        Loaded += (_, __) =>
        {
            // Put focus in the memo; start at the top
            Memo.Focus();
            Memo.CaretIndex = 0;
            Memo.ScrollToHome();
        };
    }

    // ESC (and ApplicationCommands.Close) -> close the dialog
    private void OnCloseCommand(object sender, ExecutedRoutedEventArgs e)
    {
        // ShowDialog() callers get 'true'; if used modelessly this will throw,
        // but the spec says we're using it as a modal dialog.
        DialogResult = true;
    }
}
