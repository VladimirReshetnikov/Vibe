using System;
using System.Collections.ObjectModel;
using System.Windows;
using Vibe.Utils;

namespace Vibe.Gui;

public static class ExceptionManager
{
    private static readonly ObservableCollection<string> _exceptions = new();
    public static ObservableCollection<string> Exceptions => _exceptions;
    public static Action? ShowExceptions { get; set; }

    public static void Handle(Exception ex)
    {
        Logger.LogException(ex);
        Application.Current.Dispatcher.Invoke(() =>
        {
            _exceptions.Add(ex.ToString());
            ShowExceptions?.Invoke();
        });
    }
}
