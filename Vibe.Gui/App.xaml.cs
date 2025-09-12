using System;
using System.Windows;
using System.Windows.Threading;
using Vibe.Utils;

namespace Vibe.Gui;

/// <summary>
/// Application entry point for the WPF GUI. Handles startup tasks such as API
/// key acquisition and global exception logging.
/// </summary>
public partial class App : Application
{
    /// <summary>The API key used for optional LLM integration.</summary>
    public static string? ApiKey { get; private set; }
    public static WindowLogger WindowLogger { get; } = new();

    /// <inheritdoc />
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

    /// <summary>Initializes the application and wires global exception handlers.</summary>
    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        Logger.Instance = new CompositeLogger(Logger.Instance, WindowLogger);
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ExceptionManager.Handle(e.Exception);
        e.Handled = true;
    }
}
