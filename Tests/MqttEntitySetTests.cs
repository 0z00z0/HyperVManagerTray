using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Xunit;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.HomeAssistant;

namespace HyperVManagerTray.Tests;

/// <summary>
/// HyperVManagerTray's Home Assistant entity set (issue #75): what it declares, what each entity
/// publishes from the app's own events, and what an inbound command is allowed to do.
///
/// <para>Every side effect is a delegate, so the whole set composes here with no broker, no WMI and
/// no WinUI — which is what makes the gate on the command path assertable rather than trusted.</para>
/// </summary>
public class MqttEntitySetTests
{
    /// <summary>A spec wired to recorders, so a test can drive the app's events in and read the
    /// commands out.</summary>
    private sealed class Harness
    {
        public MqttStateCache State { get; } = new();
        public Dictionary<string, string> Ips { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool PublishMetrics { get; set; }
        public List<(string Vm, VmOpKind Kind)> PowerCalls { get; } = [];
        public List<(string Vm, string Switch)> Overrides { get; } = [];
        public List<string> Refusals { get; } = [];
        public int ReChecks { get; private set; }
        public int Repairs { get; private set; }

        public MqttEntitySpec Spec(IReadOnlyList<string> vmNames, IReadOnlyList<string> ruleSwitches) => new()
        {
            VmNames        = vmNames,
            RuleSwitches   = ruleSwitches,
            State          = State,
            VmIp           = name => Ips.TryGetValue(name, out var ip) ? ip : null,
            PublishMetrics = () => PublishMetrics,
            ReCheckNetwork = _ => { ReChecks++; return Task.CompletedTask; },
            RepairHostNetworking = _ => { Repairs++; return Task.CompletedTask; },
            Power          = (vm, kind, _) => { PowerCalls.Add((vm, kind)); return Task.CompletedTask; },
            OverrideSwitch = (vm, sw, _) => { Overrides.Add((vm, sw)); return Task.CompletedTask; },
            Refuse         = reason => Refusals.Add(reason),
        };

        public HaEntitySet Build(IReadOnlyList<string>? vmNames = null,
                                IReadOnlyList<string>? ruleSwitches = null) =>
            MqttEntitySet.Build(Spec(vmNames ?? ["DevBox"], ruleSwitches ?? ["Bridged", "Isolated"]));
    }

    private static HaEntity Entity(HaEntitySet set, string objectId) =>
        set.All.Single(e => e.ObjectId == objectId);

    /// <summary>Accepts a payload and runs whatever the verdict allows, exactly as the connection's
    /// command worker does.</summary>
    private static async Task<HaCommandVerdict> SendAsync(HaEntitySet set, string objectId, string payload)
    {
        var entity = (HaCommandEntity)Entity(set, objectId);
        var verdict = entity.Accept(payload);
        if (verdict.Run is { } run) await run(CancellationToken.None);
        return verdict;
    }

    private static MatchResult Applied(NetworkStatusUi.SwitchApplyStatus status =
                                           NetworkStatusUi.SwitchApplyStatus.Applied) =>
        new("Office", "Bridged", ["DevBox"])
        {
            HostAdapterName          = "Dock LAN",
            HostAdapterInterfaceName = "Ethernet 3",
            HostIp                   = "10.0.0.42",
            Gateway                  = "10.0.0.1",
            ApplyStatus              = status,
        };

    private static VmStatus Status(string name, string state, string? vmSwitch = null) => new()
    {
        Name        = name,
        State       = state,
        Switch      = vmSwitch ?? "Bridged",
        Cpu         = 7,
        MemAssigned = 4L * 1024 * 1024 * 1024,
        MemMax      = 8L * 1024 * 1024 * 1024,
        VhdBytes    = 64L * 1024 * 1024 * 1024,
        Uptime      = "03:14:00",
    };

    // ── What the set declares ──────────────────────────────────────────────────

    /// <summary>The host-network half of issue #75's table, entity for entity.</summary>
    [Theory]
    [InlineData("network_rule",           "sensor",        HaEntityRole.Primary)]
    [InlineData("network_switch",         "sensor",        HaEntityRole.Primary)]
    [InlineData("network_adapter",        "sensor",        HaEntityRole.Primary)]
    [InlineData("network_host_ip",        "sensor",        HaEntityRole.Diagnostic)]
    [InlineData("network_gateway",        "sensor",        HaEntityRole.Diagnostic)]
    [InlineData("network_apply_status",   "sensor",        HaEntityRole.Primary)]
    [InlineData("network_bridge_healthy", "binary_sensor", HaEntityRole.Primary)]
    [InlineData("network_recheck",        "button",        HaEntityRole.Primary)]
    [InlineData("network_repair",         "button",        HaEntityRole.Primary)]
    public void TheNetworkEntitiesAreDeclared(string objectId, string component, HaEntityRole role)
    {
        var entity = Entity(new Harness().Build(), objectId);

        Assert.Equal(component, entity.Component);
        Assert.Equal(role, entity.Role);
    }

