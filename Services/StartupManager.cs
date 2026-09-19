using HyperVManagerTray.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZeroZero.Primitives;

// Aliased rather than imported: ZeroZero.Startup has its own StartupTaskState, which would be ambiguous
// against the toggle's decision helper in HyperVManagerTray.Helpers.
using StartupTask              = ZeroZero.Startup.StartupTask;
using StartupTaskOptions       = ZeroZero.Startup.StartupTaskOptions;
using StartupTaskRepairOutcome = ZeroZero.Startup.StartupTaskRepairOutcome;

namespace HyperVManagerTray.Services;

/// <summary>
/// Manages "run at Windows logon" for this elevated app.
///
/// A plain <c>HKCU\…\Run</c> entry cannot launch a <c>requireAdministrator</c> app at logon
/// (Windows starts Run-key items with a standard token and silently skips them), so auto-start
/// is implemented as a Scheduled Task with "Run with highest privileges" and a logon trigger.
/// The task runs in the user's interactive session, so the tray icon still appears, with no UAC
/// prompt.  Any obsolete Run-key value from older versions is removed whenever the setting is
/// toggled.
///
/// <para>The task itself — its definition, registration and repair — is ZeroZero.Startup's
/// <see cref="StartupTask"/>. The definition clears the scheduler defaults meant for a maintenance job:
/// the battery restrictions (issue #61), the three-day execution limit, hard termination, idle-only
/// running and below-normal priority, with new instances ignored.</para>
/// </summary>
internal sealed class StartupManager
{
    /// <summary>Also hard-coded as <c>TaskName</c> in installer\HyperVManagerTray.iss, so the
    /// installer option and the in-app toggle control the same task.</summary>
    internal const string TaskName = "HyperVManagerTray";

    private const string LegacyRunKey   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValue = "HyperVManagerTray";

    private readonly ILogger<StartupManager> _logger;

    public StartupManager(ILogger<StartupManager> logger) => _logger = logger;

    private static string Description =>
        $"Starts {AppInfo.Name} at logon, elevated, with power-safe settings.";

    /// <summary>The task by name. <paramref name="exePath"/> is what it starts; null means the running
    /// executable.</summary>
    private StartupTask Open(string? exePath = null) => new(new StartupTaskOptions
    {
        TaskName       = TaskName,
        Description    = Description,
        ExecutablePath = exePath,
        Log            = new StartupLogSink(_logger),
    });

    /// <summary>True only if the auto-start scheduled task exists AND is enabled (issue #71) — a
    /// task disabled through Task Scheduler's own UI or by policy reads as Off, not On. Never
    /// throws — the toggle reads it.</summary>
    public bool IsEnabled => StartupTaskState.IsEnabled(() =>
    {
        using var task = Open();
        // The task's own enabled flag, not its existence: a disabled task still exists.
        return task.IsEnabled;
    });

    /// <summary>Creates the logon task pointing at <paramref name="exePath"/>. Throws on failure.</summary>
    public void Enable(string exePath)
    {
        _logger.LogInformation("Enabling startup task '{TaskName}' for '{ExePath}'...", TaskName, exePath);

        using (var task = Open(exePath))
            task.Register();   // replaces a stale definition from an older build

        _logger.LogInformation("Startup task '{TaskName}' enabled successfully.", TaskName);
        RemoveLegacyRunKey();
    }

    /// <summary>Deletes the logon task. Throws on failure.</summary>
    public void Disable()
    {
        _logger.LogInformation("Disabling startup task '{TaskName}'...", TaskName);

        using (var task = Open())
            task.Delete();

        _logger.LogInformation("Startup task '{TaskName}' disabled successfully.", TaskName);
        RemoveLegacyRunKey();
    }

    /// <summary>
    /// Brings a logon task registered by an older build, or by the installer, up to the current
    /// definition — its settings, and the executable it starts when that is an older install path.
    /// Best-effort: it never creates a task, keeps the enabled flag as the user left it, and never
    /// throws. No demand start follows the rewrite, so the running app is never started a second time.
    /// </summary>
    public void TryRepair()
    {
        // A rewrite points the task at the running executable, so only an installed copy may make one:
        // a build started from its output folder would otherwise take over the logon task.
        if (!IsInstalledCopy(Environment.ProcessPath))
        {
            _logger.LogDebug("Not running from an installed copy — startup task '{TaskName}' left as it is.", TaskName);
            return;
        }

        try
        {
            using var task = Open();
            var result = task.Repair();

            switch (result.Outcome)
            {
                case StartupTaskRepairOutcome.Repaired:
                    _logger.LogInformation("Startup task '{TaskName}' repaired: it {Deviations}.",
                        TaskName, string.Join("; ", result.Deviations));
                    break;
                case StartupTaskRepairOutcome.AlreadyCorrect:
                    _logger.LogDebug("Startup task '{TaskName}' is already as it should be.", TaskName);
                    break;
                case StartupTaskRepairOutcome.NotRegistered:
                    _logger.LogDebug("No startup task '{TaskName}' — nothing to repair.", TaskName);
                    break;
                default:
                    _logger.LogWarning(result.Error,
                        "Could not repair startup task '{TaskName}' ({Outcome}) — the app may not start at logon.",
                        TaskName, result.Outcome);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Connecting to the scheduler happens before the repair's own guard.
            _logger.LogWarning(ex, "Could not repair startup task '{TaskName}' — the app may not start at logon.", TaskName);
        }
    }

    /// <summary>The installer leaves its uninstaller beside the executable; a build output folder has
    /// none.</summary>
    private static bool IsInstalledCopy(string? exePath)
    {
        var dir = Path.GetDirectoryName(exePath);
        return !string.IsNullOrEmpty(dir)
            && Directory.Exists(dir)
            && Directory.EnumerateFiles(dir, "unins*.exe").Any();
    }

    /// <summary>Removes the obsolete HKCU\Run value written by older versions, if present.</summary>
    private static void RemoveLegacyRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
        key?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
    }

    /// <summary>The component's log sink over this manager's logger, so its lines land in the same log.</summary>
    private sealed class StartupLogSink(ILogger logger) : ILogSink
    {
        public void Info(string message) => logger.LogInformation("{Message}", message);

        public void Error(string source, Exception? ex) =>
            logger.LogWarning(ex, "Startup task failure in {Source}", source);
    }
}
