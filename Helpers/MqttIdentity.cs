using HyperVManagerTray.Models;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.HomeAssistant;

namespace HyperVManagerTray.Helpers;

/// <summary>What this host is called on the broker: the names derived from the <c>mqtt</c> section when
/// its fields are blank. One definition, shared by the Settings panel's placeholders and
/// <c>MqttService</c>.</summary>
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

/// <summary>The address every retained topic is filed under — node id and discovery prefix together. A
/// change to either strands everything published under the old pair, so they compare as one
/// value.</summary>
internal readonly record struct MqttIdentity(string NodeId, string Prefix)
{
    public static MqttIdentity For(MqttSettings settings, string topicRoot, string machineName) => new(
        MqttNaming.EffectiveNodeId(settings, topicRoot, machineName),
        MqttNaming.EffectiveDiscoveryPrefix(settings));

    /// <summary>Whether moving to <paramref name="next"/> leaves retained topics behind that nothing
    /// will ever overwrite. Null means nothing has been published yet.</summary>
    public static bool Abandons(MqttIdentity? previous, MqttIdentity next) =>
        previous is { } p && p != next;
}
