using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    private const string RunFromCopySwitch = "--run-from-copy";
    private const string RunFromCopyEnv = "VIBE_GUI_RUNFROMCOPY";
    private const string TempMarkerEnv = "VIBE_GUI_TEMP";

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        var isTemp = Environment.GetEnvironmentVariable(TempMarkerEnv) == "1";
        var runFromCopy = Environment.GetEnvironmentVariable(RunFromCopyEnv) == "1" || e.Args.Contains(RunFromCopySwitch);

        if (runFromCopy && !isTemp)
        {
            var mainModule = Process.GetCurrentProcess().MainModule;
            var current = mainModule?.FileName;
            var directory = current != null ? Path.GetDirectoryName(current) : null;
            if (!string.IsNullOrEmpty(current) && !string.IsNullOrEmpty(directory))
            {
                var tempPath = Path.Combine(directory,
                    $"{Path.GetFileNameWithoutExtension(current)}-{Guid.NewGuid():N}{Path.GetExtension(current)}");
                File.Copy(current, tempPath, true);

                var psi = new ProcessStartInfo(tempPath)
                {
                    UseShellExecute = false
                };
                foreach (var arg in e.Args.Where(a => a != RunFromCopySwitch))
                {
                    psi.ArgumentList.Add(arg);
                }
                psi.EnvironmentVariables[TempMarkerEnv] = "1";
                Process.Start(psi);
                Shutdown();
                return;
            }
        }

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

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        if (Environment.GetEnvironmentVariable(TempMarkerEnv) == "1")
        {
            var mainModule = Process.GetCurrentProcess().MainModule;
            var current = mainModule?.FileName;
            if (!string.IsNullOrEmpty(current))
            {
                ScheduleSelfDeletion(current);
            }
        }

        base.OnExit(e);
    }

    private static void ScheduleSelfDeletion(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd",
                    $"/c ping 127.0.0.1 -n 2 > nul & del /f /q \"{path}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
        }
        catch
        {
            // ignore failures
        }
    }
}
