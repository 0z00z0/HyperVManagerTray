using ZeroZero.Mqtt;

namespace HyperVManagerTray.Models;

/// <summary>
/// The <c>mqtt</c> section of config.json (issue #75). The shared module stores its settings through
/// <see cref="IMqttSettingsStore"/> rather than owning a file, so this app keeps one settings document
/// and the module is none the wiser — see <c>Services\MqttConfigStore.cs</c>.
///
/// <para><see cref="MqttSettings"/> is held whole rather than field-by-field: a copy that names each
/// field silently drops the next one the module adds, and every dropped field is written back as null
/// on the first save.</para>
/// </summary>
public sealed class MqttSection
{
    /// <summary>The module's own broker block. Inert until <see cref="MqttSettings.Enabled"/> is on and
    /// a host is set, so a config that predates this section reads as "configured, disabled".</summary>
    public MqttSettings Settings { get; set; } = new();

    /// <summary>Where the broker last answered. State, not a setting: it is deliberately outside
    /// <see cref="MqttSettings"/> so a successful connect is not a settings change, which would
    /// otherwise make the connection re-apply on the strength of its own success.</summary>
    public MqttEndpointMemory? Endpoint { get; set; }

    /// <summary>Whether each managed VM's power verbs are published as one button per verb instead of
    /// as a single select of them. Off by default, which is the select.
    ///
    /// <para>This app's setting, not the module's, and deliberately outside <see cref="Settings"/>: it
    /// decides what the entity table composes rather than how the connection is made, and a shape
    /// change must rebuild the entity set without the broker settings reading as having moved.</para></summary>
    public bool PowerButtons { get; set; }

    /// <summary>A copy, so a mutator staging edits never hands the live instance to a writer.</summary>
    /// <remarks>Every field is carried. This is what <c>ConfigManager.UpdateMqtt</c> mutates and then
    /// writes back whole, so a field omitted here is not left alone — it is written back as its default
    /// and permanently lost, the same trap <c>ConfigManager.With</c> carries for the top-level fields.</remarks>
    public MqttSection Copy() => new()
    {
        Settings = Settings.Copy(),
        // A record with no mutable state — sharing the instance cannot leak an edit.
        Endpoint = Endpoint,
        PowerButtons = PowerButtons,
    };
}
