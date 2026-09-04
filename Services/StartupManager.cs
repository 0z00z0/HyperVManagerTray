using HyperVManagerTray.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

// Microsoft.Win32.TaskScheduler.Task would otherwise be ambiguous against System.Threading.Tasks.Task
// (ImplicitUsings).
using ScheduledTask = Microsoft.Win32.TaskScheduler.Task;

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
/// <para>The task is registered through the Task Scheduler API rather than <c>schtasks /Create</c>,
/// which has no switch for the battery settings that decide whether the task ever starts — see
/// <see cref="StartupTaskDefinition"/> (issue #61).</para>
/// </summary>
internal sealed class StartupManager
{
    private const string TaskName       = StartupTaskDefinition.TaskName;
    private const string LegacyRunKey   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValue = "HyperVManagerTray";

    private readonly ILogger<StartupManager> _logger;

    public StartupManager(ILogger<StartupManager> logger) => _logger = logger;

    /// <summary>True only if the auto-start scheduled task exists AND is enabled (issue #71) — a
    /// task disabled through Task Scheduler, Settings, or policy reads as Off, not On. Never throws
    /// — the toggle reads it.</summary>
    public bool IsEnabled => StartupTaskState.IsEnabled(() =>
    {
        using var ts = new TaskService();
        using ScheduledTask? task = ts.GetTask(StartupTaskDefinition.TaskPath);
        return task?.Definition.Settings.Enabled;
    });

    /// <summary>Creates the logon task pointing at <paramref name="exePath"/>. Throws on failure.</summary>
    public void Enable(string exePath)
    {
        _logger.LogInformation("Enabling startup task '{TaskName}' for '{ExePath}'...", TaskName, exePath);

        TaskIdentity user = TaskIdentity.Current()
            ?? throw new InvalidOperationException("Cannot determine the current user for the startup task.");

        using var ts = new TaskService();
        using TaskDefinition td = StartupTaskDefinition.BuildLogonTask(ts, exePath, user);
        ts.RootFolder.RegisterTaskDefinition(
            TaskName, td,
            TaskCreation.CreateOrUpdate,   // overwrite a stale definition from an older build
            userId:    null,
            password:  null,
            logonType: TaskLogonType.InteractiveToken);

        _logger.LogInformation("Startup task '{TaskName}' enabled successfully.", TaskName);
        RemoveLegacyRunKey();
    }

    /// <summary>Deletes the logon task. Throws on failure.</summary>
    public void Disable()
    {
        _logger.LogInformation("Disabling startup task '{TaskName}'...", TaskName);

        using var ts = new TaskService();
        ts.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);

        _logger.LogInformation("Startup task '{TaskName}' disabled successfully.", TaskName);
        RemoveLegacyRunKey();
    }

    /// <summary>
    /// Repairs a logon task registered by an older build, whose inherited scheduler defaults stop it
    /// starting the app on battery (issue #61). Best-effort and idempotent: it never creates a task,
    /// never throws, and only rewrites one that is actually blocked.
    /// </summary>
    public void TryRepairPowerSettings()
    {
        Exception? error   = null;
        var        outcome = Repair(ex => error = ex);

        switch (outcome)
        {
            case StartupTaskRepairOutcome.Repaired:
                _logger.LogInformation(
                    "Startup task '{TaskName}' repaired: battery restrictions cleared, so it now starts "
                    + "the app when the machine boots on battery.", TaskName);
                break;
            case StartupTaskRepairOutcome.AlreadyPowerSafe:
                _logger.LogDebug("Startup task '{TaskName}' is already power-safe.", TaskName);
                break;
            case StartupTaskRepairOutcome.NotRegistered:
                _logger.LogDebug("No startup task '{TaskName}' — nothing to repair.", TaskName);
                break;
            case StartupTaskRepairOutcome.Failed:
                _logger.LogWarning(error,
                    "Could not repair startup task '{TaskName}' — auto-start may stay blocked on battery.",
                    TaskName);
                break;
        }
    }

    /// <summary>Wires the scheduler reads/writes into <see cref="StartupTaskRepair.Run"/>, which owns
    /// the decision and swallows whatever these throw.</summary>
    private static StartupTaskRepairOutcome Repair(Action<Exception> onError)
    {
        TaskService?   ts   = null;
        ScheduledTask? task = null;
        try
        {
            return StartupTaskRepair.Run(
                readFlags: () =>
                {
                    ts   = new TaskService();
                    task = ts.GetTask(StartupTaskDefinition.TaskPath);
                    return task is null
                        ? null
                        : StartupTaskDefinition.ReadPowerFlags(task.Definition.Settings);
                },
                repair: () =>
                {
                    // In place — RegisterChanges keeps the existing trigger, action and principal,
                    // so a task pointing at another install path is fixed, not hijacked.
                    StartupTaskDefinition.ApplyPowerSafe(task!.Definition.Settings);
                    task.RegisterChanges();
                },
                onError: onError);
        }
        finally
        {
            task?.Dispose();
            ts?.Dispose();
        }
    }

    /// <summary>Removes the obsolete HKCU\Run value written by older versions, if present.</summary>
    private static void RemoveLegacyRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
        key?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
    }
}
