namespace HyperVManagerTray.Helpers;

/// <summary>Decides whether the app holds <c>VmService.SubscribeMetrics()</c> for the MQTT
/// integration. The subscription runs a 2.5 s WMI loop, so it is held only while the publish toggle is
/// on <b>and</b> the broker session is live.</summary>
public sealed class MqttMetricsHold
{
    private readonly Action _subscribe;
    private readonly Action _unsubscribe;
    private readonly object _lock = new();
    private bool _held;

    public MqttMetricsHold(Action subscribe, Action unsubscribe)
    {
        _subscribe   = subscribe;
        _unsubscribe = unsubscribe;
    }

    /// <summary>Whether the subscription is currently held.</summary>
    public bool IsHeld { get { lock (_lock) return _held; } }

    /// <summary>Whether the subscription should be held for these conditions.</summary>
    public static bool ShouldHold(bool publishMetrics, bool connected) => publishMetrics && connected;

    /// <summary>Reconciles the hold to the current conditions. Idempotent: repeated calls with the
    /// same answer subscribe and unsubscribe exactly once.</summary>
    public void Update(bool publishMetrics, bool connected)
    {
        bool want = ShouldHold(publishMetrics, connected);
        Action? act = null;
        lock (_lock)
        {
            if (want != _held)
            {
                _held = want;
                act = want ? _subscribe : _unsubscribe;
            }
        }
        act?.Invoke();
    }

    /// <summary>Releases the hold if it is held. For teardown.</summary>
    public void Release() => Update(publishMetrics: false, connected: false);
}
