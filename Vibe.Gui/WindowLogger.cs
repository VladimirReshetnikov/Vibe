using System;
using System.Collections.ObjectModel;
using System.Windows;
using Vibe.Utils;

namespace Vibe.Gui;

public sealed class WindowLogger : ILogger
{
    private readonly ObservableCollection<string> _messages = new();
    private LogWindow? _window;

    public void Log(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _messages.Add(message);
            EnsureWindow();
        });
    }

    public void LogException(Exception ex) => Log(ex.ToString());

    private void EnsureWindow()
    {
        if (_window == null)
        {
            _window = new LogWindow(_messages);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
    }
}
