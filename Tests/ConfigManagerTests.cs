using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Tests for ConfigManager file-write operations.
/// Each test writes a real temp file so we exercise the actual serialisation path.
/// </summary>
public class ConfigManagerTests : IDisposable
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string WriteTempConfig(AppConfig cfg)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvmt_test_{Guid.NewGuid():N}.json");
        var writeOpts = new JsonSerializerOptions
        {
            WriteIndented          = true,
            PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters             = { new JsonStringEnumConverter() }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, writeOpts));
        return path;
    }

    private static AppConfig ReadConfig(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, ReadOpts) ?? new AppConfig();
    }

    /// <summary>Writes a raw JSON string verbatim (to simulate a hand-edited, non-canonical config).</summary>
    private string WriteRawTempConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvmt_test_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }

    private readonly List<string> _tempFiles = [];

    private ConfigManager MakeManager(string path)
    {
        _tempFiles.Add(path);
        return new ConfigManager(path, NullLogger<ConfigManager>.Instance);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { /* best-effort */ }
    }

    // ── AddBridgedRule ────────────────────────────────────────────────────────

    [Fact]
    public void AddBridgedRule_AppendsRuleToFile()
    {
        var initial = new AppConfig
        {
            Fallback = new FallbackAction { VirtualSwitch = "Default Switch" }
        };
        var path = WriteTempConfig(initial);
        using var mgr = MakeManager(path);

        var rule = new NetworkRule
        {
            Name         = "Office LAN",
            Priority     = 1,
            VirtualSwitch = "Bridged",
            TargetVms    = ["TestVM"],
            Conditions   = new RuleConditions { AdapterMac = "AA:BB:CC:DD:EE:FF" }
        };

        mgr.AddBridgedRule(rule);

        var saved = ReadConfig(path);
        var added = Assert.Single(saved.Rules);
        Assert.Equal("Office LAN", added.Name);
        Assert.Equal("Bridged",    added.VirtualSwitch);
        Assert.Equal(["TestVM"],   added.TargetVms);
    }

    [Fact]
    public void AddBridgedRule_PreservesExistingRulesAndFallback()
    {
        var initial = new AppConfig
        {
            Rules    = [ new NetworkRule { Name = "Existing", Priority = 10, VirtualSwitch = "OldSwitch" } ],
            Fallback = new FallbackAction { VirtualSwitch = "Default Switch", TargetVms = ["VM1"] }
        };
        var path = WriteTempConfig(initial);
        using var mgr = MakeManager(path);

        mgr.AddBridgedRule(new NetworkRule { Name = "New", Priority = 5, VirtualSwitch = "Bridged" });

        var saved = ReadConfig(path);
        Assert.Equal(2, saved.Rules.Count);
        Assert.Contains(saved.Rules, r => r.Name == "Existing");
        Assert.Contains(saved.Rules, r => r.Name == "New");
        Assert.Equal("Default Switch", saved.Fallback.VirtualSwitch);
        Assert.Equal(["VM1"],          saved.Fallback.TargetVms);
    }

    [Fact]
    public void AddBridgedRule_UpdatesInMemoryConfig()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.AddBridgedRule(new NetworkRule { Name = "Test", VirtualSwitch = "Bridged" });

        Assert.Single(mgr.Current.Rules);
        Assert.Equal("Test", mgr.Current.Rules[0].Name);
    }

    // ── AddVmToConfig ─────────────────────────────────────────────────────────

    [Fact]
    public void AddVmToConfig_AddsVmToFile()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.AddVmToConfig("TestVM", "Network Adapter");

        var saved = ReadConfig(path);
        var vm = Assert.Single(saved.VirtualMachines);
        Assert.Equal("TestVM",          vm.Name);
        Assert.Equal("Network Adapter", vm.NicName);
    }

    [Fact]
    public void AddVmToConfig_NoDuplicate_WhenCalledTwiceWithSameName()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.AddVmToConfig("TestVM", "Network Adapter");
        mgr.AddVmToConfig("TestVM", "Network Adapter"); // second call — must be idempotent

        var saved = ReadConfig(path);
        Assert.Single(saved.VirtualMachines);
    }

    [Fact]
    public void AddVmToConfig_CaseInsensitiveDuplicateCheck()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.AddVmToConfig("testvm", "Network Adapter");
        mgr.AddVmToConfig("TESTVM", "Network Adapter"); // same name, different case

        var saved = ReadConfig(path);
        Assert.Single(saved.VirtualMachines);
    }

    [Fact]
    public void AddVmToConfig_BlankNicName_DefaultsToNetworkAdapter()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.AddVmToConfig("MyVM", "   "); // whitespace-only nicName

        var saved = ReadConfig(path);
        Assert.Equal("Network Adapter", saved.VirtualMachines[0].NicName);
    }

    [Fact]
    public void AddVmToConfig_UpdatesInMemoryConfig()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.AddVmToConfig("TestVM", "Network Adapter");

        Assert.Single(mgr.Current.VirtualMachines);
        Assert.Equal("TestVM", mgr.Current.VirtualMachines[0].Name);
    }

    // ── UpdateLogLevel (issue #18) ──────────────────────────────────────────────

    [Fact]
    public void UpdateLogLevel_PersistsNewLevel()
    {
        var path = WriteTempConfig(new AppConfig { LogLevel = Microsoft.Extensions.Logging.LogLevel.Debug });
        using var mgr = MakeManager(path);

        mgr.UpdateLogLevel(Microsoft.Extensions.Logging.LogLevel.Warning);

        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, ReadConfig(path).LogLevel);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, mgr.Current.LogLevel);
    }

    [Fact]
    public void UpdateLogLevel_PreservesVmsAndRules()
    {
        var initial = new AppConfig
        {
            VirtualMachines = [ new VmTarget { Name = "Alpha", NicName = "NIC 1" } ],
            Rules           = [ new NetworkRule { Name = "R", Priority = 1, VirtualSwitch = "Bridged" } ],
            Fallback        = new FallbackAction { VirtualSwitch = "Default Switch", TargetVms = ["Alpha"] },
            LogLevel        = Microsoft.Extensions.Logging.LogLevel.Debug,
        };
        var path = WriteTempConfig(initial);
        using var mgr = MakeManager(path);

        mgr.UpdateLogLevel(Microsoft.Extensions.Logging.LogLevel.Error);

        var saved = ReadConfig(path);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, saved.LogLevel);
        Assert.Single(saved.VirtualMachines);
        Assert.Single(saved.Rules);
        Assert.Equal("Default Switch", saved.Fallback.VirtualSwitch);
    }

    // ── SetVmBridgeLostAction (issue #18) ───────────────────────────────────────

    [Fact]
    public void SetVmBridgeLostAction_PersistsActionAndDelay()
    {
        var initial = new AppConfig
        {
            VirtualMachines = [ new VmTarget { Name = "Alpha" } ],
        };
        var path = WriteTempConfig(initial);
        using var mgr = MakeManager(path);

        mgr.SetVmBridgeLostAction("Alpha", "pause", 60);

        var vm = Assert.Single(ReadConfig(path).VirtualMachines);
        Assert.Equal("pause", vm.OnBridgeLostAction);
        Assert.Equal(60,       vm.OnBridgeLostDelaySeconds);
    }

    [Fact]
    public void SetVmBridgeLostAction_CaseInsensitiveNameMatch()
    {
        var path = WriteTempConfig(new AppConfig { VirtualMachines = [ new VmTarget { Name = "Alpha" } ] });
        using var mgr = MakeManager(path);

        mgr.SetVmBridgeLostAction("alpha", "save", 10);

        Assert.Equal("save", ReadConfig(path).VirtualMachines[0].OnBridgeLostAction);
    }

    [Fact]
    public void SetVmBridgeLostAction_NullAction_ClearsIt()
    {
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [ new VmTarget { Name = "Alpha", OnBridgeLostAction = "shutdown", OnBridgeLostDelaySeconds = 5 } ],
        });
        using var mgr = MakeManager(path);

        mgr.SetVmBridgeLostAction("Alpha", null, 30);

        var vm = ReadConfig(path).VirtualMachines[0];
        Assert.Null(vm.OnBridgeLostAction);
        Assert.Equal(30, vm.OnBridgeLostDelaySeconds);
    }

    [Fact]
    public void SetVmBridgeLostAction_UnknownVm_IsNoOp()
    {
        var path = WriteTempConfig(new AppConfig { VirtualMachines = [ new VmTarget { Name = "Alpha" } ] });
        using var mgr = MakeManager(path);

        mgr.SetVmBridgeLostAction("Missing", "pause", 10);   // must not throw or add a VM

        var saved = ReadConfig(path);
        var vm = Assert.Single(saved.VirtualMachines);
        Assert.Equal("Alpha", vm.Name);
        Assert.Null(vm.OnBridgeLostAction);
    }

    [Fact]
    public void SetVmBridgeLostAction_NegativeDelay_ClampedToDefault()
    {
        var path = WriteTempConfig(new AppConfig { VirtualMachines = [ new VmTarget { Name = "Alpha" } ] });
        using var mgr = MakeManager(path);

        mgr.SetVmBridgeLostAction("Alpha", "pause", -5);   // hand-edited nonsense → default 30

        Assert.Equal(30, ReadConfig(path).VirtualMachines[0].OnBridgeLostDelaySeconds);
    }

    [Fact]
    public void SetVmBridgeLostAction_HugeDelay_CappedToDayMax()
    {
        var path = WriteTempConfig(new AppConfig { VirtualMachines = [ new VmTarget { Name = "Alpha" } ] });
        using var mgr = MakeManager(path);

        mgr.SetVmBridgeLostAction("Alpha", "pause", 999_999);   // capped to a sane [0, 86400]

        Assert.Equal(86_400, ReadConfig(path).VirtualMachines[0].OnBridgeLostDelaySeconds);
    }

    [Fact]
    public void SetVmBridgeLostAction_DoesNotMutateLiveVmOnUnchangedNoOp()
    {
        // The live VmTarget must never be mutated in place: a no-op call leaves Current's instance
        // exactly as loaded (a failed write elsewhere can't arm a stale destructive action).
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [ new VmTarget { Name = "Alpha", OnBridgeLostAction = "pause", OnBridgeLostDelaySeconds = 30 } ],
        });
        using var mgr = MakeManager(path);
        var before = mgr.Current.VirtualMachines[0];

        mgr.SetVmBridgeLostAction("Alpha", "pause", 30);   // identical to stored → no-op

        Assert.Same(before, mgr.Current.VirtualMachines[0]);   // no reload, same instance
        Assert.Equal("pause", before.OnBridgeLostAction);
    }

    // ── SaveRules (issue #23) ───────────────────────────────────────────────────

    [Fact]
    public void SaveRules_ReplacesRulesAndSanitises()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.SaveRules(
        [
            new NetworkRule
            {
                Name          = "  Office  ",
                Priority      = -5,                       // clamped to 0
                VirtualSwitch = "  Bridged  ",
                TargetVms     = ["VM1", " VM1 ", "VM2"],  // deduped + trimmed
                Conditions    = new RuleConditions { AdapterMac = "aa-bb-cc-dd-ee-ff", IpCidr = " 10.0.0.0/23 " },
            }
        ]);

        var rule = Assert.Single(ReadConfig(path).Rules);
        Assert.Equal("Office", rule.Name);
        Assert.Equal(0, rule.Priority);
        Assert.Equal("Bridged", rule.VirtualSwitch);
        Assert.Equal(["VM1", "VM2"], rule.TargetVms);
        Assert.Equal("AA:BB:CC:DD:EE:FF", rule.Conditions.AdapterMac);   // canonicalised
        Assert.Equal("10.0.0.0/23", rule.Conditions.IpCidr);             // trimmed
    }

    [Fact]
    public void SaveRules_PreservesVmsFallbackAndLogLevel()
    {
        var initial = new AppConfig
        {
            VirtualMachines = [ new VmTarget { Name = "Alpha" } ],
            Fallback        = new FallbackAction { VirtualSwitch = "Default Switch", TargetVms = ["Alpha"] },
            LogLevel        = Microsoft.Extensions.Logging.LogLevel.Warning,
        };
        var path = WriteTempConfig(initial);
        using var mgr = MakeManager(path);

        mgr.SaveRules([ new NetworkRule { Name = "R", Priority = 1, VirtualSwitch = "Bridged" } ]);

        var saved = ReadConfig(path);
        Assert.Single(saved.Rules);
        Assert.Single(saved.VirtualMachines);
        Assert.Equal("Default Switch", saved.Fallback.VirtualSwitch);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, saved.LogLevel);
    }

    [Fact]
    public void SaveRules_EmptyList_ClearsRules()
    {
        var path = WriteTempConfig(new AppConfig
        {
            Rules = [ new NetworkRule { Name = "Old", VirtualSwitch = "X" } ],
        });
        using var mgr = MakeManager(path);

        mgr.SaveRules([]);

        Assert.Empty(ReadConfig(path).Rules);
    }

    [Fact]
    public void SaveRules_BlankMacAndCidr_BecomeNull()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.SaveRules(
        [
            new NetworkRule { Name = "R", VirtualSwitch = "X", Conditions = new RuleConditions { AdapterMac = "  ", IpCidr = "" } }
        ]);

        var rule = Assert.Single(ReadConfig(path).Rules);
        Assert.Null(rule.Conditions.AdapterMac);
        Assert.Null(rule.Conditions.IpCidr);
    }

    [Fact]
    public void SaveRules_InvalidMacAndCidr_DroppedToNull()
    {
        // Persistence-boundary validation (finding 5): a malformed MAC/CIDR that reached SaveRules must
        // be dropped to null rather than written back verbatim — the round-trip guarantee CleanRule's
        // XML-doc now promises.
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.SaveRules(
        [
            new NetworkRule
            {
                Name          = "R",
                VirtualSwitch = "X",
                Conditions    = new RuleConditions { AdapterMac = "GG:BB:CC:DD:EE:FF", IpCidr = "10.0.0.0/99" },
            }
        ]);

        var rule = Assert.Single(ReadConfig(path).Rules);
        Assert.Null(rule.Conditions.AdapterMac);   // non-hex MAC → dropped
        Assert.Null(rule.Conditions.IpCidr);        // prefix > 32 → dropped
    }

    [Fact]
    public void SaveRules_NoChange_IsNoOpSameInstance()
    {
        var path = WriteTempConfig(new AppConfig
        {
            Rules = [ new NetworkRule { Name = "R", Priority = 1, VirtualSwitch = "X", TargetVms = ["A"] } ],
        });
        using var mgr = MakeManager(path);
        var before = mgr.Current.Rules;

        // Re-saving an identical (already-clean) rule set must not rewrite/reload.
        mgr.SaveRules([ new NetworkRule { Name = "R", Priority = 1, VirtualSwitch = "X", TargetVms = ["A"] } ]);

        Assert.Same(before, mgr.Current.Rules);
    }

    [Fact]
    public void SaveRules_UpdatesInMemoryConfig()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.SaveRules([ new NetworkRule { Name = "Live", Priority = 3, VirtualSwitch = "S" } ]);

        Assert.Equal("Live", Assert.Single(mgr.Current.Rules).Name);
    }

    // ── SetFallback (issue #23) ─────────────────────────────────────────────────

    [Fact]
    public void SetFallback_PersistsSwitchAndTargets()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.SetFallback("External", ["VM1", " VM1 ", "VM2"]);

        var fb = ReadConfig(path).Fallback;
        Assert.Equal("External", fb.VirtualSwitch);
        Assert.Equal(["VM1", "VM2"], fb.TargetVms);
    }

    [Fact]
    public void SetFallback_BlankSwitch_KeepsCurrent()
    {
        var path = WriteTempConfig(new AppConfig
        {
            Fallback = new FallbackAction { VirtualSwitch = "Default Switch" },
        });
        using var mgr = MakeManager(path);

        mgr.SetFallback("   ", ["VM1"]);   // blank switch must not overwrite with empty

        var fb = ReadConfig(path).Fallback;
        Assert.Equal("Default Switch", fb.VirtualSwitch);
        Assert.Equal(["VM1"], fb.TargetVms);
    }

    [Fact]
    public void SetFallback_NoChange_IsNoOpSameInstance()
    {
        var path = WriteTempConfig(new AppConfig
        {
            Fallback = new FallbackAction { VirtualSwitch = "Default Switch", TargetVms = ["Alpha"] },
        });
        using var mgr = MakeManager(path);
        var before = mgr.Current.Fallback;

        mgr.SetFallback("Default Switch", ["Alpha"]);

        Assert.Same(before, mgr.Current.Fallback);
    }

    [Fact]
    public void SetFallback_PreservesRulesAndVms()
    {
        var initial = new AppConfig
        {
            VirtualMachines = [ new VmTarget { Name = "Alpha" } ],
            Rules           = [ new NetworkRule { Name = "R", Priority = 1, VirtualSwitch = "Bridged" } ],
        };
        var path = WriteTempConfig(initial);
        using var mgr = MakeManager(path);

        mgr.SetFallback("NewFallback", []);

        var saved = ReadConfig(path);
        Assert.Equal("NewFallback", saved.Fallback.VirtualSwitch);
        Assert.Single(saved.Rules);
        Assert.Single(saved.VirtualMachines);
    }

    // ── Hand-edited exotic config survival (issue #24) ──────────────────────────

    // A deliberately non-canonical, hand-edited config: odd indentation, upper-case enum, a rule MAC
    // without separators, rules out of priority order, a non-preset delay, and an upper-case action.
    private const string ExoticConfigJson =
        """
        {
            "logLevel": "Warning",
            "virtualMachines": [
                { "name": "Alpha", "nicName": "Network Adapter", "onBridgeLostAction": "PAUSE", "onBridgeLostDelaySeconds": 45 }
            ],
            "rules": [
                { "name": "Second", "priority": 50, "virtualSwitch": "SwitchB", "targetVms": ["Alpha"], "conditions": { "adapterMac": "aabbccddeeff" } },
                { "name": "First",  "priority": 10, "virtualSwitch": "SwitchA", "targetVms": ["Alpha"], "conditions": { "ipCidr": "10.0.0.0/23" } }
            ],
            "fallback": { "virtualSwitch": "Default Switch", "targetVms": ["Alpha"] }
        }
        """;

    [Fact]
    public void ConstructingManager_DoesNotRewriteFile()
    {
        // "Open → touch nothing → close": Load must only READ. The file bytes must be identical after a
        // manager is constructed (and disposed) over a hand-edited config — no silent normalisation.
        var path = WriteRawTempConfig(ExoticConfigJson);
        var before = File.ReadAllBytes(path);

        using (var mgr = MakeManager(path))
        {
            // Loaded correctly (case-insensitive enum, priority-sorted in memory)…
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, mgr.Current.LogLevel);
            Assert.Equal("First", mgr.Current.Rules[0].Name);   // sorted by priority in memory only
        }

        Assert.Equal(before, File.ReadAllBytes(path));          // …but the file on disk is byte-identical
    }

    [Fact]
    public void DeliberateEdit_PreservesOtherHandEditedValuesVerbatim()
    {
        // Changing ONE value (log level) must not canonicalise the untouched rule MAC — a hand-edited
        // "aabbccddeeff" survives verbatim because the non-rule mutators copy the loaded rules as-is.
        var path = WriteRawTempConfig(ExoticConfigJson);
        using var mgr = MakeManager(path);

        mgr.UpdateLogLevel(Microsoft.Extensions.Logging.LogLevel.Error);   // the one deliberate edit

        var saved = ReadConfig(path);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, saved.LogLevel);
        var withMac = Assert.Single(saved.Rules, r => r.Name == "Second");
        Assert.Equal("aabbccddeeff", withMac.Conditions.AdapterMac);       // untouched, not canonicalised
        var vm = Assert.Single(saved.VirtualMachines);
        Assert.Equal("PAUSE", vm.OnBridgeLostAction);                      // untouched action preserved
        Assert.Equal(45,      vm.OnBridgeLostDelaySeconds);
    }

    [Fact]
    public void NoOpSetters_OverExoticConfig_DoNotRewriteFile()
    {
        // Each mutator that detects "no change" must leave a hand-edited file byte-identical when handed
        // the already-stored values (the UI's per-control commit passes exactly these on touch-nothing).
        var path = WriteRawTempConfig(ExoticConfigJson);
        using var mgr = MakeManager(path);
        var before = File.ReadAllBytes(path);

        mgr.UpdateLogLevel(Microsoft.Extensions.Logging.LogLevel.Warning);       // same level
        mgr.SetFallback("Default Switch", ["Alpha"]);                            // same fallback

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_AllFieldsPreserved()
    {
        var original = new AppConfig
        {
            VirtualMachines = [ new VmTarget { Name = "Alpha", NicName = "NIC 1" } ],
            Rules =
            [
                new NetworkRule
                {
                    Name          = "Home",
                    Priority      = 5,
                    VirtualSwitch = "ExternalSwitch",
                    TargetVms     = ["Alpha"],
                    AutoStart     = true,
                    Conditions    = new RuleConditions { AdapterMac = "AA:BB:CC:DD:EE:FF", IpCidr = "192.168.1.0/24" }
                }
            ],
            Fallback = new FallbackAction { VirtualSwitch = "Default Switch", TargetVms = ["Alpha"] }
        };

        var path = WriteTempConfig(original);
        _tempFiles.Add(path);

        var roundTripped = ReadConfig(path);

        Assert.Single(roundTripped.VirtualMachines);
        Assert.Equal("Alpha", roundTripped.VirtualMachines[0].Name);
        Assert.Equal("NIC 1", roundTripped.VirtualMachines[0].NicName);

        var rule = Assert.Single(roundTripped.Rules);
        Assert.Equal("Home",            rule.Name);
        Assert.Equal(5,                 rule.Priority);
        Assert.Equal("ExternalSwitch",  rule.VirtualSwitch);
        Assert.Equal(["Alpha"],         rule.TargetVms);
        Assert.True(rule.AutoStart);
        Assert.Equal("AA:BB:CC:DD:EE:FF", rule.Conditions.AdapterMac);
        Assert.Equal("192.168.1.0/24",    rule.Conditions.IpCidr);

        Assert.Equal("Default Switch", roundTripped.Fallback.VirtualSwitch);
        Assert.Equal(["Alpha"],        roundTripped.Fallback.TargetVms);
    }

    // ── SaveSettingsWindowRect (issue #31) ────────────────────────────────────

    [Fact]
    public void SaveSettingsWindowRect_WritesTheRectToConfigJson()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.SaveSettingsWindowRect(new WindowRect(120, 80, 1000, 800));

        var saved = ReadConfig(path);
        Assert.Equal(120,  saved.SettingsWindowX);
        Assert.Equal(80,   saved.SettingsWindowY);
        Assert.Equal(1000, saved.SettingsWindowWidth);
        Assert.Equal(800,  saved.SettingsWindowHeight);
    }

    /// <summary>
    /// The rect must come back through the SAME reflection-based serializer the app uses (camelCase on
    /// write, case-insensitive on read). If a property name or its nullability drifts, restore silently
    /// falls back to the centred default and the window "forgets" its position with no error anywhere.
    /// </summary>
    [Fact]
    public void SettingsWindowRect_RoundTripsAsCamelCaseJson()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.SaveSettingsWindowRect(new WindowRect(-1800, 40, 960, 760));

        var json = File.ReadAllText(path);
        Assert.Contains("\"settingsWindowX\": -1800", json);
        Assert.Contains("\"settingsWindowY\": 40",    json);

        // …and the live Current is refreshed from disk, not just the file.
        Assert.Equal(-1800, mgr.Current.SettingsWindowX);
        Assert.Equal(960,   mgr.Current.SettingsWindowWidth);
    }

    /// <summary>
    /// A config that predates issue #31 has no window properties at all. They must be omitted entirely
    /// (WhenWritingNull) rather than written as nulls — TryGetSavedRect reads "absent" as "never saved".
    /// </summary>
    [Fact]
    public void SettingsWindowRect_IsOmittedFromJson_WhenNeverSaved()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.UpdateLogLevel(LogLevel.Warning);

        Assert.DoesNotContain("settingsWindow", File.ReadAllText(path));
        Assert.Null(ReadConfig(path).SettingsWindowX);
    }

    /// <summary>
    /// The regression this guards: ConfigManager.With() rebuilds the WHOLE AppConfig for every write, so
    /// a field it forgets is not "left alone" — it is serialised back as null and lost for good. Before
    /// the window rect was carried through With(), merely changing the log level erased it.
    /// </summary>
    [Fact]
    public void SavingAnUnrelatedSetting_PreservesTheSettingsWindowRect()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.SaveSettingsWindowRect(new WindowRect(120, 80, 1000, 800));

        mgr.UpdateLogLevel(LogLevel.Warning);
        mgr.AddBridgedRule(new NetworkRule { Name = "New", Priority = 5, VirtualSwitch = "Bridged" });
        mgr.AddVmToConfig("Alpha", "NIC 1");

        var saved = ReadConfig(path);
        Assert.Equal(120,  saved.SettingsWindowX);
        Assert.Equal(80,   saved.SettingsWindowY);
        Assert.Equal(1000, saved.SettingsWindowWidth);
        Assert.Equal(800,  saved.SettingsWindowHeight);
        Assert.Equal(LogLevel.Warning, saved.LogLevel);
    }

    /// <summary>
    /// Closing Settings without moving the window must not rewrite config.json — an unchanged rect is a
    /// no-op, so a Closed handler can save unconditionally without churning the file (and without
    /// waking the file watcher on every open/close cycle).
    /// </summary>
    [Fact]
    public void SaveSettingsWindowRect_IsANoOp_WhenTheRectIsUnchanged()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.SaveSettingsWindowRect(new WindowRect(120, 80, 1000, 800));
        var afterFirst = File.GetLastWriteTimeUtc(path);

        mgr.SaveSettingsWindowRect(new WindowRect(120, 80, 1000, 800));

        Assert.Equal(afterFirst, File.GetLastWriteTimeUtc(path));
    }

    /// <summary>
    /// A rect write must NOT raise ConfigReloaded: the NetworkMonitor subscribes to it and would
    /// schedule a network re-evaluation — which can move a VM's switch — every time the user closed the
    /// Settings window. Current must still be refreshed, though.
    /// </summary>
    [Fact]
    public void SaveSettingsWindowRect_DoesNotRaiseConfigReloaded()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        int reloads = 0;
        mgr.ConfigReloaded += (_, _) => reloads++;

        mgr.SaveSettingsWindowRect(new WindowRect(120, 80, 1000, 800));
        Assert.Equal(0, reloads);
        Assert.Equal(120, mgr.Current.SettingsWindowX);

        // …while a real, subscriber-relevant change still does.
        mgr.UpdateLogLevel(LogLevel.Warning);
        Assert.Equal(1, reloads);
    }
}
