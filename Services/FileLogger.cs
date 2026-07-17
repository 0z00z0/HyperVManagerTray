using Microsoft.Extensions.Logging;
using NLog.Config;
using NLog.Targets;

// NLog's own ILogger/LogLevel/Logger collide head-on with Microsoft.Extensions.Logging's. This file
// is a bridge between the two, so it names both: MEL wins the unqualified names (it is the interface
// the whole app codes against) and NLog is reached through this alias.
using Nlog = NLog;

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
    /// Each file rotates at <see cref="FileLoggerProvider.ArchiveAboveSizeBytes"/> (issue #55).
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
/// to it, with NLog as the file sink (issue #55).
///
/// <para><b>What NLog took over.</b> The write path only: opening, appending, flushing, locking and —
/// the point of the exercise — <b>rotation</b>, which the hand-rolled <c>StreamWriter</c> never had, so
/// all three logs grew without bound. NLog's <c>FileTarget</c> does this as configuration
/// (<see cref="FileTarget.ArchiveAboveSize"/> / <see cref="FileTarget.MaxArchiveFiles"/>) rather than as
/// code we maintain.</para>
///
/// <para><b>What deliberately stayed ours.</b> Category routing (#20/#21) and the live
/// <see cref="LogLevelSwitch"/> (#22). NLog can express routing as rules by logger name, but the live
/// switch is the fragile one: NLog's <c>ILogger</c> bridge caches per-level enabled-ness from the
/// <c>LoggingConfiguration</c>, so a runtime level change means reconfiguring the whole
/// <c>LogManager</c> — a heavier, race-prone mechanism than a volatile field read per write, and one
/// where the shipped no-restart behaviour could regress silently. Keeping our own
/// <see cref="ILogger.IsEnabled"/> gate keeps that behaviour, and its existing test, exactly as proven.
/// Every NLog rule is therefore pinned to <c>Trace</c> and never filters ahead of the switch.</para>
///
/// <para><b>Configured in code, not NLog.config.</b> The log paths are runtime values
/// (<c>AppInfo.DataDir</c> under %APPDATA%), so an XML file could not name them anyway — and this app is
/// unpackaged, where a shipped config file is one more thing that can fail to land beside the exe or
/// fail to resolve. Nothing to ship, nothing to resolve, nothing to go missing.</para>
///
/// <para>Each provider owns a private <see cref="LogFactory"/> rather than the global
/// <c>LogManager</c> singleton, so two providers (the resume-from-standby race below) and parallel
/// tests stay independent of one another.</para>
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>
    /// Rotate at 2 MB — roughly 20 000 lines, which is far more recent context than any diagnosis has
    /// needed, while staying small enough to open in an editor and read.
    /// </summary>
    internal const long ArchiveAboveSizeBytes = 2L * 1024 * 1024;

    /// <summary>
    /// Keep 5 archives per log. With the 2 MB cap that bounds each log family at ~12 MB and all three
    /// at ~36 MB — a fixed ceiling instead of today's unbounded growth, while still holding enough
    /// history to look back through several sessions rather than only the current one.
    /// </summary>
    internal const int MaxArchiveFiles = 5;

    private readonly string _defaultPath;
    private readonly IReadOnlyDictionary<string, string> _categoryPaths;
    private readonly LogLevelSwitch? _levelSwitch;
    private readonly Nlog.LogFactory _logFactory = new();
    // One NLog logger (and behind it one FileTarget) per distinct destination path.
    private readonly Dictionary<string, Nlog.Logger> _loggers = new(StringComparer.OrdinalIgnoreCase);

    public FileLoggerProvider(
        string defaultPath,
        IReadOnlyDictionary<string, string>? categoryPaths = null,
        LogLevelSwitch? levelSwitch = null)
    {
        _defaultPath   = defaultPath;
        _categoryPaths = categoryPaths ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _levelSwitch   = levelSwitch;

        // A log file damaged by an earlier power cut (issue #55) is repaired BEFORE NLog opens it —
        // once the target holds the handle we would be racing its writes. Best-effort by contract.
        foreach (var path in DistinctPaths()) LogFileSalvage.TrySalvage(path);

        Configure();
    }

    /// <summary>Every distinct destination: the catch-all plus each dedicated category file.</summary>
    private IEnumerable<string> DistinctPaths() =>
        _categoryPaths.Values.Prepend(_defaultPath).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds one <see cref="FileTarget"/> per distinct path and a Trace-level rule routing a private
    /// per-path logger name to it. Wrapped whole: a configuration we cannot build must degrade to
    /// silence, never take down a tray app over its log file.
    /// </summary>
    private void Configure()
    {
        // NLog swallows target write errors by default; pin both explicitly rather than inherit a
        // default that a future NLog upgrade could flip. This — not the open-time retry the old
        // StreamWriter had — is what keeps a log file open in Notepad from reaching the app.
        _logFactory.ThrowExceptions       = false;
        _logFactory.ThrowConfigExceptions = false;

        try
        {
            var config = new LoggingConfiguration(_logFactory);
            int index = 0;

            foreach (var path in DistinctPaths())
            {
                // Private, opaque names: these identify the SINK, not the category. The user-visible
                // category travels in ${message}, so a category can never collide with a rule.
                var name   = $"file-{index++}";
                var target = BuildTarget(name, path);

                config.AddTarget(target);
                // Trace: the LogLevelSwitch is the only runtime gate (issue #22). A rule minimum above
                // Trace would silently pre-filter and the no-restart level change would stop working.
                config.AddRule(Nlog.LogLevel.Trace, Nlog.LogLevel.Fatal, target, name);

                _loggers[path] = _logFactory.GetLogger(name);
            }

            _logFactory.Configuration = config;
        }
        catch
        {
            _loggers.Clear();   // no sinks: CreateLogger hands back a logger that writes nowhere
        }
    }

    private static FileTarget BuildTarget(string name, string path) => new(name)
    {
        FileName = path,

        // ${longdate} is yyyy-MM-dd HH:mm:ss.ffff — the old sink stamped whole seconds only, which is
        // why #54's latency lines carry their figures in the text. Sub-second stamps are a real gain
        // for ordering, but they measure when a line was WRITTEN; #54 measures spans its callers time
        // around the work itself, so its in-line numbers stay (see LatencyLog).
        // The level renders from an event property, not ${level}, to keep the exact Information/
        // Warning/Critical vocabulary the existing logs use — NLog would say Info/Warn/Fatal and the
        // words would change mid-file across the upgrade.
        Layout = "${longdate} [${event-properties:item=mel-level:padding=-11}] ${message}"
               + "${onexception:${newline}${exception:format=ToString}}",

        // Rotation — the reason NLog is here at all (issue #55). Archives land beside the log as
        // switcher_00.log, switcher_01.log, … (NLog's default ArchiveSuffixFormat "_{0:00}").
        ArchiveAboveSize = ArchiveAboveSizeBytes,
        MaxArchiveFiles  = MaxArchiveFiles,

        // NOT redundant with FileName, however much it looks it. Leave ArchiveFileName unset and NLog 6
        // rotates by moving the WRITER instead of the file: switcher.log is left holding the OLDEST
        // lines and new ones go to switcher_01.log, switcher_02.log, … — the stable name stops being
        // the current log. That silently breaks AppInfo.LogFile and Settings' "Switcher log" link,
        // which both point at switcher.log and would open ancient history or nothing at all. Setting
        // it — to the same path — switches NLog to rename-based archiving: switcher.log stays the live
        // file and the archives are the numbered ones. Verified by
        // FileLoggerTests.Rotation_WhenLogGrowsPastTheCap_*.
        ArchiveFileName = path,

        // Open-append-close per write. NLog 6 removed ConcurrentWrites, and this is what replaces it:
        // the only mode that is safe across PROCESSES, which this app needs — on resume-from-standby
        // the ONLOGON autostart task can start a new instance while the previous one is still shutting
        // down, so both briefly hold the log.
        //
        // It also hardens the case the old sink handled worst. That one opened once at startup and, if
        // the file was locked (Notepad, AV, a lingering instance), fell back to a null writer for the
        // WHOLE SESSION — one badly-timed lock and there was no log at all. Re-opening per write costs
        // a lock that only that one line, and the next line writes normally.
        // The price is an open+close per line, which for a tray app logging a few lines a minute is not
        // a cost worth reasoning about.
        KeepFileOpen = false,

        AutoFlush  = true,
        CreateDirs = true,
        Encoding   = System.Text.Encoding.UTF8,
    };

    public ILogger CreateLogger(string categoryName)
    {
        var path = _categoryPaths.TryGetValue(categoryName, out var mapped) ? mapped : _defaultPath;
        _loggers.TryGetValue(path, out var target);
        return new FileLogger(categoryName, target, _levelSwitch);
    }

    public void Dispose()
    {
        // Flushes and closes every FileTarget. Swallowed: a failure to shut logging down cleanly must
        // not surface during app exit.
        try { _logFactory.Shutdown(); } catch { /* best-effort */ }
    }
}

