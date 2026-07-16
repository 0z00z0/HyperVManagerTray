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

    // ── SetVmNicName (issue #41) ────────────────────────────────────────────────
    // The NIC name was reachable ONLY by hand-editing config.json before this. These lock the editor's
    // contract: it persists, it round-trips an exotic hand-edited value, and — the trap this codebase has
    // been bitten by — it cannot silently drop a field it doesn't name (ConfigManager.With rebuilds the
    // WHOLE AppConfig per write, so anything omitted is serialised back as null and lost forever).

    [Fact]
    public void SetVmNicName_PersistsTheName()
    {
        var path = WriteTempConfig(new AppConfig { VirtualMachines = [new VmTarget { Name = "Alpha" }] });
        using var mgr = MakeManager(path);

        mgr.SetVmNicName("Alpha", "Ethernet 2");

        Assert.Equal("Ethernet 2", Assert.Single(ReadConfig(path).VirtualMachines).NicName);
    }

    [Fact]
    public void SetVmNicName_BlankRestoresTheHyperVDefault()
    {
        // An empty NIC name would match no adapter at all — worse than the default it replaced.
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "Alpha", NicName = "Ethernet 2" }],
        });
        using var mgr = MakeManager(path);

        mgr.SetVmNicName("Alpha", "   ");

        Assert.Equal("Network Adapter", Assert.Single(ReadConfig(path).VirtualMachines).NicName);
    }

    [Fact]
    public void SetVmNicName_ExoticHandEditedValueRoundTrips()
    {
        // A VM's adapter can be renamed to anything; this app is not the authority on what Hyper-V allows.
        var path = WriteTempConfig(new AppConfig { VirtualMachines = [new VmTarget { Name = "Alpha" }] });
        using var mgr = MakeManager(path);

        mgr.SetVmNicName("Alpha", "  vNIC — LAN (2,5 Gb) ");

        Assert.Equal("vNIC — LAN (2,5 Gb)", Assert.Single(ReadConfig(path).VirtualMachines).NicName);
    }

    [Fact]
    public void SetVmNicName_CaseInsensitiveNameMatch()
    {
        var path = WriteTempConfig(new AppConfig { VirtualMachines = [new VmTarget { Name = "Alpha" }] });
        using var mgr = MakeManager(path);

        mgr.SetVmNicName("alpha", "Ethernet 2");

        Assert.Equal("Ethernet 2", Assert.Single(ReadConfig(path).VirtualMachines).NicName);
    }

    [Fact]
    public void SetVmNicName_UnknownVm_IsNoOp()
    {
        var path = WriteTempConfig(new AppConfig { VirtualMachines = [new VmTarget { Name = "Alpha" }] });
        using var mgr = MakeManager(path);

        mgr.SetVmNicName("Missing", "Ethernet 2");   // must not throw or add a VM

        var vm = Assert.Single(ReadConfig(path).VirtualMachines);
        Assert.Equal("Alpha", vm.Name);
        Assert.Equal("Network Adapter", vm.NicName);
    }

    [Fact]
    public void SetVmNicName_DoesNotMutateLiveVmOnUnchangedNoOp()
    {
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "Alpha", NicName = "Ethernet 2" }],
        });
        using var mgr = MakeManager(path);
        var before = mgr.Current.VirtualMachines[0];

        mgr.SetVmNicName("Alpha", "Ethernet 2");   // identical to stored → no-op

        Assert.Same(before, mgr.Current.VirtualMachines[0]);
    }

    [Fact]
    public void SetVmNicName_PreservesTheVmsOwnOtherFields()
    {
        // A NIC edit rebuilds the VmTarget — a field dropped there silently disarms (or re-arms) a
        // destructive bridge-lost action the user configured separately.
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines =
            [
                new VmTarget { Name = "Alpha", OnBridgeLostAction = "save", OnBridgeLostDelaySeconds = 120 },
            ],
        });
        using var mgr = MakeManager(path);

        mgr.SetVmNicName("Alpha", "Ethernet 2");

        var vm = Assert.Single(ReadConfig(path).VirtualMachines);
        Assert.Equal("Ethernet 2", vm.NicName);
        Assert.Equal("save",       vm.OnBridgeLostAction);
        Assert.Equal(120,          vm.OnBridgeLostDelaySeconds);
    }

    [Fact]
    public void SetVmNicName_PreservesOtherVmsRulesFallbackLogLevelAndWindowRect()
    {
        // The With() regression guard: every field the writer does not name must survive the write.
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "Alpha" }, new VmTarget { Name = "Beta", NicName = "Ethernet 9" }],
            Rules           = [new NetworkRule { Name = "Office", Priority = 10, VirtualSwitch = "Bridged" }],
            Fallback        = new FallbackAction { VirtualSwitch = "Default Switch", TargetVms = ["Alpha"] },
            LogLevel        = LogLevel.Warning,
            SettingsWindowX = 100, SettingsWindowY = 200, SettingsWindowWidth = 900, SettingsWindowHeight = 700,
        });
        using var mgr = MakeManager(path);

        mgr.SetVmNicName("Alpha", "Ethernet 2");

        var cfg = ReadConfig(path);
        Assert.Equal("Ethernet 2", cfg.VirtualMachines[0].NicName);
        Assert.Equal("Ethernet 9", cfg.VirtualMachines[1].NicName);   // the other VM is untouched
        Assert.Equal("Office",         Assert.Single(cfg.Rules).Name);
        Assert.Equal("Default Switch", cfg.Fallback.VirtualSwitch);
        Assert.Equal(["Alpha"],        cfg.Fallback.TargetVms);
        Assert.Equal(LogLevel.Warning, cfg.LogLevel);
        Assert.Equal(100, cfg.SettingsWindowX);
        Assert.Equal(200, cfg.SettingsWindowY);
        Assert.Equal(900, cfg.SettingsWindowWidth);
        Assert.Equal(700, cfg.SettingsWindowHeight);
    }

    // ── Add/RemoveVmFromConfig: the With() regression guard (issues #34 / #47) ──
    //
    // These two writers are no longer tray-only: issue #47 gives Settings its own add/remove, so both now
    // run from two surfaces. They funnel through the same With(vms:) as SetVmNicName above, and the guard
    // there is what keeps every unnamed field alive — but a writer that is reachable from a new place
    // deserves its own proof rather than inheriting one by structural argument.

    [Fact]
    public void AddVmToConfig_PreservesRulesFallbackLogLevelAndWindowRect()
    {
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "Alpha", NicName = "Ethernet 9" }],
            Rules           = [new NetworkRule { Name = "Office", Priority = 10, VirtualSwitch = "Bridged" }],
            Fallback        = new FallbackAction { VirtualSwitch = "Default Switch", TargetVms = ["Alpha"] },
            LogLevel        = LogLevel.Warning,
            SettingsWindowX = 100, SettingsWindowY = 200, SettingsWindowWidth = 900, SettingsWindowHeight = 700,
        });
        using var mgr = MakeManager(path);

        mgr.AddVmToConfig("Beta", "Ethernet 3");

        var cfg = ReadConfig(path);
        Assert.Equal(["Alpha", "Beta"], cfg.VirtualMachines.Select(v => v.Name));
        Assert.Equal("Ethernet 9",     cfg.VirtualMachines[0].NicName);   // the existing VM is untouched
        Assert.Equal("Ethernet 3",     cfg.VirtualMachines[1].NicName);
        Assert.Equal("Office",         Assert.Single(cfg.Rules).Name);
        Assert.Equal("Default Switch", cfg.Fallback.VirtualSwitch);
        Assert.Equal(["Alpha"],        cfg.Fallback.TargetVms);
        Assert.Equal(LogLevel.Warning, cfg.LogLevel);
        Assert.Equal(100, cfg.SettingsWindowX);
        Assert.Equal(200, cfg.SettingsWindowY);
        Assert.Equal(900, cfg.SettingsWindowWidth);
        Assert.Equal(700, cfg.SettingsWindowHeight);
    }

    [Fact]
    public void RemoveVmFromConfig_PreservesRulesFallbackLogLevelAndWindowRect()
    {
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "Alpha" }, new VmTarget { Name = "Beta", NicName = "Ethernet 9" }],
            Rules           = [new NetworkRule { Name = "Office", Priority = 10, VirtualSwitch = "Bridged" }],
            Fallback        = new FallbackAction { VirtualSwitch = "Default Switch", TargetVms = ["Alpha"] },
            LogLevel        = LogLevel.Warning,
            SettingsWindowX = 100, SettingsWindowY = 200, SettingsWindowWidth = 900, SettingsWindowHeight = 700,
        });
        using var mgr = MakeManager(path);

        mgr.RemoveVmFromConfig("Alpha");

        var cfg = ReadConfig(path);
        Assert.Equal("Beta",           Assert.Single(cfg.VirtualMachines).Name);
        Assert.Equal("Ethernet 9",     cfg.VirtualMachines[0].NicName);   // the surviving VM keeps its fields
        Assert.Equal("Office",         Assert.Single(cfg.Rules).Name);
        Assert.Equal("Default Switch", cfg.Fallback.VirtualSwitch);
        // Un-managing a VM must NOT quietly rewrite the rules that still name it — removing a VM from this
        // app's care is not a claim about what the user's rules should say.
        Assert.Equal(["Alpha"],        cfg.Fallback.TargetVms);
        Assert.Equal(LogLevel.Warning, cfg.LogLevel);
        Assert.Equal(100, cfg.SettingsWindowX);
        Assert.Equal(200, cfg.SettingsWindowY);
        Assert.Equal(900, cfg.SettingsWindowWidth);
        Assert.Equal(700, cfg.SettingsWindowHeight);
    }

    [Fact]
    public void RemoveVmFromConfig_ThenAddAgain_RoundTrips()
    {
        // The tray's Manage VMs list is a toggle, so this is its most ordinary interaction: an accidental
        // un-manage followed immediately by a re-add must land back in a sane state.
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "Alpha", NicName = "Ethernet 9" }],
        });
        using var mgr = MakeManager(path);

        mgr.RemoveVmFromConfig("Alpha");
        Assert.Empty(mgr.Current.VirtualMachines);

        mgr.AddVmToConfig("Alpha", "Ethernet 9");

        var vm = Assert.Single(ReadConfig(path).VirtualMachines);
        Assert.Equal("Alpha",      vm.Name);
        Assert.Equal("Ethernet 9", vm.NicName);
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

    // ── Load outcome (issue #39) ──────────────────────────────────────────────

    /// <summary>A normal load reports what it read, so a caller can tell the user.</summary>
    [Fact]
    public void Load_Valid_ReportsRuleAndVmCounts()
    {
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "A", NicName = "Network Adapter" },
                               new VmTarget { Name = "B", NicName = "Network Adapter" }],
            Rules           = [new NetworkRule { Name = "R1", Priority = 1, VirtualSwitch = "Bridged" }],
        });
        using var mgr = MakeManager(path);

        var outcome = mgr.Load();

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.RuleCount);
        Assert.Equal(2, outcome.VmCount);
        Assert.Null(outcome.Error);
        Assert.Same(outcome, mgr.LastLoad);
    }

    /// <summary>
    /// A malformed hand-edit must be REPORTED (not just logged), and must leave the last good config
    /// live — the user's rules keep working while the file is broken.
    /// </summary>
    [Fact]
    public void Load_BrokenJson_ReportsFailureAndKeepsThePreviousConfig()
    {
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "Original", NicName = "Network Adapter" }],
        });
        using var mgr = MakeManager(path);
        Assert.True(mgr.LastLoad.Succeeded);

        File.WriteAllText(path, "{ \"virtualMachines\": [ { \"name\": \"Broken\" ");   // truncated JSON
        var outcome = mgr.Load();

        Assert.False(outcome.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
        Assert.False(mgr.LastLoad.Succeeded);
        // The previous config is untouched — not emptied, not half-parsed.
        Assert.Equal("Original", Assert.Single(mgr.Current.VirtualMachines).Name);
    }

    /// <summary>
    /// The regression guard for the whole issue, spanning the real load and the UI contract: a load
    /// that failed must not be renderable as a successful reload by ANY of the surfaces. If this ever
    /// fails, Settings → "Reload config from disk" can once again re-draw the stale config and let the
    /// user believe their broken edit took effect.
    /// </summary>
    [Fact]
    public void Load_BrokenJson_CanNeverBeReportedAsASuccessfulReload()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        File.WriteAllText(path, "this is not json at all");
        var outcome = mgr.Load();

        Assert.Null(ConfigLoadUi.SuccessMessage(outcome));           // no "Reloaded — n rules…" sentence
        Assert.False(ConfigLoadUi.ShouldRebuildFromConfig(outcome)); // Settings must not re-render
        Assert.NotNull(ConfigLoadUi.FailureMessage(outcome));        // it must say something instead
        Assert.Contains("still active", ConfigLoadUi.FailureMessage(outcome)!);
    }

    /// <summary>A missing file is a failure like any other — reported, never silently defaulted.</summary>
    [Fact]
    public void Load_MissingFile_ReportsFailure()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        File.Delete(path);

        Assert.False(mgr.Load().Succeeded);
    }

    // ── Concurrency: a mutator's snapshot must be built inside the save lock ──────────────

    /// <summary>
    /// An ILogger that runs a callback the first time it logs a message containing a given fragment.
    /// The hook that makes the lost-update below DETERMINISTIC rather than a timing lottery: ConfigManager
    /// logs its success line inside the save lock, after the file is written but BEFORE the read-back
    /// updates <c>_config</c> — which is precisely the window in which another thread's snapshot of
    /// <c>_config</c> is stale. Nothing production-only is exposed to arrange it; the logger is a normal
    /// constructor dependency.
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
    /// The lost update the #31 review found: a cosmetic write silently eating a functional one.
    ///
    /// <para>Every mutator builds its whole-config snapshot with <c>With(...)</c>, i.e. by READING the
    /// live config; the file is then written wholesale. Build that snapshot before taking the save lock
    /// and two mutators become a read-modify-write race in which the loser writes its stale copy of every
    /// field it did not touch back over the winner's work. Here: the user retypes a VM's NIC name and
    /// closes the Settings window, so <c>SetVmNicName</c> (thread pool, from LostFocus) and
    /// <c>SaveSettingsWindowRect</c> (UI thread, from OnClosed) overlap, and the window rect — which
    /// cannot fail and which nobody is watching — reverts the NIC edit on disk. No error, nothing said,
    /// and the VM silently stops being reconnected, which is the failure #41 exists to fix.</para>
    ///
    /// <para>The interleaving is forced, not raced: the rect save is launched from inside the NIC save's
    /// own lock (via the success-log hook), at the one moment when <c>_config</c> is still stale. With
    /// the snapshot built outside the lock the rect save reads that stale config and this test fails on
    /// the NIC assert; with it built inside, the rect save blocks until the NIC save has completed and
    /// reads what it wrote.</para>
    /// </summary>
    [Fact]
    public async Task SaveSettingsWindowRect_CannotClobberAConcurrentNicEdit()
    {
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "vm1", NicName = "Network Adapter" }],
        });
        _tempFiles.Add(path);

        ConfigManager? mgr = null;
        Task? rectSave = null;

        // Fires inside SetVmNicName's lock: file written, _config NOT yet reloaded.
        var logger = new HookLogger("NIC name for VM 'vm1'", () =>
        {
            rectSave = Task.Run(() => mgr!.SaveSettingsWindowRect(new WindowRect(10, 20, 900, 700)));
            // Long enough for the rect save to reach its snapshot/lock. If it snapshots here it snapshots
            // a config that still says "Network Adapter" — and then writes that back.
            Thread.Sleep(250);
        });

        using (mgr = new ConfigManager(path, logger))
        {
            mgr.SetVmNicName("vm1", "Ethernet 2");
            await rectSave!.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var onDisk = ReadConfig(path);
        Assert.Equal("Ethernet 2", Assert.Single(onDisk.VirtualMachines).NicName);   // the edit survived
        Assert.Equal(900, onDisk.SettingsWindowWidth);                               // and so did the rect
    }

    // ── Issue #39: the broken-config balloon must re-arm after ANY successful load ────────

    /// <summary>
    /// Waits for <paramref name="condition"/>, polling — the FileSystemWatcher + 500 ms debounce is real
    /// here, so these two announcements are driven the way the app drives them.
    /// </summary>
    private static bool WaitFor(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    /// <summary>
    /// "Say it once" must not become "say it once, ever".
    ///
    /// <para>The say-it-once latch was set on a failed debounced load and re-armed ONLY there. But
    /// <c>SaveAndReload</c> pauses the watcher, writes a valid file and calls <c>Load()</c> directly:
    /// that load succeeds and the paused watcher never ticks, so the latch stayed set. Sequence:
    /// the user breaks config.json by hand (balloon — correct); the user then changes anything at all
    /// in the app, even just moving the Settings window and closing it; the user breaks the file again —
    /// and now NOTHING is said. The app runs on rules the user never wrote and reports nothing, which is
    /// exactly the issue #39 defect the latch exists to fix.</para>
    ///
    /// <para>Fixing it in <c>Load</c> — the one place every successful load flows through — is what makes
    /// this hold for the debounce tick, the Reload button and every in-app save alike.</para>
    /// </summary>
    [Fact]
    public void ABrokenConfigIsAnnouncedAgainAfterAnInAppSaveSucceeded()
    {
        var path = WriteTempConfig(new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "vm1", NicName = "Network Adapter" }],
        });
        _tempFiles.Add(path);

        using var mgr = new ConfigManager(path, NullLogger<ConfigManager>.Instance);
        int announced = 0;
        mgr.ConfigLoadFailed += (_, _) => Interlocked.Increment(ref announced);

        // 1. The user breaks the file by hand. The watcher notices; this is announced.
        File.WriteAllText(path, "{ this is not json");
        Assert.True(WaitFor(() => Volatile.Read(ref announced) == 1), "the first breakage was never announced");

        // 2. The user does something ordinary in the app. This writes a VALID file from the last good
        //    config and reloads it directly — with the watcher paused, so no tick follows.
        mgr.SaveSettingsWindowRect(new WindowRect(10, 20, 900, 700));
        Assert.True(mgr.LastLoad.Succeeded, "the in-app save should have left a good config loaded");

        // 3. The user breaks it again. This is NEWS: it must be announced again.
        File.WriteAllText(path, "{ broken all over again — and then some");
        Assert.True(WaitFor(() => Volatile.Read(ref announced) == 2),
            "the second breakage was never announced — the say-it-once latch is stuck set, so the app is "
            + "running on a config the user did not write and telling nobody");
    }
}
