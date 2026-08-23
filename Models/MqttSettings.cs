using ZeroZero.Mqtt;

namespace HyperVManagerTray.Models;

/// <summary>
/// The <c>mqtt</c> section of config.json (issue #75): where the broker is, how to authenticate, what
/// this host is called in Home Assistant, and whether the per-VM metrics are published.
///
/// <para>Persisted here rather than as a <see cref="MqttOptions"/> so the file keeps this repo's shape
/// (mutable properties, camelCase, nulls omitted) and so the broker password has somewhere to live —
/// <see cref="MqttOptions"/> carries a credential reference, not a secret. <see cref="ToOptions"/> is
/// the one place the two are mapped.</para>
/// </summary>
public sealed class MqttSettings
{
    /// <summary>The credential-store key the broker password is filed under. One connection per
    /// process, so one key.</summary>
    public const string CredentialReference = "broker";

    public bool Enabled { get; set; }

    /// <summary>Broker host name or IP. Blank means "not configured": the connection stands down.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Broker port, or null to let the transport sweep find it.</summary>
    public int? Port { get; set; }

    public MqttTransportSetting Transport { get; set; } = MqttTransportSetting.Auto;

    public bool UseTls { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary>The broker password, in plain text beside the rest of the configuration. Held behind
    /// <see cref="IMqttCredentialStore"/> at runtime, so an encrypted store is an implementation swap
    /// rather than a config change.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Home Assistant's discovery prefix; blank uses its default.</summary>
    public string DiscoveryPrefix { get; set; } = string.Empty;

    /// <summary>The device name every entity groups under; blank derives one from the machine name.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>The topic node id; blank derives one from the machine name.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Whether the host-network entities — the active rule, switch, adapter, addresses, apply
    /// status, bridge health and the two command buttons — are published.</summary>
    public bool PublishNetwork { get; set; } = true;

    /// <summary>Whether each managed VM's state and its power, on/off and switch-override controls are
    /// published.</summary>
    public bool PublishVmState { get; set; } = true;

    /// <summary>Whether each managed VM's switch, guest IP, uptime and last operation are published.</summary>
    public bool PublishVmDiagnostics { get; set; } = true;

    /// <summary>
    /// Whether per-VM CPU, memory and VHD are published. <b>Off by default, and that default is the
    /// point</b> (issue #75): those figures only flow while something holds
    /// <c>VmService.SubscribeMetrics()</c>, a 2.5 s WMI loop, and the app otherwise does no in-process
    /// WMI work while idle. The other three categories cost nothing extra — they publish what the app
    /// already knows — which is why only this one defaults off.
    /// </summary>
    public bool PublishVmMetrics { get; set; }

    /// <summary>Where the broker last answered — state the connection writes back, not a setting.</summary>
    public MqttEndpointMemory? LastGoodEndpoint { get; set; }

    /// <summary>A copy carrying the same values, so a mutator never hands the live instance to a writer.</summary>
    public MqttSettings Copy() => new()
    {
        Enabled              = Enabled,
        Host                 = Host,
        Port                 = Port,
        Transport            = Transport,
        UseTls               = UseTls,
        Username             = Username,
        Password             = Password,
        DiscoveryPrefix      = DiscoveryPrefix,
        DeviceName           = DeviceName,
        NodeId               = NodeId,
        PublishNetwork       = PublishNetwork,
        PublishVmState       = PublishVmState,
        PublishVmDiagnostics = PublishVmDiagnostics,
        PublishVmMetrics     = PublishVmMetrics,
        LastGoodEndpoint     = LastGoodEndpoint,
    };

    /// <summary>The shared connection's view of these settings. The password is deliberately absent —
    /// it reaches the connection through <see cref="IMqttCredentialStore"/>.</summary>
    public MqttOptions ToOptions() => new()
    {
        Enabled             = Enabled,
        Host                = Host ?? string.Empty,
        Port                = Port,
        Transport           = Transport,
        UseTls              = UseTls,
        Username            = Username ?? string.Empty,
        CredentialReference = CredentialReference,
        NodeId              = NodeId ?? string.Empty,
        LastGoodEndpoint    = LastGoodEndpoint,
    };
}
