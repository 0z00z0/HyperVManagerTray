using ZeroZero.Mqtt;
using ZeroZero.Mqtt.WinUI;

namespace HyperVManagerTray.Services;

/// <summary>
/// The module's strings with the one that names a particular consumer replaced (issue #75). Everything
/// else falls through to <see cref="MqttResourceStrings.Instance"/> — the module's own map, then its
/// built-in en-GB — so a string the module adds later arrives without this file being touched.
/// </summary>
/// <remarks>The settings UI stays consumer-neutral: it describes an MQTT broker and an openly published
/// discovery convention, and names no product. The prefix box still defaults to
/// <see cref="MqttSettings.DefaultDiscoveryPrefix"/>, which is the convention's own registered value.</remarks>
public sealed class MqttPanelStrings : IMqttStringSource
{
    /// <summary>The module's info line beside the discovery prefix, minus the product name.</summary>
    private static readonly Dictionary<string, string> Overrides = new(StringComparer.Ordinal)
    {
        ["InfoDiscoveryPrefix"] =
            "Entities are announced using the MQTT Discovery convention, an openly published "
            + "specification that some MQTT consumers follow and others ignore. The prefix only needs "
            + "changing for a consumer that listens elsewhere.",
    };

    public static IMqttStringSource Instance { get; } = new MqttPanelStrings();

    private MqttPanelStrings() { }

    public string? Find(string key) =>
        Overrides.TryGetValue(key, out string? text) ? text : MqttResourceStrings.Instance.Find(key);
}
