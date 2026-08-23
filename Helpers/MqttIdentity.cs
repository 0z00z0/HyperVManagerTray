using HyperVManagerTray.Models;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.HomeAssistant;

namespace HyperVManagerTray.Helpers;

/// <summary>What this host is called on the broker: the names derived from the <c>mqtt</c> section when
/// its fields are blank. One definition, so the Settings panel's placeholders describe what
/// <c>MqttService</c> actually publishes rather than a second copy that can drift.</summary>
internal static class MqttNaming
{
    /// <summary>The device every entity groups under when <c>deviceName</c> is blank.</summary>
    public static string DefaultDeviceName => $"{AppInfo.Name} ({Environment.MachineName})";

    public static string EffectiveDeviceName(MqttSettings settings) =>
        string.IsNullOrWhiteSpace(settings?.DeviceName) ? DefaultDeviceName : settings.DeviceName.Trim();

    /// <summary>The discovery prefix in force. Blank is Home Assistant's own default, not an empty
    /// topic segment.</summary>
    public static string EffectiveDiscoveryPrefix(MqttSettings settings) =>
        string.IsNullOrWhiteSpace(settings?.DiscoveryPrefix)
            ? HaDiscoveryContext.DefaultPrefix
            : settings.DiscoveryPrefix.Trim();

    public static string EffectiveNodeId(MqttSettings settings, string topicRoot, string machineName) =>
        MqttOptionsValidator.EffectiveNodeId(settings?.NodeId, topicRoot, machineName);
}

/// <summary>
/// The address every retained topic is filed under — the node id and the discovery prefix together.
/// The node id is the state, command and availability path; the prefix is where the discovery configs
/// live. A change to either strands everything published under the old pair (issue #75), so they are
/// compared as one value.
/// </summary>
internal readonly record struct MqttIdentity(string NodeId, string Prefix)
{
    public static MqttIdentity For(MqttSettings settings, string topicRoot, string machineName) => new(
        MqttNaming.EffectiveNodeId(settings, topicRoot, machineName),
        MqttNaming.EffectiveDiscoveryPrefix(settings));

    /// <summary>
    /// Whether moving to <paramref name="next"/> leaves retained topics behind that nothing will ever
    /// overwrite. Null means nothing has been published yet, so there is nothing to strand.
    ///
    /// <para>Only the pair matters. A device name edited on its own republishes over the same topics,
    /// and a disable evicts through the connection's own stop hook — neither abandons anything.</para>
    /// </summary>
    public static bool Abandons(MqttIdentity? previous, MqttIdentity next) =>
        previous is { } p && p != next;
}
