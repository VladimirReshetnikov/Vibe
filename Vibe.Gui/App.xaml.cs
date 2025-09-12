using System;
using System.Windows;
using System.Windows.Threading;
using Vibe.Utils;

namespace Vibe.Gui;

public partial class App : Application
{
    public static string? ApiKey { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var envApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(envApiKey))
        {
            var dlg = new MissingApiKeyWindow();
            if (dlg.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            ApiKey = dlg.ApiKey;
        }
        else
        {
            ApiKey = envApiKey;
        }

        base.OnStartup(e);
    }

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        Logger.Instance = new CompositeLogger(Logger.Instance, new WindowLogger());
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ExceptionManager.Handle(e.Exception);
        e.Handled = true;
    }
}