    /// <summary>The per-VM half, for one managed VM.</summary>
    [Theory]
    [InlineData("vm_devbox_state",           "sensor",        HaEntityRole.Primary)]
    [InlineData("vm_devbox_running",         "binary_sensor", HaEntityRole.Primary)]
    [InlineData("vm_devbox_switch",          "sensor",        HaEntityRole.Diagnostic)]
    [InlineData("vm_devbox_ip",              "sensor",        HaEntityRole.Diagnostic)]
    [InlineData("vm_devbox_uptime",          "sensor",        HaEntityRole.Diagnostic)]
    [InlineData("vm_devbox_operation",       "sensor",        HaEntityRole.Diagnostic)]
    [InlineData("vm_devbox",                 "switch",        HaEntityRole.Primary)]
    [InlineData("vm_devbox_power",           "select",        HaEntityRole.Primary)]
    [InlineData("vm_devbox_switch_override", "select",        HaEntityRole.Config)]
    public void ThePerVmEntitiesAreDeclared(string objectId, string component, HaEntityRole role)
    {
        var entity = Entity(new Harness().Build(), objectId);

        Assert.Equal(component, entity.Component);
        Assert.Equal(role, entity.Role);
    }

    [Fact]
    public void EachVmGetsItsOwnEntities()
    {
        var set = new Harness().Build(["DevBox", "BuildBox"]);

        Assert.Contains(set.All, e => e.ObjectId == "vm_devbox_state");
        Assert.Contains(set.All, e => e.ObjectId == "vm_buildbox_state");
    }

    [Fact]
    public void NoVmsMeansOnlyTheNetworkEntities()
    {
        var set = new Harness().Build([]);

        Assert.All(set.All, e => Assert.StartsWith("network_", e.ObjectId, StringComparison.Ordinal));
    }

    // ── The network entities publish from SwitchApplied ─────────────────────────

    /// <summary>Before the first apply pass there is nothing to say, and a sensor that says nothing
    /// reads as unknown rather than as a fabricated value.</summary>
    [Fact]
    public void NothingIsPublishedBeforeTheFirstApplyPass()
    {
        var set = new Harness().Build();

        foreach (var entity in set.All.Where(e => e.ObjectId.StartsWith("network_", StringComparison.Ordinal)))
            Assert.Null(entity.Payload());
    }

    [Fact]
    public void SwitchAppliedReachesEveryNetworkSensor()
    {
        var harness = new Harness();
        var set = harness.Build();

        harness.State.SetNetwork(Applied());

        Assert.Equal("Office",    Entity(set, "network_rule").Payload());
        Assert.Equal("Bridged",   Entity(set, "network_switch").Payload());
        Assert.Equal("Dock LAN",  Entity(set, "network_adapter").Payload());
        Assert.Equal("10.0.0.42", Entity(set, "network_host_ip").Payload());
        Assert.Equal("10.0.0.1",  Entity(set, "network_gateway").Payload());
        Assert.Equal("Applied",   Entity(set, "network_apply_status").Payload());
    }

    /// <summary>The bridge-healthy sensor is <c>IsFailure</c> inverted, for every status — not for the
    /// two that happen to be checked by hand.</summary>
    [Theory]
    [InlineData(NetworkStatusUi.SwitchApplyStatus.Applied)]
    [InlineData(NetworkStatusUi.SwitchApplyStatus.BindFailed)]
    [InlineData(NetworkStatusUi.SwitchApplyStatus.VmConnectFailed)]
    [InlineData(NetworkStatusUi.SwitchApplyStatus.NotEvaluated)]
    [InlineData(NetworkStatusUi.SwitchApplyStatus.Starting)]
    public void BridgeHealthyIsTheInverseOfIsFailure(NetworkStatusUi.SwitchApplyStatus status)
    {
        var harness = new Harness();
        var set = harness.Build();
        harness.State.SetNetwork(Applied(status));

        string expected = NetworkStatusUi.IsFailure(status) ? "OFF" : "ON";

        Assert.Equal(expected, Entity(set, "network_bridge_healthy").Payload());
    }

    // ── The per-VM entities publish from StatusesChanged / OperationProgress ────

