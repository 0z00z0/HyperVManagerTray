using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The announce/suppress decision behind the failed-apply tray balloon, lifted out of App.xaml.cs so it
/// can be tested without a WinUI host. Two defects are pinned here: a user command producing two
/// contradictory toasts, and a failure being marked "announced" though no balloon was ever shown.
/// </summary>
public class FailureAnnouncerTests
{
    private const string Msg  = "Could not bind virtual switch 'Bridged' to 'Ethernet 5'.";
    private const string Msg2 = "Could not connect 'vDev' to virtual switch 'Bridged'.";

    // ── Rule 1: one action, one report ───────────────────────────────────────────

    /// <summary>
    /// A user-initiated pass (Re-check, Override) is reported by the command that ran it — which knows
    /// it was an override and can distinguish "not a managed VM". The automatic balloon must stand down
    /// rather than fire a second, contradictory toast for the same click ("Could not connect 'vDev'…"
    /// followed by "Could not move vDev… Nothing was changed.", which asserts what the first denies).
    /// </summary>
    [Fact]
    public void Next_UserInitiatedFailureIsLeftToTheCommandThatRanIt() =>
        Assert.Null(new FailureAnnouncer().Next(Msg, userInitiated: true));

    /// <summary>
    /// Standing down must not arm the latch: the same failure recurring on a later AUTOMATIC pass has
    /// nobody watching, which is the case this balloon exists for.
    /// </summary>
    [Fact]
    public void Next_StandingDownForACommandDoesNotSuppressTheLaterAutomaticFailure()
    {
        var a = new FailureAnnouncer();

        Assert.Null(a.Next(Msg, userInitiated: true));      // the command reports this one
        Assert.Equal(Msg, a.Next(Msg, userInitiated: false)); // ...but an automatic recurrence still speaks
    }

    // An automatic failure is announced — the baseline this all rests on.
    [Fact]
    public void Next_AutomaticFailureIsAnnounced() =>
        Assert.Equal(Msg, new FailureAnnouncer().Next(Msg, userInitiated: false));

    // ── Rule 2: latch only what was actually announced ───────────────────────────

    /// <summary>
    /// THE defect. The old code latched the message before calling ShowBalloon, which suppresses the
    /// toast while the dashboard is visible. A failure first seen with the dashboard open was recorded
    /// as announced although nothing was shown — and once the dashboard closed, the identical message
    /// short-circuited forever, so the balloon was never shown at all. Not calling MarkAnnounced (the
    /// suppressed case) must leave it announceable.
    /// </summary>
    [Fact]
    public void Next_AFailureThatWasNeverShownIsStillAnnounceable()
    {
        var a = new FailureAnnouncer();

        Assert.Equal(Msg, a.Next(Msg, userInitiated: false));   // offered...
        // ...but ShowBalloon suppressed it (dashboard visible), so MarkAnnounced is NOT called.

        Assert.Equal(Msg, a.Next(Msg, userInitiated: false));   // must still be offered next time
    }

    // Dedupe still works once the balloon has genuinely been shown: SwitchApplied fires on every network
    // blip, so a persisting failure must not re-toast while the user is doing nothing about it.
    [Fact]
    public void Next_AnAnnouncedFailureIsNotRepeated()
    {
        var a = new FailureAnnouncer();

        Assert.Equal(Msg, a.Next(Msg, userInitiated: false));
        a.MarkAnnounced(Msg);

        Assert.Null(a.Next(Msg, userInitiated: false));
        Assert.Null(a.Next(Msg, userInitiated: false));
    }

    // A DIFFERENT failure is news even while the first is still latched.
    [Fact]
    public void Next_ADifferentFailureIsAnnouncedEvenWhileOneIsLatched()
    {
        var a = new FailureAnnouncer();
        a.Next(Msg, userInitiated: false);
        a.MarkAnnounced(Msg);

        Assert.Equal(Msg2, a.Next(Msg2, userInitiated: false));
    }

    // ── Recovery re-arms ─────────────────────────────────────────────────────────

    /// <summary>A null message is NetworkStatusUi.FailureMessage saying "this pass didn't fail" —
    /// the failure resolved, so the same failure recurring later is genuinely new and must announce.</summary>
    [Fact]
    public void Next_RecoveryReArmsSoTheSameFailureAnnouncesAgainLater()
    {
        var a = new FailureAnnouncer();
        a.Next(Msg, userInitiated: false);
        a.MarkAnnounced(Msg);
        Assert.Null(a.Next(Msg, userInitiated: false));   // still latched

        Assert.Null(a.Next(null, userInitiated: false));  // recovered

        Assert.Equal(Msg, a.Next(Msg, userInitiated: false));   // it came back — say so
    }

    // A success never produces a toast, whoever triggered the pass.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Next_NoFailureIsNeverAnnounced(bool userInitiated) =>
        Assert.Null(new FailureAnnouncer().Next(null, userInitiated));
}
