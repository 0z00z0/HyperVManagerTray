namespace HyperVManagerTray.Helpers;

/// <summary>
/// Whether the "run at logon" toggle should read On. Existence alone is not enough — a task
/// disabled through Task Scheduler's own UI or by policy still satisfies "the task exists", so
/// existence alone would read On for a task the scheduler will never fire (issue #71).
///
/// <para>The read arrives as a delegate, exactly as <see cref="StartupTaskRepair.Run"/> takes its
/// scheduler calls, so the three-valued state — absent, present-but-disabled, present-and-enabled —
/// is testable with no live scheduled task.</para>
/// </summary>
internal static class StartupTaskState
{
    /// <param name="readEnabledFlag">The registered task's enabled flag, or <c>null</c> when there
    /// is no task. Whatever it throws is swallowed — an unreachable scheduler reads as "not
    /// enabled", same as "no task", never as an error the toggle need surface.</param>
    internal static bool IsEnabled(Func<bool?> readEnabledFlag)
    {
        try { return readEnabledFlag() == true; }
        catch { return false; }
    }
}