    [Fact]
    public void StatusesChangedReachesEveryPerVmSensor()
    {
        var harness = new Harness();
        harness.Ips["DevBox"] = "10.0.0.77";
        var set = harness.Build();

        harness.State.SetVms([Status("DevBox", "Running")]);

        Assert.Equal("Running",   Entity(set, "vm_devbox_state").Payload());
        Assert.Equal("ON",        Entity(set, "vm_devbox_running").Payload());
        Assert.Equal("Bridged",   Entity(set, "vm_devbox_switch").Payload());
        Assert.Equal("10.0.0.77", Entity(set, "vm_devbox_ip").Payload());
        Assert.Equal("3h 14m",    Entity(set, "vm_devbox_uptime").Payload());
        Assert.Equal("ON",        Entity(set, "vm_devbox").Payload());
    }

    [Fact]
    public void AStoppedVmPublishesOffAndNoUptime()
    {
        var harness = new Harness();
        var set = harness.Build();

        harness.State.SetVms([Status("DevBox", "Off")]);

        Assert.Equal("Off", Entity(set, "vm_devbox_state").Payload());
        Assert.Equal("OFF", Entity(set, "vm_devbox_running").Payload());
        Assert.Null(Entity(set, "vm_devbox_uptime").Payload());
    }

    [Fact]
    public void AVmWithNoKnownIpPublishesNothingRatherThanABlank()
    {
        var harness = new Harness();
        var set = harness.Build();
        harness.State.SetVms([Status("DevBox", "Running")]);

        Assert.Null(Entity(set, "vm_devbox_ip").Payload());
    }

    [Fact]
    public void OperationProgressReachesTheLastOperationSensor()
    {
        var harness = new Harness();
        var set = harness.Build();

        harness.State.SetOperation(new VmOperationProgress(
            "DevBox", VmOpKind.Save, VmOpPhase.Failed, null, "not enough memory"));

        Assert.Equal("Save — Failed: not enough memory", Entity(set, "vm_devbox_operation").Payload());
    }

    [Fact]
    public void AnOperationWithNoMessageStillNamesItsVerbAndPhase()
    {
        var harness = new Harness();
        var set = harness.Build();

        harness.State.SetOperation(new VmOperationProgress(
            "DevBox", VmOpKind.Start, VmOpPhase.Requested, null, null));

        Assert.Equal("Start — Requested", Entity(set, "vm_devbox_operation").Payload());
    }

    [Fact]
    public void OneVmsOperationNeverAppearsOnAnother()
    {
        var harness = new Harness();
        var set = harness.Build(["DevBox", "BuildBox"]);

        harness.State.SetOperation(new VmOperationProgress(
            "DevBox", VmOpKind.Pause, VmOpPhase.Succeeded, null, null));

        Assert.NotNull(Entity(set, "vm_devbox_operation").Payload());
        Assert.Null(Entity(set, "vm_buildbox_operation").Payload());
    }

    // ── The metrics toggle ─────────────────────────────────────────────────────

    /// <summary>CPU, memory and VHD are declared but withheld while the toggle is off, so the retained
    /// discovery is emptied rather than left behind as unavailable.</summary>
    [Fact]
    public void MetricEntitiesAreWithheldWhileTheToggleIsOff()
    {
        var harness = new Harness { PublishMetrics = false };
        var set = harness.Build();

        var withheld = set.Withheld.Select(e => e.ObjectId).ToList();

        Assert.Equal(["vm_devbox_cpu", "vm_devbox_memory", "vm_devbox_vhd"], withheld);
        Assert.DoesNotContain(set.Announced, e => e.ObjectId == "vm_devbox_cpu");
    }

    [Fact]
    public void MetricEntitiesAreAnnouncedWhenTheToggleIsOn()
    {
        var harness = new Harness { PublishMetrics = true };
        var set = harness.Build();

        Assert.Empty(set.Withheld);
        Assert.Contains(set.Announced, e => e.ObjectId == "vm_devbox_cpu");
    }

    /// <summary>The toggle is read per pass, so flipping it takes effect on a reload without the set
    /// being rebuilt.</summary>
    [Fact]
    public void TheToggleIsReadPerPassRatherThanCapturedAtBuild()
    {
        var harness = new Harness { PublishMetrics = false };
        var set = harness.Build();

        harness.PublishMetrics = true;

        Assert.Empty(set.Withheld);
    }

    [Fact]
    public void MetricsArePublishedAsMachineReadableNumbers()
    {
        var harness = new Harness { PublishMetrics = true };
        var set = harness.Build();
        harness.State.SetVms([Status("DevBox", "Running")]);

        Assert.Equal("7",    Entity(set, "vm_devbox_cpu").Payload());
        Assert.Equal("4096", Entity(set, "vm_devbox_memory").Payload());
        Assert.Equal("64",   Entity(set, "vm_devbox_vhd").Payload());
    }

