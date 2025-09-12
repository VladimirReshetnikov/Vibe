using System;
using System.Collections.ObjectModel;
using System.Windows;
using Vibe.Utils;

namespace Vibe.Gui;

/// <summary>
/// <see cref="ILogger"/> implementation that displays log messages in a window
/// within the WPF application.
/// </summary>
public sealed class WindowLogger : ILogger
{
    private readonly ObservableCollection<string> _messages = new();
    private LogWindow? _window;

    /// <inheritdoc />
    public void Log(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _messages.Add(message);
        });
    }

    /// <inheritdoc />
    public void LogException(Exception ex) => Log(ex.ToString());

    /// <summary>Shows the log window, creating it if necessary.</summary>
    public void Show()
    {
        Application.Current.Dispatcher.Invoke(EnsureWindow);
    }

    private void EnsureWindow()
    {
        if (_window == null)
        {
            _window = new LogWindow(_messages);
            _window.Closed += (_, _) => _window = null;
        }
        if (!_window.IsVisible)
            _window.Show();
        _window.Activate();
    }
}
