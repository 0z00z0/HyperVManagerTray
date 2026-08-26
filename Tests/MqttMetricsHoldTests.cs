using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Whether the app holds <c>VmService.SubscribeMetrics()</c> for the MQTT integration (issue #75). The
/// subscription is a 2.5 s WMI loop in an app that otherwise does no in-process WMI work while idle, so
/// every test here is really about one thing: the loop must not be running when nobody is reading it.
/// </summary>
public class MqttMetricsHoldTests
{
    /// <summary>Counts the two callbacks, so "idempotent" is asserted as a count rather than as a flag.</summary>
    private sealed class Counter
    {
        public int Subscribed;
        public int Unsubscribed;

        public MqttMetricsHold Hold() => new(() => Subscribed++, () => Unsubscribed++);
    }

    // ── The condition ───────────────────────────────────────────────────────────

    /// <summary>Both, or neither. A live connection with the group off is the default configuration, and
    /// the group on with no connection is an app that cannot publish what it would be sampling.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true,  false)]
    [InlineData(true,  false, false)]
    [InlineData(true,  true,  true)]
    public void ShouldHold_RequiresTheGroupOnAndTheConnectionLive(bool metrics, bool connected, bool expected)
        => Assert.Equal(expected, MqttMetricsHold.ShouldHold(metrics, connected));

    [Fact]
    public void IsHeld_IsFalseBeforeAnythingIsReconciled() => Assert.False(new Counter().Hold().IsHeld);

    // ── Reconciliation ──────────────────────────────────────────────────────────

    [Fact]
    public void Update_TakesTheHoldWhenBothConditionsArriveAndReleasesItWhenOneGoes()
    {
        var counter = new Counter();
        var hold = counter.Hold();

        hold.Update(publishMetrics: true, connected: false);
        Assert.False(hold.IsHeld);
        Assert.Equal(0, counter.Subscribed);

        hold.Update(publishMetrics: true, connected: true);
        Assert.True(hold.IsHeld);
        Assert.Equal(1, counter.Subscribed);

        // The broker drops: the loop must stop, not carry on sampling into nothing.
        hold.Update(publishMetrics: true, connected: false);
        Assert.False(hold.IsHeld);
        Assert.Equal(1, counter.Unsubscribed);
    }

    /// <summary>Connection state and group toggles both arrive repeatedly — a reconnect re-announces,
    /// and a settings write re-reads the group. Each must reconcile to the same answer without stacking
    /// a second subscription the app would then have to remember to release twice.</summary>
    [Fact]
    public void Update_IsIdempotent()
    {
        var counter = new Counter();
        var hold = counter.Hold();

        for (int i = 0; i < 5; i++) hold.Update(publishMetrics: true, connected: true);
        Assert.Equal(1, counter.Subscribed);
        Assert.Equal(0, counter.Unsubscribed);

        for (int i = 0; i < 5; i++) hold.Update(publishMetrics: false, connected: true);
        Assert.Equal(1, counter.Subscribed);
        Assert.Equal(1, counter.Unsubscribed);
    }

    [Fact]
    public void Release_DropsAHeldSubscription()
    {
        var counter = new Counter();
        var hold = counter.Hold();
        hold.Update(publishMetrics: true, connected: true);

        hold.Release();

        Assert.False(hold.IsHeld);
        Assert.Equal(1, counter.Unsubscribed);
    }

    /// <summary>Teardown runs whether or not the hold was ever taken, so releasing an unheld one must
    /// not reach <c>VmService</c> at all.</summary>
    [Fact]
    public void Release_IsANoOpWhenNothingIsHeld()
    {
        var counter = new Counter();
        var hold = counter.Hold();

        hold.Release();
        hold.Release();

        Assert.Equal(0, counter.Unsubscribed);
        Assert.Equal(0, counter.Subscribed);
    }

    // ── Where the callbacks run ─────────────────────────────────────────────────

    /// <summary>
    /// The callbacks reach <c>VmService</c>, which takes locks of its own, so they must run OUTSIDE this
    /// class's lock — otherwise a connection-state change and a group toggle arriving together deadlock
    /// against VmService's own ordering.
    ///
    /// <para>Asserted by having the subscribe callback ask another thread to read <see cref="MqttMetricsHold.IsHeld"/>,
    /// which takes the same lock. Invoked inside the lock, that read blocks until the callback returns
    /// and the wait times out.</para>
    /// </summary>
    [Fact]
    public void Update_InvokesTheCallbackOutsideItsOwnLock()
    {
        MqttMetricsHold? hold = null;
        bool answeredPromptly = false;

        hold = new MqttMetricsHold(
            subscribe: () => answeredPromptly =
                Task.Run(() => hold!.IsHeld).Wait(TimeSpan.FromSeconds(5)),
            unsubscribe: () => { });

        hold.Update(publishMetrics: true, connected: true);

        Assert.True(answeredPromptly,
            "the subscribe callback ran while the hold's own lock was held — another thread could not "
            + "read IsHeld, and a callback that takes VmService's locks would deadlock here");
    }

    /// <summary>Two threads reconciling at once still subscribe exactly once: the decision and the flag
    /// move together under the lock, so only one of them can see the transition.</summary>
    [Fact]
    public void Update_SubscribesOnceUnderConcurrentReconciliation()
    {
        var counter = new Counter();
        var hold = counter.Hold();

        Parallel.For(0, 64, _ => hold.Update(publishMetrics: true, connected: true));

        Assert.Equal(1, counter.Subscribed);
        Assert.True(hold.IsHeld);
    }
}
