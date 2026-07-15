using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Services;

/// <summary>
/// A live, mutable minimum log level shared by the file logger and consulted on <em>every</em> write,
/// so a level change (from Settings or a config.json edit) takes effect immediately with no restart
/// (issue #22). The factory's own minimum is pinned to <see cref="LogLevel.Trace"/> so it never
/// pre-filters ahead of this switch; this switch is the single runtime gate for switcher.log,
/// vm-power.log and ui.log alike. Setting it to <see cref="LogLevel.None"/> silences all three.
/// </summary>
public sealed class LogLevelSwitch(LogLevel initial)
{
    // Enum with an int backing is a valid volatile type; reads/writes cross threads (UI sets it,
    // background log-writer threads read it) without tearing.
    private volatile LogLevel _minimum = initial;

    /// <summary>The current minimum level. Anything below it (or <see cref="LogLevel.None"/>) is dropped.</summary>
    public LogLevel MinimumLevel
    {
        get => _minimum;
        set => _minimum = value;
    }

    /// <summary>True when <paramref name="level"/> should be written at the current minimum.</summary>
    public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimum;
}

/// <summary><see cref="ILoggingBuilder"/> extension for registering the category-routing file logger.</summary>
public static class FileLoggerExtensions
{
    /// <summary>
    /// Adds a logger that appends each entry as a line to a file chosen by the logger's category name:
    /// a category listed in <paramref name="categoryPaths"/> goes to its dedicated file, everything
    /// else goes to <paramref name="defaultPath"/>. Categories sharing a path share one writer.
    /// </summary>
    /// <param name="defaultPath">The catch-all log (e.g. switcher.log) for un-routed categories.</param>
    /// <param name="categoryPaths">Exact-category-name → dedicated-file map (e.g. "vm-power" → vm-power.log).</param>
    /// <param name="levelSwitch">
    /// Optional live minimum-level gate consulted per write (issue #22). When null, every level except
    /// <see cref="LogLevel.None"/> is written and filtering is left to the LoggerFactory minimum.
    /// </param>
    public static ILoggingBuilder AddSimpleFileLogger(
        this ILoggingBuilder builder,
        string defaultPath,
        IReadOnlyDictionary<string, string>? categoryPaths = null,
        LogLevelSwitch? levelSwitch = null)
    {
        builder.AddProvider(new FileLoggerProvider(defaultPath, categoryPaths, levelSwitch));
        return builder;
    }
}

/// <summary>
/// Routes each logger category to a file and hands out <see cref="FileLogger"/> instances that append
/// to it. One shared append-mode <see cref="StreamWriter"/> is kept per distinct file path (categories
/// mapped to the same path share a writer); a single lock serialises all writes across every file.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _defaultPath;
    private readonly IReadOnlyDictionary<string, string> _categoryPaths;
    private readonly LogLevelSwitch? _levelSwitch;
    // One writer per distinct destination path (default + each dedicated category file).
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public FileLoggerProvider(
        string defaultPath,
        IReadOnlyDictionary<string, string>? categoryPaths = null,
        LogLevelSwitch? levelSwitch = null)
    {
        _defaultPath   = defaultPath;
        _categoryPaths = categoryPaths ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _levelSwitch   = levelSwitch;

        // Open every sink up front (construction is single-threaded) so a category's first log line
        // never pays the open cost, and a bad path fails over to the null writer here, not mid-run.
        OpenIfNeeded(_defaultPath);
        foreach (var path in _categoryPaths.Values) OpenIfNeeded(path);
    }

    // Caller must hold _lock (or be in the constructor, which is single-threaded).
    private StreamWriter OpenIfNeeded(string path)
    {
        if (!_writers.TryGetValue(path, out var writer))
        {
            writer = OpenWriter(path);
            _writers[path] = writer;
        }
        return writer;
    }

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

    public ILogger CreateLogger(string categoryName)
    {
        var path = _categoryPaths.TryGetValue(categoryName, out var mapped) ? mapped : _defaultPath;
        StreamWriter writer;
        lock (_lock) writer = OpenIfNeeded(path);
        return new FileLogger(categoryName, writer, _lock, _levelSwitch);
    }

    public void Dispose()
    {
        lock (_lock)
            foreach (var writer in _writers.Values) writer.Dispose();
    }
}

/// <summary>Minimal <see cref="ILogger"/> that writes timestamped lines to a shared <see cref="StreamWriter"/>.</summary>
internal sealed class FileLogger(string category, StreamWriter writer, Lock lockObj, LogLevelSwitch? levelSwitch) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    // Level filtering: when a live LogLevelSwitch is supplied it is the runtime gate (issue #22),
    // consulted per write so a Settings/config change takes effect with no restart. Without one,
    // filtering is left to the LoggerFactory's configured minimum and everything but None passes.
    public bool IsEnabled(LogLevel level) =>
        levelSwitch is null ? level != LogLevel.None : levelSwitch.IsEnabled(level);

    public void Log<TState>(LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level,-11}] {category}: {formatter(state, exception)}";
        if (exception is not null) line += $"\n{exception}";
        lock (lockObj) writer.WriteLine(line);
    }
}
