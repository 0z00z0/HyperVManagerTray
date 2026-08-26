using ZeroZero.Mqtt;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Whether a state change is signalled to the broker connection now, or held until there is a link to
/// publish it over (issue #75).
///
/// <para><b>What this exists to stop.</b> The app's own events start arriving the moment the services
/// come up, roughly 1.4 s before the socket does. Every one of them signalled every declared channel, and
/// each of those reached the broker client while it was still connecting — fifty <c>Publish</c> failures
/// in mqtt.log at every launch, on a start that then went on to work perfectly. The values were never in
/// doubt: the connect republishes every channel from the state cache, so the whole burst was work whose
/// only product was the error lines.</para>
///
/// <para><b>Held, not dropped.</b> A change that arrived with no link is remembered and signalled once
/// the link reports itself connected. That makes the app's own behaviour complete rather than resting on
/// the module happening to republish on connect, so a value can never be stranded by this gate.</para>
///
/// <para><b>Liveness is passed in, never sampled here.</b> <see cref="Signal"/> takes the connection's own
/// <c>IsConnected</c> at the moment of the change — true from the moment the socket is up, which is
/// earlier than <see cref="MqttConnectionState.Connected"/> is published. Gating on the published state
/// instead would drop a change that landed between the socket coming up and the announcement finishing,
/// and nothing afterwards would carry it.</para>
/// </summary>
public sealed class MqttPublishGate(Action requestPublish)
{
    private readonly object _lock = new();
    private bool _held;

    /// <summary>Whether a change is waiting for a link.</summary>
    public bool IsHolding { get { lock (_lock) return _held; } }

    /// <summary>A published value may have moved. Signalled straight through while the link is live;
    /// held otherwise.</summary>
    public void Signal(bool linkIsLive)
    {
        lock (_lock)
        {
            if (!linkIsLive) { _held = true; return; }
            _held = false;
        }
        // Outside the lock: the callback reaches the connection, which starts work of its own.
        requestPublish();
    }

    /// <summary>The connection's state moved. A change held while there was no link is released here, and
    /// nothing is asked for when none was held — a reconnect with no missed change has nothing to say.</summary>
    public void OnState(MqttConnectionState state)
    {
        if (state != MqttConnectionState.Connected) return;
        lock (_lock)
        {
            if (!_held) return;
            _held = false;
        }
        requestPublish();
    }
}
