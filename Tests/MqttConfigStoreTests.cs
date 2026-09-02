using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The module's settings store over this app's own config.json (issue #75). Every test writes a real
/// temp file, because the two failures this file exists to catch are both failures of what reaches disk:
/// a whole-config rewrite that drops a section it did not name, and a snapshot taken outside the save
/// lock that writes a stale copy of everything else back over a sibling's work.
/// </summary>
public class MqttConfigStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Mirrors ConfigManager's own write options, so a temp config is byte-shaped like a real one.</summary>
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter() }
    };

    private readonly List<string> _tempFiles = [];

    /// <summary>
    /// A directory of this test class's own, holding nothing but the configs it writes.
    ///
    /// <para>ConfigManager puts a <see cref="FileSystemWatcher"/> on the directory its config lives in.
    /// Pointed at the machine temp directory it also sees every other file the rest of the suite writes
    /// there, and the watcher's kernel buffer is a fixed 8 KB: on a loaded runner it overflows and the
    /// change we are waiting for is dropped, never late. No wait ceiling can recover a dropped event, so
    /// the watcher has to be given a quiet directory instead. Production already has one — the app's own
    /// %AppData% folder.</para>
    /// </summary>
    private readonly string _tempDir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"hvmt_mqtt_{Guid.NewGuid():N}")).FullName;

    private string WriteTempConfig(AppConfig cfg)
    {
        var path = Path.Combine(_tempDir, $"hvmt_mqtt_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, WriteOpts));
        _tempFiles.Add(path);
        return path;
    }

    private static AppConfig ReadConfig(string path) =>
        JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), ReadOpts) ?? new AppConfig();

    private ConfigManager MakeManager(string path)
    {
        _tempFiles.Add(path);
        return new ConfigManager(path, NullLogger<ConfigManager>.Instance);
    }

    /// <summary>Raises <c>Changed</c> on the calling thread, so a test asserts what was raised rather
    /// than racing the thread pool for it. The production default is deliberately NOT this — see
    /// <see cref="Changed_IsRaisedOffTheThreadThatWrote"/>.</summary>
    private static MqttConfigStore InlineStore(ConfigManager config) => new(config, work => work());

    /// <summary>A config with something in every section, so a write that drops one is visible.</summary>
    private static AppConfig PopulatedConfig() => new()
    {
        VirtualMachines = [new VmTarget { Name = "Dev", NicName = "Network Adapter",
                                          OnBridgeLostAction = "pause", OnBridgeLostDelaySeconds = 30 }],
        Rules           = [new NetworkRule { Name = "Office", Priority = 1, VirtualSwitch = "Bridged",
                                             TargetVms = ["Dev"], AutoStart = true,
                                             Conditions = new RuleConditions { IpCidr = "10.0.0.0/24" } }],
        Fallback        = new FallbackAction { VirtualSwitch = "Default Switch", TargetVms = ["Dev"] },
        AdapterNames    = [new AdapterNameOverride { DeviceInstanceId = "USB\\VID_0BDA&PID_8153\\0001",
                                                     OriginalFriendlyName = "Realtek USB GbE",
                                                     CurrentFriendlyName = "Dock LAN" }],
        LogLevel             = LogLevel.Warning,
        SettingsWindowX      = 120,
        SettingsWindowY      = 80,
        SettingsWindowWidth  = 1000,
        SettingsWindowHeight = 800,
    };

    /// <summary>Everything the MQTT write did not touch, asserted in one place so each test below says
    /// only what it is actually about.</summary>
    private static void AssertUnrelatedConfigIntact(AppConfig saved)
    {
        var vm = Assert.Single(saved.VirtualMachines);
        Assert.Equal("Dev", vm.Name);
        Assert.Equal("Network Adapter", vm.NicName);
        Assert.Equal("pause", vm.OnBridgeLostAction);
        Assert.Equal(30, vm.OnBridgeLostDelaySeconds);

        var rule = Assert.Single(saved.Rules);
        Assert.Equal("Office", rule.Name);
        Assert.Equal("Bridged", rule.VirtualSwitch);
        Assert.Equal(["Dev"], rule.TargetVms);
        Assert.True(rule.AutoStart);
        Assert.Equal("10.0.0.0/24", rule.Conditions?.IpCidr);

        Assert.Equal("Default Switch", saved.Fallback.VirtualSwitch);
        Assert.Equal(["Dev"], saved.Fallback.TargetVms);

        Assert.Equal("Dock LAN", Assert.Single(saved.AdapterNames).CurrentFriendlyName);

        Assert.Equal(LogLevel.Warning, saved.LogLevel);
        Assert.Equal(120,  saved.SettingsWindowX);
        Assert.Equal(80,   saved.SettingsWindowY);
        Assert.Equal(1000, saved.SettingsWindowWidth);
        Assert.Equal(800,  saved.SettingsWindowHeight);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { /* best-effort */ }
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ── The round trip, and everything it must not touch ─────────────────────────

    /// <summary>
    /// The one that matters. <c>ConfigManager.With(...)</c> rebuilds the WHOLE config from a fixed
    /// parameter list and the result is serialised over config.json wholesale, so a field it fails to
    /// carry through is not "left alone" — it is written back as its default and permanently lost. This
    /// asserts an MQTT write against every other section at once: a rule, a VM target, the fallback, the
    /// saved adapter names, the log level and the Settings window rect.
    /// </summary>
    [Fact]
    public void Update_WritesTheBrokerSettingsAndPreservesEveryOtherSection()
    {
        var path = WriteTempConfig(PopulatedConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        store.Update(s =>
        {
            s.Enabled = true;
            s.Host = "broker.lan";
            s.Port = 8883;
            s.Username = "hvmt";
            s.DeviceName = "Hyper-V host";
        });

        var saved = ReadConfig(path);
        Assert.True(saved.Mqtt.Settings.Enabled);
        Assert.Equal("broker.lan", saved.Mqtt.Settings.Host);
        Assert.Equal(8883, saved.Mqtt.Settings.Port);
        Assert.Equal("hvmt", saved.Mqtt.Settings.Username);
        Assert.Equal("Hyper-V host", saved.Mqtt.Settings.DeviceName);
        AssertUnrelatedConfigIntact(saved);
    }

    /// <summary>The other direction, and the one a reader of <c>With(...)</c> is likeliest to break: an
    /// unrelated write must carry the <c>mqtt</c> section through untouched. Omitting it there would
    /// blank a configured broker every time the log level changed.</summary>
    [Fact]
    public void AnUnrelatedWrite_PreservesTheMqttSection()
    {
        var path = WriteTempConfig(PopulatedConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);
        store.Update(s => { s.Enabled = true; s.Host = "broker.lan"; s.Username = "hvmt"; });
        store.RememberEndpoint(new MqttEndpointMemory("broker.lan", "hvmt", 8883, MqttTransport.Tcp, true));
        store.SetPowerButtons(true);

        config.UpdateLogLevel(LogLevel.Error);
        config.SaveSettingsWindowRect(new WindowRect(10, 20, 900, 700));
        config.AddBridgedRule(new NetworkRule { Name = "Home", Priority = 2, VirtualSwitch = "Bridged2" });

        var saved = ReadConfig(path);
        Assert.True(saved.Mqtt.Settings.Enabled);
        Assert.Equal("broker.lan", saved.Mqtt.Settings.Host);
        Assert.Equal("hvmt", saved.Mqtt.Settings.Username);
        Assert.Equal(8883, saved.Mqtt.Endpoint?.Port);
        Assert.True(saved.Mqtt.PowerButtons);
    }

    /// <summary>Group state rides in the settings block, so it survives every unrelated write for the
    /// same reason — and a lost group state silently re-enables the metrics WMI loop.</summary>
    [Fact]
    public void AnUnrelatedWrite_PreservesTheStoredGroupState()
    {
        var path = WriteTempConfig(PopulatedConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);
        store.Update(s => s.Groups["metrics"] = true);

        config.UpdateLogLevel(LogLevel.Error);

        Assert.True(ReadConfig(path).Mqtt.Settings.Groups["metrics"]);
    }

    /// <summary>A config written before the section existed, or one hand-edited to <c>"mqtt": null</c>,
    /// must load as "configured, disabled" rather than crash every consumer of it.</summary>
    [Theory]
    [InlineData("""{ "logLevel": "Debug" }""")]
    [InlineData("""{ "logLevel": "Debug", "mqtt": null }""")]
    [InlineData("""{ "logLevel": "Debug", "mqtt": { "settings": null } }""")]
    public void AConfigWithNoUsableMqttSection_LoadsAsConfiguredAndDisabled(string json)
    {
        var path = Path.Combine(_tempDir, $"hvmt_mqtt_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        using var config = MakeManager(path);

        Assert.NotNull(config.Current.Mqtt);
        Assert.NotNull(config.Current.Mqtt.Settings);
        Assert.False(config.Current.Mqtt.Settings.Enabled);
        Assert.Null(config.Current.Mqtt.Endpoint);
    }

    [Fact]
    public void Read_HandsBackACopy_SoStagedEditsCannotReachTheLiveConfig()
    {
        var path = WriteTempConfig(new AppConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        var staged = store.Read();
        staged.Host = "typed-but-not-applied";
        staged.Groups["metrics"] = true;

        Assert.Equal("", store.Read().Host);
        Assert.Empty(store.Read().Groups);
        Assert.Equal("", config.Current.Mqtt.Settings.Host);
    }

    /// <summary>Nothing changed, so nothing is written: a settings panel that commits on every control
    /// edit would otherwise rewrite config.json for each keystroke that left a value where it was.</summary>
    [Fact]
    public void Update_WritesNothing_WhenTheSectionComesOutUnchanged()
    {
        var path = WriteTempConfig(new AppConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);
        store.Update(s => s.Host = "broker.lan");

        var reloads = 0;
        config.ConfigReloaded += (_, _) => reloads++;
        var written = File.GetLastWriteTimeUtc(path);

        store.Update(s => s.Host = "broker.lan");   // the value it already holds

        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(0, reloads);
    }

    // ── The endpoint memory is state, not a setting ─────────────────────────────

    [Fact]
    public void RememberEndpoint_RoundTripsThroughRecall()
    {
        var path = WriteTempConfig(PopulatedConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        var memory = new MqttEndpointMemory("broker.lan", "hvmt", 8883, MqttTransport.WebSocket, true);
        store.RememberEndpoint(memory);

        Assert.Equal(memory, store.RecallEndpoint());
        Assert.Equal(memory, ReadConfig(path).Mqtt.Endpoint);
        AssertUnrelatedConfigIntact(ReadConfig(path));
    }

    [Fact]
    public void RecallEndpoint_IsNullBeforeAnyConnectHasSucceeded()
    {
        var path = WriteTempConfig(new AppConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        Assert.Null(store.RecallEndpoint());
    }

    /// <summary>
    /// The rule the endpoint memory exists to keep. It is deliberately outside <see cref="MqttSettings"/>
    /// so recording a successful connect is not a settings change — a consumer that re-applies its
    /// connection on <c>Changed</c> would otherwise reconnect on the strength of its own success, for
    /// ever.
    /// </summary>
    [Fact]
    public void RememberEndpoint_RaisesNoSettingsChange()
    {
        var path = WriteTempConfig(new AppConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        var changes = 0;
        store.Changed += () => changes++;

        store.RememberEndpoint(new MqttEndpointMemory("broker.lan", "hvmt", 8883, MqttTransport.Tcp, true));
        store.RememberEndpoint(new MqttEndpointMemory("broker.lan", "hvmt", 1883, MqttTransport.Tcp, false));

        Assert.Equal(0, changes);
        Assert.Equal(1883, store.RecallEndpoint()?.Port);   // …but it was recorded
    }

    // ── The power shape is this app's setting, not the module's ─────────────────

    /// <summary>Off is the default and the shape an existing installation keeps: the buttons are opt-in,
    /// and a config written before the field existed must not read as having opted in.</summary>
    [Theory]
    [InlineData("""{ "logLevel": "Debug" }""")]
    [InlineData("""{ "logLevel": "Debug", "mqtt": { "settings": { "enabled": true } } }""")]
    public void PowerButtons_AreOffForAConfigThatNeverNamedThem(string json)
    {
        var path = Path.Combine(_tempDir, $"hvmt_mqtt_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        Assert.False(store.PowerButtons);
    }

    [Fact]
    public void SetPowerButtons_RoundTripsThroughTheStoreAndTheFile()
    {
        var path = WriteTempConfig(PopulatedConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        store.SetPowerButtons(true);

        Assert.True(store.PowerButtons);
        Assert.True(ReadConfig(path).Mqtt.PowerButtons);
        AssertUnrelatedConfigIntact(ReadConfig(path));

        store.SetPowerButtons(false);

        Assert.False(store.PowerButtons);
        Assert.False(ReadConfig(path).Mqtt.PowerButtons);
    }

    /// <summary>
    /// <c>UpdateMqtt</c> mutates a <see cref="MqttSection.Copy"/> and writes the result back whole, so a
    /// field the copy forgets is not left alone — it is written back as its default and lost. Asserted
    /// both ways: a broker write must not blank the power shape, and a power-shape write must not blank
    /// the broker settings or the endpoint memory.
    /// </summary>
    [Fact]
    public void APowerShapeWriteAndABrokerWrite_EachPreserveTheOther()
    {
        var path = WriteTempConfig(PopulatedConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        store.SetPowerButtons(true);
        store.Update(s => { s.Enabled = true; s.Host = "broker.lan"; s.Username = "hvmt"; });
        store.RememberEndpoint(new MqttEndpointMemory("broker.lan", "hvmt", 8883, MqttTransport.Tcp, true));

        var saved = ReadConfig(path);
        Assert.True(saved.Mqtt.PowerButtons);
        Assert.Equal("broker.lan", saved.Mqtt.Settings.Host);
        Assert.Equal(8883, saved.Mqtt.Endpoint?.Port);

        store.SetPowerButtons(false);

        saved = ReadConfig(path);
        Assert.False(saved.Mqtt.PowerButtons);
        Assert.True(saved.Mqtt.Settings.Enabled);
        Assert.Equal("broker.lan", saved.Mqtt.Settings.Host);
        Assert.Equal("hvmt", saved.Mqtt.Settings.Username);
        Assert.Equal(8883, saved.Mqtt.Endpoint?.Port);
    }

    /// <summary>The power shape is outside <see cref="MqttSettings"/> for the reason the endpoint memory
    /// is: a consumer that re-applies its connection on <c>Changed</c> must not bounce the socket because
    /// the entity table was rebuilt. The rebuild rides on the config reload instead.</summary>
    [Fact]
    public void SetPowerButtons_RaisesNoSettingsChange()
    {
        var path = WriteTempConfig(new AppConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        var changes = 0;
        store.Changed += () => changes++;

        store.SetPowerButtons(true);
        store.SetPowerButtons(false);

        Assert.Equal(0, changes);
    }

    // ── Changed ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Changed_IsRaisedWhenTheBrokerSettingsMove()
    {
        var path = WriteTempConfig(new AppConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        var changes = 0;
        store.Changed += () => changes++;

        store.Update(s => s.Host = "broker.lan");
        Assert.Equal(1, changes);

        store.Update(s => s.Username = "hvmt");
        Assert.Equal(2, changes);
    }

    /// <summary>A config write that touched nothing in this section raises nothing: the module would
    /// otherwise re-apply its connection every time the log level changed.</summary>
    [Fact]
    public void Changed_IsNotRaisedForAWriteThatMissedThisSection()
    {
        var path = WriteTempConfig(new AppConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        var changes = 0;
        store.Changed += () => changes++;

        config.UpdateLogLevel(LogLevel.Error);
        config.SaveSettingsWindowRect(new WindowRect(10, 20, 900, 700));
        config.AddBridgedRule(new NetworkRule { Name = "Home", Priority = 2, VirtualSwitch = "Bridged2" });

        Assert.Equal(0, changes);
    }

    /// <summary>
    /// <c>ConfigReloaded</c> fires while <c>ConfigManager</c> holds its save lock, so a subscriber that
    /// does real work — re-applying a broker connection — would block every other config write in the
    /// app behind it. The notification therefore has to leave the writing thread, which is also what the
    /// module's own contract requires: <c>Changed</c> must not fire while the store's write lock is held.
    /// </summary>
    [Fact]
    public void Changed_IsRaisedOffTheThreadThatWrote()
    {
        var path = WriteTempConfig(new AppConfig());
        using var config = MakeManager(path);
        using var store = new MqttConfigStore(config);   // the production raise, not the inline one

        int writer = Environment.CurrentManagedThreadId;
        int handler = 0;
        using var raised = new SemaphoreSlim(0);
        store.Changed += () => { handler = Environment.CurrentManagedThreadId; raised.Release(); };

        store.Update(s => s.Host = "broker.lan");

        Assert.True(raised.Wait(TimeSpan.FromSeconds(10)), "Changed was never raised");
        Assert.NotEqual(writer, handler);
    }

    /// <summary>A hand-edit to config.json is a settings change like any other — the module has to
    /// re-apply for it exactly as it would for a write from the panel.</summary>
    [Fact]
    public async Task Changed_IsRaisedForAHandEditToConfigJson()
    {
        var path = WriteTempConfig(new AppConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        using var raised = new SemaphoreSlim(0);
        store.Changed += () => raised.Release();

        var edited = new AppConfig();
        edited.Mqtt.Settings.Host = "broker.lan";
        File.WriteAllText(path, JsonSerializer.Serialize(edited, WriteOpts));

        Assert.True(await raised.WaitAsync(TimeSpan.FromSeconds(10)), "the file watcher never fired");
    }

    [Fact]
    public void Dispose_StopsTheStoreListeningToTheConfig()
    {
        var path = WriteTempConfig(new AppConfig());
        using var config = MakeManager(path);
        var store = InlineStore(config);

        var changes = 0;
        store.Changed += () => changes++;
        store.Dispose();

        config.UpdateMqtt(section => section.Settings.Host = "broker.lan");

        Assert.Equal(0, changes);
        Assert.Equal("broker.lan", config.Current.Mqtt.Settings.Host);   // the write still happened
    }

    /// <summary>What counts as the settings having moved is decided by serialising them, not by
    /// comparing a field list — a field the module adds later is covered without this app being touched.
    /// The two halves are asserted together because either alone passes for the wrong reason.</summary>
    [Fact]
    public void Fingerprint_MovesWithEveryFieldAndWithNothingElse()
    {
        var settings = new MqttSettings();
        var baseline = MqttConfigStore.Fingerprint(settings);

        Assert.Equal(baseline, MqttConfigStore.Fingerprint(settings.Copy()));

        foreach (var edit in new Action<MqttSettings>[]
                 {
                     s => s.Enabled = true,
                     s => s.Host = "broker.lan",
                     s => s.Port = 8883,
                     s => s.TransportMode = MqttTransportMode.WebSocket,
                     s => s.EncryptionMode = MqttEncryptionMode.On,
                     s => s.CertificateTrust = MqttCertificateTrust.ForThumbprint("AA BB CC"),
                     s => s.Username = "hvmt",
                     s => s.Password = "secret",
                     s => s.DeviceId = "hvmt-host",
                     s => s.DeviceName = "Hyper-V host",
                     s => s.DiscoveryPrefix = "ha",
                     s => s.Groups["metrics"] = true,
                 })
        {
            var moved = settings.Copy();
            edit(moved);
            Assert.NotEqual(baseline, MqttConfigStore.Fingerprint(moved));
        }
    }

    /// <summary>The fingerprint is retained for the life of the store and compared on every config
    /// reload, so the broker password must not be in it: a clear-text secret held in a long-lived
    /// string outlives every place the settings themselves are read and dropped. Both halves again —
    /// absent from the string, and still moving the comparison when it changes.</summary>
    [Fact]
    public void Fingerprint_MovesWithThePasswordWithoutCarryingIt()
    {
        static MqttSettings With(string password) =>
            new() { Host = "broker.lan", Username = "hvmt", Password = password };

        var fingerprint = MqttConfigStore.Fingerprint(With("hunter2"));

        Assert.DoesNotContain("hunter2", fingerprint, StringComparison.Ordinal);
        Assert.NotEqual(fingerprint, MqttConfigStore.Fingerprint(With("hunter3")));
        Assert.NotEqual(fingerprint, MqttConfigStore.Fingerprint(With("")));
        Assert.Equal(fingerprint, MqttConfigStore.Fingerprint(With("hunter2")));
    }

    // ── The mqtt section is not a network change (issue #49) ─────────────────────

    /// <summary>
    /// <c>NonNetworkProperties</c> is an EXCLUSION list: everything not named there lands in the
    /// comparison and re-evaluates the network. So the assertion has to run both ways — an mqtt-only
    /// move must be false, and the same configs with a rule edit on top must still be true, or the test
    /// would pass against a list that excluded everything.
    /// </summary>
    [Fact]
    public void AffectsNetwork_IsFalseForAnMqttOnlyChangeAndTrueWithARuleEditOnTop()
    {
        static AppConfig Base() => new()
        {
            Rules = [new NetworkRule { Name = "Office", Priority = 1, VirtualSwitch = "Bridged" }],
        };

        var before = Base();
        var after = Base();
        after.Mqtt.Settings.Enabled = true;
        after.Mqtt.Settings.Host = "broker.lan";
        after.Mqtt.Settings.Groups["metrics"] = true;
        after.Mqtt.Endpoint = new MqttEndpointMemory("broker.lan", "hvmt", 8883, MqttTransport.Tcp, true);

        Assert.False(ConfigManager.AffectsNetwork(before, after));

        after.Rules[0].VirtualSwitch = "Bridged2";
        Assert.True(ConfigManager.AffectsNetwork(before, after));
    }

    /// <summary>First of the two save paths: an in-app write. Storing broker settings must never
    /// schedule a network re-evaluation — that pass enumerates every NIC on the host and can reach
    /// <c>UpdateSwitchBindingAsync</c>, i.e. it can move a real VM's switch.</summary>
    [Fact]
    public void AnInAppMqttWrite_DoesNotAffectTheNetwork()
    {
        var path = WriteTempConfig(PopulatedConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        var affected = new List<bool>();
        config.ConfigReloaded += (_, e) => affected.Add(e.AffectsNetwork);

        store.Update(s => { s.Enabled = true; s.Host = "broker.lan"; });
        store.RememberEndpoint(new MqttEndpointMemory("broker.lan", "hvmt", 8883, MqttTransport.Tcp, true));
        Assert.Equal([false, false], affected);

        // …while a change the monitor actually acts on still says so, from the same subscription.
        config.AddBridgedRule(new NetworkRule { Name = "Home", Priority = 2, VirtualSwitch = "Bridged2" });
        Assert.Equal([false, false, true], affected);
    }

    /// <summary>Second save path: the debounced file watcher. A hand-edit is classified by exactly the
    /// same rule as an in-app write, so editing the broker host by hand must no more move a VM's switch
    /// than the settings panel that writes the same field.</summary>
    [Fact]
    public async Task AHandEditToTheMqttSection_DoesNotAffectTheNetwork()
    {
        var path = WriteTempConfig(PopulatedConfig());
        using var config = MakeManager(path);

        var affected = new List<bool>();
        using var seen = new SemaphoreSlim(0);
        config.ConfigReloaded += (_, e) => { affected.Add(e.AffectsNetwork); seen.Release(); };

        var edited = PopulatedConfig();
        edited.Mqtt.Settings.Enabled = true;
        edited.Mqtt.Settings.Host = "broker.lan";
        File.WriteAllText(path, JsonSerializer.Serialize(edited, WriteOpts));
        Assert.True(await seen.WaitAsync(TimeSpan.FromSeconds(10)), "the file watcher never fired");
        Assert.Equal([false], affected);

        // …and one the monitor must act on, through the same path.
        edited.Rules[0].VirtualSwitch = "Bridged2";
        File.WriteAllText(path, JsonSerializer.Serialize(edited, WriteOpts));
        Assert.True(await seen.WaitAsync(TimeSpan.FromSeconds(10)), "the file watcher never fired");
        Assert.Equal([false, true], affected);
    }

    // ── The snapshot is built inside the save lock (the issue #31 bug class) ──────

    /// <summary>
    /// An ILogger that runs a callback the first time it logs a message containing a given fragment.
    /// ConfigManager writes its success line INSIDE the save lock, after the file is written but before
    /// the read-back updates <c>_config</c> — precisely the window in which another thread's snapshot of
    /// <c>_config</c> is stale. Hooking it makes the interleaving below deterministic rather than a
    /// timing lottery, and exposes nothing production-only to do it.
    /// </summary>
    private sealed class HookLogger(string fragment, Action hook) : ILogger
    {
        private int _fired;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains(fragment, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
                hook();
        }
    }

    /// <summary>
    /// The lost update, in the direction the MQTT integration adds. <c>UpdateMqtt</c> builds its snapshot
    /// with <c>With(mqtt: …)</c>, i.e. by READING the live config, and the file is then written whole —
    /// so a snapshot built before the save lock is taken writes a stale copy of every OTHER field back
    /// over whatever landed meanwhile. Here a successful connect records its endpoint at the moment the
    /// user's NIC-name edit is committing, and the NIC edit disappears from disk with nothing said.
    ///
    /// <para>The interleaving is forced, not raced: the MQTT write is launched from inside the NIC
    /// save's own lock, at the one moment when <c>_config</c> is still stale.</para>
    /// </summary>
    [Fact]
    public async Task AnMqttWriteCannotClobberAConcurrentNicEdit()
    {
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "vm1", NicName = "Network Adapter" }],
        });

        ConfigManager? config = null;
        Task? mqttWrite = null;

        // Fires inside SetVmNicName's lock: file written, _config NOT yet reloaded.
        var logger = new HookLogger("NIC name for VM 'vm1'", () =>
        {
            mqttWrite = Task.Run(() => config!.UpdateMqtt(s => s.Settings.Host = "broker.lan"));
            // Long enough for the MQTT write to reach its snapshot/lock. If it snapshots here it
            // snapshots a config that still says "Network Adapter" — and then writes that back.
            Thread.Sleep(250);
        });

        using (config = new ConfigManager(path, logger))
        {
            config.SetVmNicName("vm1", "Ethernet 2");
            await mqttWrite!.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var onDisk = ReadConfig(path);
        Assert.Equal("Ethernet 2", Assert.Single(onDisk.VirtualMachines).NicName);   // the edit survived
        Assert.Equal("broker.lan", onDisk.Mqtt.Settings.Host);                       // and so did the broker
    }

    /// <summary>
    /// The same rule applied to the section itself, which is the case the module's own store contract
    /// names: <c>Update</c> is a read-modify-write against the LIVE state, so a caller holding a stale
    /// snapshot commits one field without rolling back what a sibling changed meanwhile. Here the panel
    /// commits a device name while a successful connect records its endpoint; if the section were copied
    /// before the lock, the second write would revert the first.
    /// </summary>
    [Fact]
    public async Task TwoOverlappingMqttWritesBothLand()
    {
        var path = WriteTempConfig(new AppConfig());

        ConfigManager? config = null;
        Task? second = null;

        // Fires inside the first UpdateMqtt's lock: file written, _config NOT yet reloaded.
        var logger = new HookLogger("MQTT settings saved to", () =>
        {
            second = Task.Run(() => config!.UpdateMqtt(s => s.Settings.DeviceName = "Hyper-V host"));
            Thread.Sleep(250);
        });

        using (config = new ConfigManager(path, logger))
        {
            config.UpdateMqtt(s => s.Settings.Host = "broker.lan");
            await second!.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var onDisk = ReadConfig(path);
        Assert.Equal("broker.lan",   onDisk.Mqtt.Settings.Host);
        Assert.Equal("Hyper-V host", onDisk.Mqtt.Settings.DeviceName);
    }

    /// <summary>Overlapping writes queue rather than collide, and the last one in sees everything the
    /// ones before it wrote.</summary>
    [Fact]
    public void ConcurrentMqttWritesAllLand()
    {
        var path = WriteTempConfig(new AppConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        Parallel.For(0, 16, i => store.Update(s => s.Groups[$"g{i}"] = true));

        var saved = ReadConfig(path).Mqtt.Settings.Groups;
        Assert.All(Enumerable.Range(0, 16), i => Assert.True(saved[$"g{i}"]));
    }

    [Fact]
    public void Update_RejectsANullMutator()
    {
        var path = WriteTempConfig(new AppConfig());
        using var config = MakeManager(path);
        using var store = InlineStore(config);

        Assert.Throws<ArgumentNullException>(() => store.Update(null!));
        Assert.Throws<ArgumentNullException>(() => config.UpdateMqtt(null!));
    }
}
