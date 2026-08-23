using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>When the app holds <c>VmService.SubscribeMetrics()</c> for the MQTT integration. The toggle
/// being off means the 2.5 s WMI loop never starts, not that its output is discarded.</summary>
public class MqttMetricsHoldTests
{
    private sealed class Counter
    {
        public int Subscribes { get; private set; }
        public int Unsubscribes { get; private set; }
        public MqttMetricsHold Hold { get; }

        public Counter() => Hold = new MqttMetricsHold(() => Subscribes++, () => Unsubscribes++);
    }

    /// <summary>The rule this class exists for.</summary>
    [Fact]
    public void NothingIsHeldWhileTheToggleIsOff()
    {
        var counter = new Counter();

        counter.Hold.Update(publishMetrics: false, connected: true);

        Assert.False(counter.Hold.IsHeld);
        Assert.Equal(0, counter.Subscribes);
    }

    [Fact]
    public void NothingIsHeldWhileTheBrokerIsNotConnected()
    {
        var counter = new Counter();

        counter.Hold.Update(publishMetrics: true, connected: false);

        Assert.False(counter.Hold.IsHeld);
        Assert.Equal(0, counter.Subscribes);
    }

    [Fact]
    public void TheSubscriptionIsTakenOnlyWhenBothHold()
    {
        var counter = new Counter();

        counter.Hold.Update(publishMetrics: true, connected: true);

        Assert.True(counter.Hold.IsHeld);
        Assert.Equal(1, counter.Subscribes);
        Assert.Equal(0, counter.Unsubscribes);
    }

    [Fact]
    public void TurningTheToggleOffReleasesTheSubscription()
    {
        var counter = new Counter();
        counter.Hold.Update(publishMetrics: true, connected: true);

        counter.Hold.Update(publishMetrics: false, connected: true);

        Assert.False(counter.Hold.IsHeld);
        Assert.Equal(1, counter.Unsubscribes);
    }

    [Fact]
    public void LosingTheBrokerReleasesTheSubscription()
    {
        var counter = new Counter();
        counter.Hold.Update(publishMetrics: true, connected: true);

        counter.Hold.Update(publishMetrics: true, connected: false);

        Assert.False(counter.Hold.IsHeld);
        Assert.Equal(1, counter.Unsubscribes);
    }

    /// <summary>Every event the app raises re-checks the hold, so a non-idempotent Update would take
    /// the ref-counted subscription out several times over and never give it back.</summary>
    [Fact]
    public void RepeatedUpdatesSubscribeAndUnsubscribeExactlyOnce()
    {
        var counter = new Counter();

        for (int i = 0; i < 5; i++) counter.Hold.Update(publishMetrics: true, connected: true);
        for (int i = 0; i < 5; i++) counter.Hold.Update(publishMetrics: false, connected: true);

        Assert.Equal(1, counter.Subscribes);
        Assert.Equal(1, counter.Unsubscribes);
    }

    [Fact]
    public void ReleaseGivesBackAHeldSubscription()
    {
        var counter = new Counter();
        counter.Hold.Update(publishMetrics: true, connected: true);

        counter.Hold.Release();

        Assert.False(counter.Hold.IsHeld);
        Assert.Equal(1, counter.Unsubscribes);
    }

    [Fact]
    public void ReleaseOnAnUnheldSubscriptionDoesNothing()
    {
        var counter = new Counter();

        counter.Hold.Release();

        Assert.Equal(0, counter.Unsubscribes);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true,  false)]
    [InlineData(true,  false, false)]
    [InlineData(true,  true,  true)]
    public void ShouldHold_IsBothConditionsAndNothingElse(bool publishMetrics, bool connected, bool expected) =>
        Assert.Equal(expected, MqttMetricsHold.ShouldHold(publishMetrics, connected));
}