    // ── Commands ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheNetworkButtonsRunTheirCommands()
    {
        var harness = new Harness();
        var set = harness.Build();

        await SendAsync(set, "network_recheck", "PRESS");
        await SendAsync(set, "network_repair", "PRESS");

        Assert.Equal(1, harness.ReChecks);
        Assert.Equal(1, harness.Repairs);
    }

    [Fact]
    public async Task APowerVerbTheStateAllowsReachesTheVm()
    {
        var harness = new Harness();
        var set = harness.Build();
        harness.State.SetVms([Status("DevBox", "Running")]);

        await SendAsync(set, "vm_devbox_power", "Shutdown");

        Assert.Equal([("DevBox", VmOpKind.Shutdown)], harness.PowerCalls);
        Assert.Empty(harness.Refusals);
    }

    /// <summary>The gate on the command path, which is the point: the option is announced, Home
    /// Assistant may still send it, and the current state decides whether anything runs.</summary>
    [Fact]
    public async Task APowerVerbTheStateForbidsIsRefusedRatherThanAttempted()
    {
        var harness = new Harness();
        var set = harness.Build();
        harness.State.SetVms([Status("DevBox", "Off")]);

        await SendAsync(set, "vm_devbox_power", "Shutdown");

        Assert.Empty(harness.PowerCalls);
        Assert.Single(harness.Refusals);
        Assert.Contains("DevBox", harness.Refusals[0], StringComparison.Ordinal);
    }

    /// <summary>A VM the app has no status for yet allows no verb at all — an unknown state must not
    /// be optimistic.</summary>
    [Fact]
    public async Task NoVerbRunsForAVmWhoseStateIsNotKnownYet()
    {
        var harness = new Harness();
        var set = harness.Build();

        foreach (string option in MqttCommandGate.PowerOptions)
            await SendAsync(set, "vm_devbox_power", option);

        Assert.Empty(harness.PowerCalls);
        Assert.Equal(MqttCommandGate.PowerOptions.Count, harness.Refusals.Count);
    }

    [Fact]
    public async Task AnUnannouncedPowerOptionIsRefusedByTheEntityItself()
    {
        var harness = new Harness();
        var set = harness.Build();
        harness.State.SetVms([Status("DevBox", "Running")]);

        var verdict = await SendAsync(set, "vm_devbox_power", "Reboot");

        Assert.Equal(HaCommandOutcome.NotAnOption, verdict.Outcome);
        Assert.Empty(harness.PowerCalls);
    }

    [Fact]
    public async Task TheRunningSwitchStartsAStoppedVmAndShutsDownARunningOne()
    {
        var harness = new Harness();
        var set = harness.Build();

        harness.State.SetVms([Status("DevBox", "Off")]);
        await SendAsync(set, "vm_devbox", "ON");

        harness.State.SetVms([Status("DevBox", "Running")]);
        await SendAsync(set, "vm_devbox", "OFF");

        Assert.Equal([("DevBox", VmOpKind.Start), ("DevBox", VmOpKind.Shutdown)], harness.PowerCalls);
    }

    [Fact]
    public async Task TheRunningSwitchResumesAPausedVm()
    {
        var harness = new Harness();
        var set = harness.Build();
        harness.State.SetVms([Status("DevBox", "Paused")]);

        await SendAsync(set, "vm_devbox", "ON");

        Assert.Equal([("DevBox", VmOpKind.Resume)], harness.PowerCalls);
    }

    [Fact]
    public async Task TheRunningSwitchIsRefusedMidTransition()
    {
        var harness = new Harness();
        var set = harness.Build();
        harness.State.SetVms([Status("DevBox", "Starting")]);

        await SendAsync(set, "vm_devbox", "ON");
        await SendAsync(set, "vm_devbox", "OFF");

        Assert.Empty(harness.PowerCalls);
        Assert.Equal(2, harness.Refusals.Count);
    }

    [Fact]
    public async Task TheSwitchOverrideAppliesAConfiguredSwitch()
    {
        var harness = new Harness();
        var set = harness.Build();

        await SendAsync(set, "vm_devbox_switch_override", "Isolated");

        Assert.Equal([("DevBox", "Isolated")], harness.Overrides);
    }

    [Fact]
    public async Task TheSwitchOverrideRefusesASwitchNoRuleNames()
    {
        var harness = new Harness();
        var set = harness.Build();

        var verdict = await SendAsync(set, "vm_devbox_switch_override", "Default Switch");

        Assert.Equal(HaCommandOutcome.NotAnOption, verdict.Outcome);
        Assert.Empty(harness.Overrides);
    }

