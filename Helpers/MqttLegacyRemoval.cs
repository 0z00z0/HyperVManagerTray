using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Helpers;

/// <summary>What the one-time removal of the superseded flat <c>mqtt</c> block did.</summary>
internal enum MqttLegacyRemovalOutcome
{
    /// <summary>No <c>mqtt</c> section, or one already in the current shape.</summary>
    NotNeeded,
    Removed,
    Failed,
}

/// <summary>
/// Drops the flat <c>mqtt</c> block written by builds before the shared module, replacing it with an
/// empty section in the current shape (issue #75). The settings it held are discarded, not converted:
/// the broker is configured again in the MQTT panel.
///
/// <para><b>Why it is a step of its own.</b> <see cref="Services.ConfigManager"/> serialises the whole
/// document on every save, so the flat block is destroyed by the first write of any kind — moving the
/// Settings window is enough. That silently takes the broker password with it and says nothing.
/// Removing it deliberately, once, at startup, is the same event with a record of it in mqtt.log.</para>
/// </summary>
internal static class MqttLegacyRemoval
{
    // Indented like everything ConfigManager writes, so the file the user opens next looks the same as
    // the one they closed. The property names come off the parsed document, so no naming policy applies.
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// The keys the flat block wrote directly under <c>mqtt</c>. Detection is positive — one of these
    /// present at the block's TOP LEVEL is the legacy shape — rather than "the current keys are
    /// missing", which an empty section also satisfies and which would therefore rewrite config.json on
    /// every start for ever.
    ///
    /// <para>Several of the names also exist in the current shape, but one level down inside
    /// <c>settings</c>, where the module owns them. At the block's own top level none of them can be
    /// anything but a leftover.</para>
    /// </summary>
    internal static readonly string[] LegacyKeys =
    [
        "enabled", "host", "transport", "useTls", "username", "password", "discoveryPrefix",
        "deviceName", "nodeId", "publishNetwork", "publishVmState", "publishVmDiagnostics",
        "publishVmMetrics", "lastGoodEndpoint",
    ];

    /// <summary>Whether <paramref name="mqtt"/> is the flat block rather than the current shape.</summary>
    internal static bool IsLegacy(JsonObject mqtt) =>
        LegacyKeys.Any(mqtt.ContainsKey);

    /// <summary>
    /// Rewrites <paramref name="configPath"/> without the flat block, leaving <c>"mqtt": { "settings":
    /// {} }</c> behind. Called before the <see cref="Services.ConfigManager"/> exists, so the write
    /// cannot trip its watcher or race one of its saves.
    /// </summary>
    /// <returns>What happened, for the caller's own report.</returns>
    internal static MqttLegacyRemovalOutcome Run(string configPath, ILogger logger)
    {
        try
        {
            if (!File.Exists(configPath)) return MqttLegacyRemovalOutcome.NotNeeded;

            // Case-insensitive, matching how ConfigManager reads the document: a hand-edited "Host"
            // is the same leftover as "host".
            var options = new JsonNodeOptions { PropertyNameCaseInsensitive = true };
            if (JsonNode.Parse(File.ReadAllText(configPath), options) is not JsonObject root)
                return MqttLegacyRemovalOutcome.NotNeeded;

            // A null or non-object value carries no legacy keys; Load repairs it to inert defaults.
            if (root["mqtt"] is not JsonObject mqtt || !IsLegacy(mqtt))
                return MqttLegacyRemovalOutcome.NotNeeded;

            root["mqtt"] = new JsonObject { ["settings"] = new JsonObject() };
            File.WriteAllText(configPath, root.ToJsonString(WriteOptions));

            // Warning, not Information: the broker stops publishing and nothing else says why.
            logger.LogWarning(
                "The MQTT settings in {Path} were written by an earlier build and have been discarded. "
                + "MQTT stays off until the broker, its credentials and the publishing options are "
                + "entered again in Settings.", configPath);
            return MqttLegacyRemovalOutcome.Removed;
        }
        catch (Exception ex)
        {
            // Best-effort: the block is left as it was, and the next start tries again.
            logger.LogError(ex, "Could not remove the earlier MQTT settings from {Path}", configPath);
            return MqttLegacyRemovalOutcome.Failed;
        }
    }
}
