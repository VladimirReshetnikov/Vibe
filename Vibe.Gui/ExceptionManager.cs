using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Threading.Tasks;
using Vibe.Utils;

namespace Vibe.Gui;

public static class ExceptionManager
{
    private static readonly ObservableCollection<string> _exceptions = new();
    public static ObservableCollection<string> Exceptions => _exceptions;
    public static Action? ShowExceptions { get; set; }

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

    private static bool IsIgnorable(Exception ex) =>
        ex is TaskCanceledException ||
        ex is AggregateException agg && agg.InnerExceptions.All(IsIgnorable);
}
