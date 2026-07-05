using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Services;

/// <summary><see cref="ILoggingBuilder"/> extension for registering the simple file logger.</summary>
public static class FileLoggerExtensions
{
    /// <summary>Adds a logger that appends each entry as a line to <paramref name="path"/>.</summary>
    public static ILoggingBuilder AddSimpleFileLogger(this ILoggingBuilder builder, string path)
    {
        builder.AddProvider(new FileLoggerProvider(path));
        return builder;
    }
}

/// <summary>Provides <see cref="FileLogger"/> instances that share a single append-mode writer.</summary>
internal sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly StreamWriter _writer = OpenWriter(path);
    private readonly Lock _lock = new();

    /// <summary>
    /// Opens the log for appending without ever letting a file-lock fail app startup.
    ///
    /// On resume-from-standby the ONLOGON autostart task can launch a new instance while the
    /// previous one is still shutting down, so both briefly hold the log. <see cref="FileShare.ReadWrite"/>
    /// lets two current-build instances share it. The retry rides out an OLD-build instance that
    /// opened the file with the framework default (deny-write) — that case can't be share-negotiated,
    /// so we wait for it to release. If the file still can't be opened (lingering lock, AV, denied
    /// permission), fall back to a no-op writer: losing this session's log is acceptable, crashing
    /// the app over a log file is not.
    /// </summary>
    // Retry budget for riding out a departing instance still holding the log (~1.5 s total).
    private const int OpenAttempts = 10;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(150);

    private static StreamWriter OpenWriter(string path)
    {
        for (int attempt = 0; attempt < OpenAttempts; attempt++)
        {
            try
            {
                return new StreamWriter(
                    new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    { AutoFlush = true };
            }
            catch (IOException)                  { Thread.Sleep(OpenRetryDelay); }  // sharing violation — retry
            catch (UnauthorizedAccessException)  { break; }                         // won't resolve by waiting
        }
        return StreamWriter.Null;  // logging disabled this session; app still starts
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _lock);

    public void Dispose() => _writer.Dispose();
}

/// <summary>Minimal <see cref="ILogger"/> that writes timestamped lines to a shared <see cref="StreamWriter"/>.</summary>
internal sealed class FileLogger(string category, StreamWriter writer, Lock lockObj) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    // Level filtering is owned by the LoggerFactory's configured minimum (SetMinimumLevel, driven
    // by config.json's logLevel). This provider writes whatever the factory lets through.
    public bool IsEnabled(LogLevel level) => level != LogLevel.None;

    public void Log<TState>(LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level,-11}] {category}: {formatter(state, exception)}";
        if (exception is not null) line += $"\n{exception}";
        lock (lockObj) writer.WriteLine(line);
    }
}
