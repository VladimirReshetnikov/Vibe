using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Threading.Tasks;
using Vibe.Utils;

namespace Vibe.Gui;

/// <summary>
/// Centralised exception handling for the GUI. Exceptions are logged and stored
/// so that they can be displayed to the user on demand.
/// </summary>
public static class ExceptionManager
{
    private static readonly ObservableCollection<string> _exceptions = new();
    /// <summary>Collection bound to the exception pane in the UI.</summary>
    public static ObservableCollection<string> Exceptions => _exceptions;
    /// <summary>Optional callback used to bring the exception window to front.</summary>
    public static Action? ShowExceptions { get; set; }

    /// <summary>
    /// Logs and records an exception unless it is considered ignorable.
    /// </summary>
    public static void Handle(Exception ex)
    {
        if (IsIgnorable(ex))
            return;

        Logger.LogException(ex);
        Application.Current.Dispatcher.Invoke(() =>
        {
            _exceptions.Add(ex.ToString());
            ShowExceptions?.Invoke();
        });
    }

    /// <summary>
    /// Determines whether the exception represents a normal cancellation scenario
    /// that does not need to be surfaced to the user.
    /// </summary>
    private static bool IsIgnorable(Exception ex) =>
        ex is TaskCanceledException ||
        ex is AggregateException agg && agg.InnerExceptions.All(IsIgnorable);
}
