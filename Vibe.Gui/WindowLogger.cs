using System;
using System.Collections.ObjectModel;
using System.Windows;
using Vibe.Utils;

namespace Vibe.Gui;

/// <summary>
/// <see cref="ILogger"/> implementation that stores log messages for display
/// within the main application's docked log pane.
/// </summary>
public sealed class WindowLogger : ILogger
{
    /// <summary>Collection of log messages to be bound to the UI.</summary>
    public ObservableCollection<string> Messages { get; } = new();

    /// <inheritdoc />
    public void Log(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Messages.Add(message);
        });
    }

    /// <inheritdoc />
    public void LogException(Exception ex) => Log(ex.ToString());
}
