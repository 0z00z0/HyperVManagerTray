using HyperVManagerTray.Helpers;
using Xunit;
using ZeroZero.Lifecycle;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The sentence written for each single-instance outcome. Gaining the distinction is the whole point
/// of taking the shared lock: the helper it replaced answered true or false, so "another instance
/// holds it" and "the name is not this process's to open" were the same event, and an access failure
/// escaped as an exception rather than a refusal.
/// </summary>
public class SingleInstanceLogTests
{
    private static readonly SingleInstanceOutcome[] AllOutcomes =
        Enum.GetValues<SingleInstanceOutcome>();

    /// <summary>
    /// Four outcomes, four sentences. One shared between any two of them hides the difference in the
    /// crash log, where these lines are the only record a refused launch leaves.
    /// </summary>
    [Fact]
    public void EveryOutcome_GetsItsOwnSentence()
    {
        var messages = AllOutcomes.Select(SingleInstanceLog.Message).ToList();

        Assert.Equal(messages.Count, messages.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A blank line in the crash log records nothing at all.</summary>
    [Theory]
    [InlineData(SingleInstanceOutcome.TakenFree)]
    [InlineData(SingleInstanceOutcome.TakenAbandoned)]
    [InlineData(SingleInstanceOutcome.RefusedHeld)]
    [InlineData(SingleInstanceOutcome.RefusedDenied)]
    public void EveryOutcome_SaysSomething(SingleInstanceOutcome outcome)
        => Assert.False(string.IsNullOrWhiteSpace(SingleInstanceLog.Message(outcome)));

    /// <summary>
    /// A refusal says the launch is stopping. Without it the line reads as an observation, and the
    /// crash log is the only place anyone learns the launch went nowhere.
    /// </summary>
    [Theory]
    [InlineData(SingleInstanceOutcome.RefusedHeld)]
    [InlineData(SingleInstanceOutcome.RefusedDenied)]
    public void ARefusal_SaysTheLaunchIsExiting(SingleInstanceOutcome outcome)
        => Assert.Contains("exiting", SingleInstanceLog.Message(outcome), StringComparison.Ordinal);

    /// <summary>
    /// The denied refusal names the rights, not a second copy of the app. On a machine with one user
    /// it is usually a name clash with something else entirely, and reporting it as "another instance
    /// is running" sends the reader looking for a process that does not exist.
    /// </summary>
    [Fact]
    public void ADeniedRefusal_DoesNotClaimAnotherInstanceIsRunning()
    {
        var message = SingleInstanceLog.Message(SingleInstanceOutcome.RefusedDenied);

        Assert.Contains("may not open it", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Another instance already holds", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An abandoned lock says the previous instance did not exit cleanly — the silent compositor
    /// teardown this app is actually hit by. Reported as an ordinary free name, the one machine-level
    /// clue that it happened is lost.
    /// </summary>
    [Fact]
    public void AnAbandonedLock_SaysThePreviousInstanceDidNotExitCleanly()
        => Assert.Contains("did not exit cleanly",
                           SingleInstanceLog.Message(SingleInstanceOutcome.TakenAbandoned),
                           StringComparison.Ordinal);

    /// <summary>An outcome the component might add later must still produce a line naming it.</summary>
    [Fact]
    public void AnUnrecognisedOutcome_StillNamesWhatHappened()
        => Assert.Contains("unrecognised outcome",
                           SingleInstanceLog.Message((SingleInstanceOutcome)999),
                           StringComparison.Ordinal);
}
