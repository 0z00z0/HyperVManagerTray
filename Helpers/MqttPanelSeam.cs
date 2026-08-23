using HyperVManagerTray.Models;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// The mapping between this app's <c>mqtt</c> config section and the shared settings panel's view of
/// it (issue #75). The panel edits a <c>MqttPanelSnapshot</c> and reports each edit back as a facet —
/// the master toggle, one publish category, the broker batch, the node id — and each facet lands on a
/// copy of the stored settings here.
///
/// <para>Pure, and deliberately so: the panel is WinUI, so an edit's effect on the stored settings is
/// only assertable if the arithmetic lives outside it. The window destructures the snapshot and calls
/// in; nothing here knows a control exists.</para>
/// </summary>
internal static class MqttPanelSeam
{
    /// <summary>The publish-category keys, as the panel reports them back. Every key the window offers
    /// is one of these — <see cref="WithCategory"/> and <see cref="IsOn"/> answer for exactly this set,
    /// so a key that is not in it toggles nothing rather than silently writing some other field.</summary>
    public const string NetworkKey        = "network";
    public const string VmStateKey        = "vm-state";
    public const string VmDiagnosticsKey  = "vm-diagnostics";
    public const string VmMetricsKey      = "vm-metrics";

    public static IReadOnlyList<string> Keys { get; } =
        [NetworkKey, VmStateKey, VmDiagnosticsKey, VmMetricsKey];

    /// <summary>Whether a category is currently published. An unrecognised key reads false.</summary>
    public static bool IsOn(MqttSettings settings, string key) => key switch
    {
        NetworkKey       => settings.PublishNetwork,
        VmStateKey       => settings.PublishVmState,
        VmDiagnosticsKey => settings.PublishVmDiagnostics,
        VmMetricsKey     => settings.PublishVmMetrics,
        _                => false,
    };

    /// <summary>One publish category toggled. An unrecognised key changes nothing — a dead toggle is
    /// visible, whereas a key typo that fell through to some other field would not be.</summary>
    public static MqttSettings WithCategory(MqttSettings settings, string key, bool isOn)
    {
        var next = settings.Copy();
        switch (key)
        {
            case NetworkKey:       next.PublishNetwork       = isOn; break;
            case VmStateKey:       next.PublishVmState       = isOn; break;
            case VmDiagnosticsKey: next.PublishVmDiagnostics = isOn; break;
            case VmMetricsKey:     next.PublishVmMetrics     = isOn; break;
        }
        return next;
    }

    public static MqttSettings WithEnabled(MqttSettings settings, bool enabled)
    {
        var next = settings.Copy();
        next.Enabled = enabled;
        return next;
    }

    /// <summary>A confirmed node-id change. Blank is the sentinel for "derive one from the machine
    /// name", so it is stored as blank rather than as the derived value.</summary>
    public static MqttSettings WithNodeId(MqttSettings settings, string? nodeId)
    {
        var next = settings.Copy();
        next.NodeId = (nodeId ?? string.Empty).Trim();
        return next;
    }

    /// <summary>
    /// The broker batch, committed as one. Only the fields the batch owns are written: the master
    /// toggle, the node id and the remembered endpoint each have their own commit path, and taking
    /// them from a snapshot the panel has been holding since it opened would roll back whichever of
    /// them changed meanwhile.
    /// </summary>
    public static MqttSettings WithBroker(
        MqttSettings settings, MqttOptions options, string? deviceName, string? discoveryPrefix,
        string? password)
    {
        var next = settings.Copy();
        next.Host            = (options.Host ?? string.Empty).Trim();
        next.Port            = options.Port;
        next.Transport       = options.Transport;
        next.UseTls          = options.UseTls;
        next.Username        = (options.Username ?? string.Empty).Trim();
        next.DeviceName      = (deviceName ?? string.Empty).Trim();
        next.DiscoveryPrefix = (discoveryPrefix ?? string.Empty).Trim();
        next.Password        = password ?? string.Empty;
        return next;
    }
}
