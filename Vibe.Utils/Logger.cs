using System;
using System.IO;

namespace Vibe.Utils;

public interface ILogger
{
    void Log(string message);
    void LogException(Exception ex);
}

public sealed class FileLogger : ILogger
{
    private readonly object _lock = new();
    private readonly string _path;

    public FileLogger(string? path = null)
    {
        _path = path ?? Path.Combine(AppContext.BaseDirectory, "vibe.log");
    }

    public void Log(string message)
    {
        lock (_lock)
        {
            File.AppendAllText(_path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
    }

    public void LogException(Exception ex) => Log(ex.ToString());
}

public sealed class NullLogger : ILogger
{
    public void Log(string message) { }
    public void LogException(Exception ex) { }
}

public sealed class CompositeLogger : ILogger
{
    private readonly ILogger[] _loggers;
    public CompositeLogger(params ILogger[] loggers) => _loggers = loggers;
    public void Log(string message)
    {
        foreach (var logger in _loggers)
            try { logger.Log(message); } catch { }
    }
    public void LogException(Exception ex)
    {
        foreach (var logger in _loggers)
            try { logger.LogException(ex); } catch { }
    }
}

public static class Logger
{
    public static ILogger Instance { get; set; } = new FileLogger();

    public static void Log(string message)
    {
        try { Instance.Log(message); } catch { }
    }

    public static void LogException(Exception ex)
    {
        try { Instance.LogException(ex); } catch { }
    }
}

