using System;
using System.Collections.ObjectModel;
using System.Windows;
using Vibe.Utils;

namespace Vibe.Gui;

public sealed class WindowLogger : ILogger
{
    private readonly ObservableCollection<string> _messages = new();
    public ObservableCollection<string> Messages => _messages;
    public Action? ShowLog { get; set; }

    public void Log(string message)
    {
        Application.Current.Dispatcher.Invoke(() => _messages.Add(message));
    }

    public void LogException(Exception ex) => Log(ex.ToString());

    public void Show()
    {
        Application.Current.Dispatcher.Invoke(() => ShowLog?.Invoke());
    }
}
