using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Issue #71: <c>StartupManager.IsEnabled</c> used to test only whether the logon task existed, so a
/// task disabled through Task Scheduler, Settings → Apps → Startup, or policy still read as On. The
/// fix is <see cref="StartupTaskState.IsEnabled"/> — the read arrives as a delegate, so all three
/// states (absent, present-but-disabled, present-and-enabled) are assertable with no live task.
/// </summary>
public class StartupTaskStateTests
{
    /// <summary>THE test for the defect: a present-but-disabled task reads Off, not On.</summary>
    [Fact]
    public void APresentButDisabledTask_ReadsAsNotEnabled() =>
        Assert.False(StartupTaskState.IsEnabled(() => false));

    [Fact]
    public void APresentAndEnabledTask_ReadsAsEnabled() =>
        Assert.True(StartupTaskState.IsEnabled(() => true));

    /// <summary>No task at all is the same "Off" as a disabled one, never an error.</summary>
    [Fact]
    public void AnAbsentTask_ReadsAsNotEnabled() =>
        Assert.False(StartupTaskState.IsEnabled(() => null));

    /// <summary>An unreachable scheduler must not take the toggle down with it — reads as Off, the
    /// same as "no task", exactly like the property it replaces.</summary>
    [Fact]
    public void AFailedRead_ReadsAsNotEnabledAndNeverThrows() =>
        Assert.False(StartupTaskState.IsEnabled(() => throw new InvalidOperationException("scheduler unavailable")));
}
