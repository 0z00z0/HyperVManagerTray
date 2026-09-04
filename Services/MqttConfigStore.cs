using System.Text.Json;
using HyperVManagerTray.Models;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Services;

/// <summary>
/// The module's settings store, implemented over this app's own config.json (issue #75). The shared
/// module treats <see cref="IMqttSettingsStore"/> as its entire storage dependency and makes no
/// assumption that it owns the file behind it, so <c>MqttSettingsFile</c> is never constructed here and
/// the broker block stays one section of the document the app already has.
/// </summary>
/// <remarks>
/// <para><see cref="Update"/> delegates to <see cref="ConfigManager.UpdateMqtt"/>, whose snapshot is
/// built inside the save lock — which is what makes this a read-modify-write against the live state
/// rather than against whatever the caller last read.</para>
/// <para><see cref="Changed"/> is raised off the thread that wrote, never inline:
/// <c>ConfigReloaded</c> fires while <c>ConfigManager</c> holds its save lock, and a subscriber that
/// does real work — re-applying a connection — would block every other config write behind it. The
/// module's contract allows that: <c>Changed</c> promises nothing about ordering, thread affinity or
/// coalescing.</para>
/// </remarks>
public sealed class MqttConfigStore : IMqttSettingsStore, IDisposable
{
    private readonly ConfigManager _config;
    private readonly Action<Action> _raise;
    private readonly object _lock = new();
    // The settings as last announced, so a config write that touched only the endpoint memory — or
    // nothing in this section at all — raises nothing. A successful connect must not read as a settings
    // change, or the connection re-applies on the strength of its own success.
    private string _announced;

    /// <param name="raise">How a <see cref="Changed"/> notification leaves the writing thread. Handed in
    /// so a test can run it inline instead of racing the thread pool.</param>
    public MqttConfigStore(ConfigManager config, Action<Action>? raise = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _raise = raise ?? (work => Task.Run(work));
        _announced = Fingerprint(_config.Current.Mqtt.Settings);
        _config.ConfigReloaded += OnConfigReloaded;
    }

    public MqttSettings Read() => _config.Current.Mqtt.Settings.Copy();

    public void Update(Action<MqttSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        _config.UpdateMqtt(section => mutate(section.Settings));
    }

    public event Action? Changed;

    /// <summary>Records where the broker answered. Not part of <see cref="MqttSettings"/>, so it raises
    /// no <see cref="Changed"/> and cannot be mistaken for the broker settings moving.</summary>
    public void RememberEndpoint(MqttEndpointMemory memory) =>
        _config.UpdateMqtt(section => section.Endpoint = memory);

    /// <summary>Where the broker last answered, or null when nothing has been recorded.</summary>
    public MqttEndpointMemory? RecallEndpoint() => _config.Current.Mqtt.Endpoint;

    /// <summary>Whether the power verbs are published as one button each rather than as one select.
    /// Outside <see cref="MqttSettings"/> for the same reason the endpoint memory is: it raises no
    /// <see cref="Changed"/>, so a shape change rebuilds the entity table without the broker settings
    /// reading as having moved and bouncing the connection.</summary>
    public bool PowerButtons => _config.Current.Mqtt.PowerButtons;

    /// <inheritdoc cref="PowerButtons"/>
    public void SetPowerButtons(bool on) =>
        _config.UpdateMqtt(section => section.PowerButtons = on);

    public void Dispose() => _config.ConfigReloaded -= OnConfigReloaded;

    private void OnConfigReloaded(object? sender, ConfigReloadedEventArgs e)
    {
        string current = Fingerprint(e.Config.Mqtt.Settings);
        lock (_lock)
        {
            if (current == _announced) return;
            _announced = current;
        }
        _raise(() => Changed?.Invoke());
    }

    /// <summary>What counts as the broker settings having moved. Serialised rather than field-compared,
    /// so a field the module adds later is covered without this file being touched — with the password
    /// reduced to the module's own non-reversible stand-in first, because this string is retained for
    /// the life of the store and compared on every reload.</summary>
    internal static string Fingerprint(MqttSettings settings)
    {
        var scrubbed = settings.Copy();
        scrubbed.Password = MqttConnectParameters.Fingerprint(scrubbed.Password);
        return JsonSerializer.Serialize(scrubbed);
    }
}