    /// <summary>Home Assistant rejects a select with no options, so with nothing to pick from the
    /// override is not declared at all.</summary>
    [Fact]
    public void TheSwitchOverrideIsNotDeclaredWithoutRuleSwitches()
    {
        var set = new Harness().Build(["DevBox"], []);

        Assert.DoesNotContain(set.All, e => e.ObjectId == "vm_devbox_switch_override");
        Assert.Contains(set.All, e => e.ObjectId == "vm_devbox_power");
    }

    [Fact]
    public void TheOverrideAnnouncesExactlyTheConfiguredRuleSwitches()
    {
        var set = new Harness().Build(["DevBox"], ["Bridged", "Isolated"]);

        var select = (HaSelect)Entity(set, "vm_devbox_switch_override");

        Assert.Equal(["Bridged", "Isolated"], select.Options);
    }

    // ── Object ids ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DevBox",        "devbox")]
    [InlineData("Dev Box",       "dev_box")]
    [InlineData("Win11-Test VM", "win11_test_vm")]
    [InlineData("  spaced  ",    "spaced")]
    [InlineData("!!!",           "vm")]
    [InlineData("",              "vm")]
    public void ASlugIsTopicSafe(string name, string expected) =>
        Assert.Equal(expected, MqttObjectIds.Slug(name));

    /// <summary>The slug is the state and command topic, so two names that reduce to the same one
    /// would route one VM's commands to the other.</summary>
    [Fact]
    public void CollidingNamesGetDistinctSlugs()
    {
        var ids = MqttObjectIds.ForVms(["My VM", "My-VM", "My_VM"]);

        Assert.Equal(3, ids.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("my_vm", ids["My VM"]);
    }

    [Fact]
    public void ASlugIsCappedSoATopicStaysReadable()
    {
        string slug = MqttObjectIds.Slug(new string('a', 100));

        Assert.Equal(MqttObjectIds.MaxLength, slug.Length);
    }

    /// <summary>Two VMs whose names differ only in case are one VM everywhere else in this app.</summary>
    [Fact]
    public void NamesDifferingOnlyInCaseAreOneVm()
    {
        var ids = MqttObjectIds.ForVms(["DevBox", "devbox"]);

        Assert.Single(ids);
    }

    // ── Discovery ──────────────────────────────────────────────────────────────

    private static HaDiscoveryContext Context() => new()
    {
        Topics = new MqttTopics { Root = MqttEntitySet.TopicRoot, NodeId = "hypervmanagertray_lab" },
        Device = new HaDevice { Name = "Hyper-V host" },
    };

    [Fact]
    public void EveryEntityGetsItsOwnUniqueId()
    {
        var set = new Harness().Build(["DevBox", "BuildBox"]);
        var context = Context();

        var uniqueIds = set.All.Select(e => (string)e.Discovery(context)["unique_id"]!).ToList();

        Assert.Equal(uniqueIds.Count, uniqueIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryEntityGroupsUnderOneDevice()
    {
        var set = new Harness().Build(["DevBox", "BuildBox"]);
        var context = Context();

        Assert.All(set.All, e =>
        {
            var device = (IReadOnlyDictionary<string, object?>)e.Discovery(context)["device"]!;
            Assert.Equal("Hyper-V host", device["name"]);
        });
    }

    [Fact]
    public void ACommandEntityAnnouncesACommandTopic()
    {
        var set = new Harness().Build();
        var config = Entity(set, "vm_devbox_power").Discovery(Context());

        Assert.Equal("hypervmanagertray/hypervmanagertray_lab/cmd/vm_devbox_power", config["command_topic"]);
    }

    /// <summary>A button has nothing to publish, so it announces no state topic to sit unknown on.</summary>
    [Fact]
    public void AButtonAnnouncesNoStateTopic()
    {
        var set = new Harness().Build();
        var config = Entity(set, "network_recheck").Discovery(Context());

        Assert.False(config.ContainsKey("state_topic"));
    }

    [Fact]
    public void OneChannelIsPublishedPerAnnouncedStatefulEntity()
    {
        var harness = new Harness { PublishMetrics = false };
        var set = harness.Build();

        var suffixes = set.Channels().Select(c => c.TopicSuffix).ToList();

        Assert.DoesNotContain("network_recheck", suffixes);
        Assert.DoesNotContain("vm_devbox_cpu", suffixes);
        Assert.Contains("vm_devbox_state", suffixes);
    }
}
