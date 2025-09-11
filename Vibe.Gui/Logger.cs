using System;
using System.IO;

namespace Vibe.Gui;

public static class Logger
{
    private static readonly object _lock = new();
    private static readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "vibe.log");

    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_logPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Suppress all logging failures to avoid recursive exceptions
        }
    }

    public static void LogException(Exception ex)
    {
        Log(ex.ToString());
    }
}