/// <summary>
/// Minimal <see cref="ILogger"/> that hands each entry to the NLog logger for its category's file.
/// </summary>
/// <param name="target">
/// The NLog logger for this category's file, or null when configuration failed — in which case this
/// logger silently writes nowhere rather than throwing at its caller.
/// </param>
internal sealed class FileLogger(string category, Nlog.Logger? target, LogLevelSwitch? levelSwitch) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    // Level filtering: when a live LogLevelSwitch is supplied it is the runtime gate (issue #22),
    // consulted per write so a Settings/config change takes effect with no restart. Deliberately NOT
    // delegated to NLog, whose enabled-ness is cached off the LoggingConfiguration and would need a
    // full reconfigure per change. Without a switch, filtering is left to the LoggerFactory's
    // configured minimum and everything but None passes.
    public bool IsEnabled(LogLevel level) =>
        levelSwitch is null ? level != LogLevel.None : levelSwitch.IsEnabled(level);

    public void Log<TState>(LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level) || target is null) return;

        var e = new Nlog.LogEventInfo(ToNLogLevel(level), target.Name, $"{category}: {formatter(state, exception)}")
        {
            Exception = exception,
        };
        // The MEL level name the layout renders, so the file keeps saying "Information" not "Info".
        e.Properties["mel-level"] = level.ToString();

        target.Log(e);
    }

    /// <summary>Maps the MEL level onto NLog's. Only <see cref="LogLevel.None"/> has no counterpart —
    /// and it is dropped by <see cref="IsEnabled"/> before ever reaching here.</summary>
    private static Nlog.LogLevel ToNLogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace       => Nlog.LogLevel.Trace,
        LogLevel.Debug       => Nlog.LogLevel.Debug,
        LogLevel.Information => Nlog.LogLevel.Info,
        LogLevel.Warning     => Nlog.LogLevel.Warn,
        LogLevel.Error       => Nlog.LogLevel.Error,
        LogLevel.Critical    => Nlog.LogLevel.Fatal,
        _                    => Nlog.LogLevel.Off,
    };
}
