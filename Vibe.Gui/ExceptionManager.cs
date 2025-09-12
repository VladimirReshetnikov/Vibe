using System;
using System.Collections.ObjectModel;
using System.Windows;
using Vibe.Utils;

namespace Vibe.Gui;

public static class ExceptionManager
{
    private static readonly ObservableCollection<string> _exceptions = new();
    private static ExceptionWindow? _window;

    public static void Handle(Exception ex)
    {
        Logger.LogException(ex);
        Application.Current.Dispatcher.Invoke(() =>
        {
            _exceptions.Add(ex.ToString());
            if (_window == null)
            {
                _window = new ExceptionWindow(_exceptions);
                _window.Closed += (_, _) => _window = null;
                _window.Show();
            }
        });
    }
}
