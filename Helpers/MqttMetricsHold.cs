namespace HyperVManagerTray.Helpers;

/// <summary>
/// Decides whether the app holds <c>VmService.SubscribeMetrics()</c> for the MQTT integration (issue
/// #75). That subscription runs a 2.5 s WMI loop in an app that does no in-process WMI work while idle,
/// so it is held only while the metrics group is on <b>and</b> the broker connection is live.
/// </summary>
/// <remarks>Liveness arrives from <c>MqttConnection.StateChanged</c> rather than being sampled: the
/// module publishes its connection state, so nothing here has to poll for it.</remarks>
public sealed class MqttMetricsHold(Action subscribe, Action unsubscribe)
{
    private readonly object _lock = new();
    private bool _held;

    /// <summary>Whether the subscription is currently held.</summary>
    public bool IsHeld { get { lock (_lock) return _held; } }

    /// <summary>Whether the subscription should be held for these conditions.</summary>
    public static bool ShouldHold(bool publishMetrics, bool connected) => publishMetrics && connected;

    /// <summary>Reconciles the hold to the current conditions. Idempotent: repeated calls with the same
    /// answer subscribe and unsubscribe exactly once.</summary>
    public void Update(bool publishMetrics, bool connected)
    {
        bool want = ShouldHold(publishMetrics, connected);
        Action? act = null;
        lock (_lock)
        {
            if (want != _held)
            {
                _held = want;
                act = want ? subscribe : unsubscribe;
            }
        }
        // Outside the lock: the callbacks reach VmService, which takes locks of its own.
        act?.Invoke();
    }

    /// <summary>Releases the hold if it is held. For teardown.</summary>
    public void Release() => Update(publishMetrics: false, connected: false);
}
