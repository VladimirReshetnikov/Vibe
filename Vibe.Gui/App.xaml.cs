using System.Windows;
using System.Windows.Threading;

namespace Vibe.Gui;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ExceptionManager.Handle(e.Exception);
        e.Handled = true;
    }
}
