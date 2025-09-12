using System;
using System.IO;

namespace Vibe.Utils;

/// <summary>
/// Defines the basic logging operations used throughout the application.
/// Implementations can forward messages to files, windows or other sinks.
/// </summary>
public interface ILogger
{
    /// <summary>Writes an informational message to the log.</summary>
    /// <param name="message">The message to record.</param>
    void Log(string message);

    /// <summary>Records an exception with its full stack trace.</summary>
    /// <param name="ex">The exception to log.</param>
    void LogException(Exception ex);
}

/// <summary>
/// Simple logger that appends all messages to a text file on disk.
/// The file path defaults to <c>vibe.log</c> under the application's
/// base directory but can be overridden via the constructor.
/// </summary>
public sealed class FileLogger : ILogger
{
    private readonly object _lock = new();
    private readonly string _path;

    /// <summary>
    /// Creates a new file logger.
    /// </summary>
    /// <param name="path">
    /// Optional path to the log file. When omitted the log is written to
    /// <c>vibe.log</c> next to the executable.
    /// </param>
    public FileLogger(string? path = null)
    {
        _path = path ?? Path.Combine(AppContext.BaseDirectory, "vibe.log");
    }

    /// <inheritdoc />
    public void Log(string message)
    {
        lock (_lock)
        {
            File.AppendAllText(_path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
    }

    /// <inheritdoc />
    public void LogException(Exception ex) => Log(ex.ToString());
}

/// <summary>
/// Logger implementation that silently discards all messages.
/// Useful when logging is optional or needs to be suppressed.
/// </summary>
public sealed class NullLogger : ILogger
{
    /// <inheritdoc />
    public void Log(string message) { }

    /// <inheritdoc />
    public void LogException(Exception ex) { }
}

/// <summary>
/// Dispatches log messages to multiple child loggers. Failures in one
/// logger do not prevent others from receiving the message.
/// </summary>
public sealed class CompositeLogger : ILogger
{
    private readonly ILogger[] _loggers;
    /// <summary>
    /// Creates a composite logger that forwards to the specified list of loggers.
    /// </summary>
    public CompositeLogger(params ILogger[] loggers) => _loggers = loggers;

    /// <inheritdoc />
    public void Log(string message)
    {
        foreach (var logger in _loggers)
            try { logger.Log(message); } catch { }
    }
    /// <inheritdoc />
    public void LogException(Exception ex)
    {
        foreach (var logger in _loggers)
            try { logger.LogException(ex); } catch { }
    }
}

/// <summary>
/// Convenience wrapper around a globally accessible <see cref="ILogger"/> instance.
/// </summary>
public static class Logger
{
    /// <summary>Gets or sets the active logger. Defaults to a <see cref="FileLogger"/>.</summary>
    public static ILogger Instance { get; set; } = new FileLogger();

    /// <summary>Writes an informational message using the current <see cref="Instance"/>.</summary>
    public static void Log(string message)
    {
        try { Instance.Log(message); } catch { }
    }

    /// <summary>Writes an exception using the current <see cref="Instance"/>.</summary>
    public static void LogException(Exception ex)
    {
        try { Instance.LogException(ex); } catch { }
    }
}

