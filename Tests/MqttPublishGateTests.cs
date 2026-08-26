using HyperVManagerTray.Helpers;
using Xunit;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Whether a state change is signalled to the broker connection now or held until there is a link
/// (issue #75). Every test here is about one measured failure: with MQTT already configured, the app's
/// own events start ~1.4 s before the socket does, and each one signalled every declared channel into a
/// client that was not connected — fifty Error lines in mqtt.log at every launch of a session that then
/// worked perfectly.
///
/// <para>The counterpart matters as much: a change held must not be lost. A gate that simply dropped
/// everything until the link came up would look identical in the log and strand any value the connect's
/// own republish did not happen to cover.</para>
/// </summary>
public class MqttPublishGateTests
{
    /// <summary>Counts the signals that actually reached the connection, so "held" and "dropped" are told
    /// apart by a number rather than by a flag.</summary>
    private sealed class Counter
    {
        public int Published;

        public MqttPublishGate Gate() => new(() => Published++);
    }

    // ── The gate ────────────────────────────────────────────────────────────────

    [Fact]
    public void Signal_ReachesTheConnectionWhileTheLinkIsLive()
    {
        var counter = new Counter();
        counter.Gate().Signal(linkIsLive: true);

        Assert.Equal(1, counter.Published);
    }

    /// <summary>The defect itself: fifty of these ran before the socket existed.</summary>
    [Fact]
    public void Signal_ReachesNothingWhileThereIsNoLink()
    {
        var counter = new Counter();
        var gate = counter.Gate();

        for (int i = 0; i < 50; i++) gate.Signal(linkIsLive: false);

        Assert.Equal(0, counter.Published);
        Assert.True(gate.IsHolding);
    }

    [Fact]
    public void IsHolding_IsFalseBeforeAnythingIsSignalled() => Assert.False(new Counter().Gate().IsHolding);

    // ── The release ─────────────────────────────────────────────────────────────

    /// <summary>Held, not dropped. Fifty signals before the link coalesce into exactly one afterwards.</summary>
    [Fact]
    public void OnState_ReleasesWhatWasHeldWhenTheLinkComesUp()
    {
        var counter = new Counter();
        var gate = counter.Gate();

        for (int i = 0; i < 50; i++) gate.Signal(linkIsLive: false);
        gate.OnState(MqttConnectionState.Connected);

        Assert.Equal(1, counter.Published);
        Assert.False(gate.IsHolding);
    }

    /// <summary>A reconnect that missed nothing has nothing to say. Without this the gate would put a
    /// whole pass over every channel behind every reconnect, for values the connect already republished.</summary>
    [Fact]
    public void OnState_AsksForNothingWhenNothingWasHeld()
    {
        var counter = new Counter();
        counter.Gate().OnState(MqttConnectionState.Connected);

        Assert.Equal(0, counter.Published);
    }

    /// <summary>The release is armed once. A second Connected — a reconnect, or the state being
    /// re-published — must not replay a change already signalled.</summary>
    [Fact]
    public void OnState_ReleasesOnceForOneHeldChange()
    {
        var counter = new Counter();
        var gate = counter.Gate();

        gate.Signal(linkIsLive: false);
        gate.OnState(MqttConnectionState.Connected);
        gate.OnState(MqttConnectionState.Connected);

        Assert.Equal(1, counter.Published);
    }

    /// <summary>Only Connected releases. Every other state is the link still not being there, and
    /// releasing on one of them is the failed publish this class exists to stop.</summary>
    [Theory]
    [InlineData(MqttConnectionState.Disabled)]
    [InlineData(MqttConnectionState.Connecting)]
    [InlineData(MqttConnectionState.Searching)]
    [InlineData(MqttConnectionState.Retrying)]
    [InlineData(MqttConnectionState.Failed)]
    public void OnState_HoldsOnEveryStateThatIsNotConnected(MqttConnectionState state)
    {
        var counter = new Counter();
        var gate = counter.Gate();

        gate.Signal(linkIsLive: false);
        gate.OnState(state);

        Assert.Equal(0, counter.Published);
        Assert.True(gate.IsHolding);
    }

    /// <summary>A change that got through while the link was live leaves nothing owing, so the next
    /// connect asks for nothing. Without clearing the hold here, every disconnect/reconnect after a
    /// single early signal would replay a pass for ever.</summary>
    [Fact]
    public void Signal_WhileLiveClearsWhatWasHeld()
    {
        var counter = new Counter();
        var gate = counter.Gate();

        gate.Signal(linkIsLive: false);
        gate.Signal(linkIsLive: true);
        Assert.False(gate.IsHolding);

        gate.OnState(MqttConnectionState.Connected);
        Assert.Equal(1, counter.Published);
    }

    /// <summary>A drop with a change arriving while it is down: held, then released by the reconnect.</summary>
    [Fact]
    public void AChangeArrivingWhileTheLinkIsDownSurvivesTheReconnect()
    {
        var counter = new Counter();
        var gate = counter.Gate();

        gate.Signal(linkIsLive: true);                       // healthy
        gate.OnState(MqttConnectionState.Retrying);          // the broker went away
        gate.Signal(linkIsLive: false);                      // a VM changed state meanwhile
        Assert.Equal(1, counter.Published);

        gate.OnState(MqttConnectionState.Connected);
        Assert.Equal(2, counter.Published);
    }
}
