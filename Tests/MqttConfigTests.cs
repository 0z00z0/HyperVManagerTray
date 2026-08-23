using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The two config traps issue #75 names, both of which are properties of ConfigManager rather than of
/// the MQTT code that has to survive them:
///
/// <list type="bullet">
///   <item><see cref="ConfigManager"/>'s <c>With(...)</c> replaces the WHOLE config, so a field its
///   signature omits is written back as null and permanently lost. Every mutator is exercised below
///   after MQTT settings have been saved, one test per call site, because a missed one does not fail
///   to compile — it silently wipes the section the next time that mutator runs.</item>
///   <item><c>AffectsNetwork</c> is an EXCLUSION list, so an <c>mqtt</c> property not named in
///   <c>NonNetworkProperties</c> would make every MQTT settings change re-evaluate the network — and a
///   full re-evaluation can move a VM's switch. Editing a broker port must not.</item>
/// </list>
/// </summary>
public class MqttConfigTests : IDisposable
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter() }
    };

    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { /* best-effort */ }
    }

    /// <summary>Settings distinguishable from every default, so a wipe cannot look like a pass.</summary>
    private static MqttSettings Sample() => new()
    {
        Enabled          = true,
        Host             = "broker.lan",
        Port             = 1884,
        Transport        = MqttTransportSetting.WebSocket,
        UseTls           = true,
        Username         = "hvmt",
        Password         = "s3cret",
        DiscoveryPrefix  = "ha",
        DeviceName       = "Hyper-V host",
        NodeId           = "hypervmanagertray_lab",
        // Every publish category the opposite of its default, so a mutator that drops one is caught by
        // the value it lands on rather than being indistinguishable from the fresh state.
        PublishNetwork       = false,
        PublishVmState       = false,
        PublishVmDiagnostics = false,
        PublishVmMetrics     = true,
        LastGoodEndpoint = new MqttEndpointMemory("broker.lan", "hvmt", 1884, MqttTransport.WebSocket),
    };

    /// <summary>Every field of <see cref="Sample"/> still on the settings, with the remembered endpoint
    /// taken as an argument — it is the one field a mutator (the endpoint write-back) legitimately
    /// moves.</summary>
    private static void AssertSampleIntact(MqttSettings? actual, MqttEndpointMemory? endpoint = null)
    {
        var expected = Sample();
        Assert.NotNull(actual);
        Assert.True(actual!.Enabled);
        Assert.Equal(expected.Host, actual.Host);
        Assert.Equal(expected.Port, actual.Port);
        Assert.Equal(expected.Transport, actual.Transport);
        Assert.True(actual.UseTls);
        Assert.Equal(expected.Username, actual.Username);
        Assert.Equal(expected.Password, actual.Password);
        Assert.Equal(expected.DiscoveryPrefix, actual.DiscoveryPrefix);
        Assert.Equal(expected.DeviceName, actual.DeviceName);
        Assert.Equal(expected.NodeId, actual.NodeId);
        Assert.False(actual.PublishNetwork);
        Assert.False(actual.PublishVmState);
        Assert.False(actual.PublishVmDiagnostics);
        Assert.True(actual.PublishVmMetrics);
        Assert.Equal(endpoint ?? expected.LastGoodEndpoint, actual.LastGoodEndpoint);
    }

    /// <summary>A manager over a temp config that already holds a VM, a rule and a fallback, so every
    /// mutator below has something to act on and none of them no-ops.</summary>
    private ConfigManager MakeManager()
    {
        var initial = new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "DevBox", NicName = "Network Adapter" }],
            Rules =
            [
                new NetworkRule
                {
                    Name = "Office", Priority = 10, VirtualSwitch = "Bridged",
                    TargetVms = ["DevBox"],
                    Conditions = new RuleConditions { AdapterMac = "AA:BB:CC:DD:EE:FF" },
                }
            ],
            Fallback = new FallbackAction { VirtualSwitch = "Default Switch", TargetVms = [] },
        };

        var path = Path.Combine(Path.GetTempPath(), $"hvmt_mqtt_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(initial, WriteOpts));
        _tempFiles.Add(path);
        return new ConfigManager(path, NullLogger<ConfigManager>.Instance);
    }

    private static AppConfig ReadFile(string path) =>
        JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), ReadOpts) ?? new AppConfig();

    // ── Trap 1: With(...) must carry the mqtt section through every call site ──────────────────────

    /// <summary>The round trip itself: saved settings survive a write and a re-read.</summary>
    [Fact]
    public void SaveMqttSettings_RoundTripsThroughTheFile()
    {
        using var manager = MakeManager();
        manager.SaveMqttSettings(Sample());

        AssertSampleIntact(manager.Current.Mqtt);
    }

    /// <summary>
    /// One case per <c>With(...)</c> call site. Each mutator is a whole-file write built from a
    /// snapshot, so any of them omitting the section erases it — and the erasure is silent.
    /// </summary>
    private static readonly string[] MutatorNames =
    [
        "AddBridgedRule", "AddVmToConfig", "RemoveVmFromConfig", "UpdateLogLevel",
        "SetVmBridgeLostAction", "SetVmNicName", "SaveRules", "SetFallback",
        "UpsertAdapterName", "SaveSettingsWindowRect", "RememberMqttEndpoint",
    ];

    public static TheoryData<string> Mutators()
    {
        var data = new TheoryData<string>();
        foreach (string name in MutatorNames) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Mutators))]
    public void EveryMutator_LeavesTheMqttSectionIntact(string mutator)
    {
        using var manager = MakeManager();
        manager.SaveMqttSettings(Sample());

        var endpoint = Run(manager, mutator);

        AssertSampleIntact(manager.Current.Mqtt, endpoint);
    }

    /// <summary>The same, asserted against the FILE rather than the in-memory copy: <c>With</c>
    /// serialises over config.json wholesale, so the file is where a dropped field actually goes.</summary>
    [Theory]
    [MemberData(nameof(Mutators))]
    public void EveryMutator_LeavesTheMqttSectionOnDisk(string mutator)
    {
        using var manager = MakeManager();
        manager.SaveMqttSettings(Sample());
        var path = _tempFiles[^1];

        var endpoint = Run(manager, mutator);

        AssertSampleIntact(ReadFile(path).Mqtt, endpoint);
    }

    /// <summary>Runs one mutator with arguments that are guaranteed to change something, so its
    /// unchanged-value guard cannot turn the test into a no-op. Returns the endpoint the settings
    /// should carry afterwards — the same one for every mutator but the endpoint write-back.</summary>
    private static MqttEndpointMemory? Run(ConfigManager manager, string mutator)
    {
        switch (mutator)
        {
            case "AddBridgedRule":
                manager.AddBridgedRule(new NetworkRule { Name = "Home", Priority = 20, VirtualSwitch = "Bridged" });
                break;
            case "AddVmToConfig":
                manager.AddVmToConfig("BuildBox", "Network Adapter");
                break;
            case "RemoveVmFromConfig":
                manager.RemoveVmFromConfig("DevBox");
                break;
            case "UpdateLogLevel":
                manager.UpdateLogLevel(LogLevel.Warning);
                break;
            case "SetVmBridgeLostAction":
                manager.SetVmBridgeLostAction("DevBox", "Pause", 30);
                break;
            case "SetVmNicName":
                manager.SetVmNicName("DevBox", "Second Adapter");
                break;
            case "SaveRules":
                manager.SaveRules([new NetworkRule { Name = "Rewritten", Priority = 5, VirtualSwitch = "Bridged" }]);
                break;
            case "SetFallback":
                manager.SetFallback("NAT Switch", ["DevBox"]);
                break;
            case "UpsertAdapterName":
                manager.UpsertAdapterName(new AdapterNameOverride
                {
                    DeviceInstanceId     = "USB\\VID_0BDA&PID_8153\\000002000000",
                    OriginalFriendlyName = "Realtek USB GbE",
                    CurrentFriendlyName  = "Dock LAN",
                });
                break;
            case "SaveSettingsWindowRect":
                manager.SaveSettingsWindowRect(new HyperVManagerTray.Helpers.WindowRect(10, 20, 900, 700));
                break;
            case "RememberMqttEndpoint":
                var found = new MqttEndpointMemory("broker.lan", "hvmt", 8883, MqttTransport.Tcp);
                manager.RememberMqttEndpoint(found);
                return found;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutator), mutator, "Unknown mutator.");
        }
        return null;
    }

    /// <summary>
    /// <see cref="Mutators"/> has to name EVERY public mutator, or the trap is only half-covered. This
    /// asserts the list against the class itself, so a mutator added later fails here rather than
    /// silently going untested.
    /// </summary>
    [Fact]
    public void MutatorList_NamesEveryPublicConfigMutator()
    {
        var declared = typeof(ConfigManager)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.DeclaredOnly)
            // IsSpecialName drops the property and event accessors, which are also void-with-arguments.
            .Where(m => !m.IsSpecialName && m.ReturnType == typeof(void) && m.GetParameters().Length > 0)
            .Select(m => m.Name)
            // The section's own writer: it cannot lose what it is writing, and it is covered by
            // SaveMqttSettings_RoundTripsThroughTheFile.
            .Where(n => n != nameof(ConfigManager.SaveMqttSettings))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var covered = MutatorNames.OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(declared, covered);
    }

    /// <summary>The endpoint write-back is itself a <c>With</c> call site, and the one the connection
    /// drives: it must move the endpoint and nothing else.</summary>
    [Fact]
    public void RememberMqttEndpoint_ChangesOnlyTheEndpoint()
    {
        using var manager = MakeManager();
        manager.SaveMqttSettings(Sample());

        var found = new MqttEndpointMemory("broker.lan", "hvmt", 8883, MqttTransport.Tcp);
        manager.RememberMqttEndpoint(found);

        var mqtt = manager.Current.Mqtt;
        Assert.Equal(found, mqtt.LastGoodEndpoint);
        Assert.Equal("s3cret", mqtt.Password);
        Assert.Equal(1884, mqtt.Port);
        Assert.True(mqtt.Enabled);
    }

    /// <summary>A config written before the section existed loads as "configured, disabled" rather
    /// than as a null nobody guards against.</summary>
    [Fact]
    public void ConfigWithoutTheSection_LoadsAsDisabledDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvmt_mqtt_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "logLevel": "Debug", "virtualMachines": [], "rules": [] }""");
        _tempFiles.Add(path);

        using var manager = new ConfigManager(path, NullLogger<ConfigManager>.Instance);

        Assert.NotNull(manager.Current.Mqtt);
        Assert.False(manager.Current.Mqtt.Enabled);
    }

    /// <summary>A hand-edited <c>"mqtt": null</c> is repaired on load for the same reason.</summary>
    [Fact]
    public void ConfigWithANullSection_LoadsAsDisabledDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvmt_mqtt_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "logLevel": "Debug", "mqtt": null }""");
        _tempFiles.Add(path);

        using var manager = new ConfigManager(path, NullLogger<ConfigManager>.Instance);

        Assert.NotNull(manager.Current.Mqtt);
        Assert.False(manager.Current.Mqtt.Enabled);
    }

    /// <summary>A partial section — one field written by hand, the rest absent — fills in from the
    /// defaults rather than from nulls the publish path would have to guard.</summary>
    [Fact]
    public void ConfigWithAPartialSection_FillsTheRestFromTheDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvmt_mqtt_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "logLevel": "Debug", "mqtt": { "host": "broker.lan" } }""");
        _tempFiles.Add(path);

        using var manager = new ConfigManager(path, NullLogger<ConfigManager>.Instance);
        var mqtt = manager.Current.Mqtt;

        Assert.Equal("broker.lan", mqtt.Host);
        Assert.False(mqtt.Enabled);                 // inert until the master toggle is set
        Assert.Null(mqtt.Port);
        Assert.Equal(string.Empty, mqtt.Username);
        Assert.Equal(string.Empty, mqtt.Password);
        Assert.Equal(string.Empty, mqtt.NodeId);
        Assert.Null(mqtt.LastGoodEndpoint);
        Assert.True(mqtt.PublishNetwork);
        Assert.False(mqtt.PublishVmMetrics);        // the one category that costs a WMI poll
    }

    /// <summary>A section whose types do not parse fails the load like any other malformed field: the
    /// previously loaded config stays live and the outcome says so. It must not throw out of
    /// <c>Load</c> — a broken hand-edit may not take a tray app down.</summary>
    [Fact]
    public void ConfigWithAMalformedSection_KeepsThePreviousConfig()
    {
        using var manager = MakeManager();
        manager.SaveMqttSettings(Sample());
        var path = _tempFiles[^1];

        File.WriteAllText(path, """{ "mqtt": { "port": "not-a-port" } }""");
        var outcome = manager.Load();

        Assert.False(outcome.Succeeded);
        AssertSampleIntact(manager.Current.Mqtt);
    }

    // ── Trap 2: an mqtt edit is not a network edit ────────────────────────────────────────────────

    /// <summary>"mqtt" must be named in the exclusion list, or every property under it lands in the
    /// network comparison automatically.</summary>
    [Fact]
    public void NonNetworkProperties_NamesTheMqttSection()
    {
        Assert.Contains("mqtt", ConfigManager.NonNetworkProperties);
    }

    [Fact]
    public void AffectsNetwork_IsFalseForAnMqttOnlyChange()
    {
        var before = new AppConfig { Mqtt = new MqttSettings { Host = "broker.lan", Port = 1883 } };
        var after  = new AppConfig { Mqtt = new MqttSettings { Host = "broker.lan", Port = 8883 } };

        Assert.False(ConfigManager.AffectsNetwork(before, after));
    }

    [Fact]
    public void AffectsNetwork_IsFalseWhenTheWholeMqttSectionIsReplaced()
    {
        var before = new AppConfig();
        var after  = new AppConfig { Mqtt = Sample() };

        Assert.False(ConfigManager.AffectsNetwork(before, after));
    }

    /// <summary>The other direction, so the exclusion cannot be over-broad: a rule change still
    /// re-evaluates.</summary>
    [Fact]
    public void AffectsNetwork_IsStillTrueForARuleChange()
    {
        var before = new AppConfig { Mqtt = Sample() };
        var after  = new AppConfig
        {
            Mqtt  = Sample(),
            Rules = [new NetworkRule { Name = "Office", Priority = 10, VirtualSwitch = "Bridged" }],
        };

        Assert.True(ConfigManager.AffectsNetwork(before, after));
    }

    /// <summary>The end-to-end version: saving MQTT settings raises a reload that reports
    /// <c>AffectsNetwork</c> false, so the NetworkMonitor stands down and no switch moves.</summary>
    [Fact]
    public void SavingMqttSettings_RaisesAReloadThatDoesNotAffectTheNetwork()
    {
        using var manager = MakeManager();
        var reported = new List<bool>();
        manager.ConfigReloaded += (_, e) => reported.Add(e.AffectsNetwork);

        manager.SaveMqttSettings(Sample());

        Assert.Equal([false], reported);
    }

    /// <summary>Same for the endpoint write-back, which fires on every fresh broker connect.</summary>
    [Fact]
    public void RememberingTheEndpoint_RaisesAReloadThatDoesNotAffectTheNetwork()
    {
        using var manager = MakeManager();
        manager.SaveMqttSettings(Sample());

        var reported = new List<bool>();
        manager.ConfigReloaded += (_, e) => reported.Add(e.AffectsNetwork);
        manager.RememberMqttEndpoint(new MqttEndpointMemory("broker.lan", "hvmt", 8883, MqttTransport.Tcp));

        Assert.Equal([false], reported);
    }

    // ── The options mapping ───────────────────────────────────────────────────────────────────────

    /// <summary>The password reaches the connection through the credential store, never through the
    /// options — so it cannot end up in a log line that dumps them.</summary>
    [Fact]
    public void ToOptions_CarriesTheCredentialReferenceRatherThanThePassword()
    {
        var options = Sample().ToOptions();

        Assert.Equal(MqttSettings.CredentialReference, options.CredentialReference);
        Assert.DoesNotContain("s3cret", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToOptions_CarriesEveryConnectionField()
    {
        var options = Sample().ToOptions();

        Assert.True(options.Enabled);
        Assert.Equal("broker.lan", options.Host);
        Assert.Equal(1884, options.Port);
        Assert.Equal(MqttTransportSetting.WebSocket, options.Transport);
        Assert.True(options.UseTls);
        Assert.Equal("hvmt", options.Username);
        Assert.Equal("hypervmanagertray_lab", options.NodeId);
        Assert.Equal(Sample().LastGoodEndpoint, options.LastGoodEndpoint);
    }

    [Fact]
    public void Copy_IsIndependentOfTheOriginal()
    {
        var original = Sample();
        var copy = original.Copy();
        copy.Host = "elsewhere";

        Assert.Equal("broker.lan", original.Host);
        Assert.Equal("elsewhere", copy.Host);
    }
}
