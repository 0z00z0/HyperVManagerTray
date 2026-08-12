namespace HyperVManagerTray.Helpers;

/// <summary>The battery settings a registered logon task carries, and whether they block it.</summary>
internal readonly record struct StartupTaskPowerFlags(bool DisallowStartIfOnBatteries, bool StopIfGoingOnBatteries)
{
    /// <summary>Either flag is enough to stop the app starting (or keep it running) on battery.</summary>
    internal bool NeedsRepair => DisallowStartIfOnBatteries || StopIfGoingOnBatteries;
}

/// <summary>What the self-heal did, for the log line and for the tests.</summary>
internal enum StartupTaskRepairOutcome
{
    /// <summary>No logon task — the user never enabled startup. Nothing to repair, and nothing created here.</summary>
    NotRegistered,
    AlreadyPowerSafe,
    Repaired,
    Failed,
}

/// <summary>
/// The startup self-heal for issue #61: a logon task registered by an older build (or by any bare
/// <c>schtasks /Create</c>) carries Task Scheduler's battery defaults and never starts the app on a
/// machine that boots on battery. Read and write arrive as delegates, so the rules below are
/// testable without a real scheduled task.
/// </summary>
internal static class StartupTaskRepair
{
    /// <param name="readFlags">The task's battery settings, or <c>null</c> when there is no task.</param>
    /// <param name="repair">Rewrites the task power-safe. Called only when it is not already.</param>
    /// <param name="onError">Receives whatever <paramref name="readFlags"/> or <paramref name="repair"/> threw.</param>
    internal static StartupTaskRepairOutcome Run(
        Func<StartupTaskPowerFlags?> readFlags, Action repair, Action<Exception>? onError = null)
    {
        try
        {
            if (readFlags() is not { } flags) return StartupTaskRepairOutcome.NotRegistered;
            if (!flags.NeedsRepair)           return StartupTaskRepairOutcome.AlreadyPowerSafe;

            repair();
            return StartupTaskRepairOutcome.Repaired;
        }
        catch (Exception ex)
        {
            // Best-effort by design: a lost auto-start must never cost the app its startup.
            onError?.Invoke(ex);
            return StartupTaskRepairOutcome.Failed;
        }
    }
}
