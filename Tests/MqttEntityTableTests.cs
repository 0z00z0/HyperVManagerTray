using System.Globalization;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Xunit;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.Discovery;

namespace HyperVManagerTray.Tests;

/// <summary>
/// This app's published surface (issue #75): which entities exist, what they read, and what an inbound
/// command reaches. The table takes every side effect as a delegate, so all of it composes here with no
/// WMI, no broker and no WinUI — which is the point of building it that way.
/// </summary>
public class MqttEntityTableTests
{
    // ── The spec, with every side effect recorded rather than performed ──────────

    private sealed class Spy
    {
        public readonly MqttStateCache State = new();
        public List<string> Switches = [];
        public readonly Dictionary<string, string> Ips = new(StringComparer.OrdinalIgnoreCase);
        public int ReChecks;
        public int Repairs;
        public readonly List<(string Vm, VmOpKind Kind)> Power = [];
        public readonly List<(string Vm, string Switch)> Overrides = [];

        /// <summary>Names no power shape, deliberately: a spec that says nothing gets the type's own
        /// default, which is what an installation that never chose one publishes. Tests wanting the
        /// other shape say so with <c>with { PowerButtons = true }</c>.</summary>
        public MqttEntitySpec Spec(params string[] vmNames) => new()
        {
            VmNames             = vmNames,
            RuleSwitches        = () => Switches,
            State               = State,
            VmIp                = name => Ips.GetValueOrDefault(name),
            ReCheckNetwork      = _ => { ReChecks++; return Task.CompletedTask; },
            RepairHostNetworking = _ => { Repairs++; return Task.CompletedTask; },
            Power               = (vm, kind, _) => { Power.Add((vm, kind)); return Task.CompletedTask; },
            OverrideSwitch      = (vm, sw, _) => { Overrides.Add((vm, sw)); return Task.CompletedTask; },
        };
    }

    /// <summary>The group state as a publish pass sees it. Built over a store rather than hand-made:
    /// <c>PublishGroupSnapshot</c> is only constructible through <see cref="PublishGroupSet"/>, which is
    /// also the only thing that applies a group's declared default.</summary>
    private sealed class FakeStore(MqttSettings settings) : IMqttSettingsStore
    {
        public MqttSettings Read() => settings.Copy();
        public void Update(Action<MqttSettings> mutate) { mutate(settings); Changed?.Invoke(); }
        public event Action? Changed;
    }

    private static PublishGroupSnapshot Snapshot(params (string Key, bool On)[] groups)
    {
        var settings = new MqttSettings();
        foreach (var (key, on) in groups) settings.Groups[key] = on;
        return new PublishGroupSet(new FakeStore(settings), MqttEntityTable.Groups).Snapshot();
    }

    private static void Press(MqttEntity entity) =>
        ((MqttCommandEntity)entity).Accept(MqttButton.DefaultPress).Run!(CancellationToken.None).Wait();

    private static MqttCommandVerdict Send(MqttEntity entity, string payload) =>
        ((MqttCommandEntity)entity).Accept(payload);

    private static MqttEntity Get(MqttEntitySet set, string entityId)
    {
        var entity = set.Find(entityId);
        Assert.NotNull(entity);
        return entity;
    }

    /// <summary>A set built in the button shape.</summary>
    private static MqttEntitySet Buttons(Spy spy, params string[] vmNames) =>
        MqttEntityTable.Build(spy.Spec(vmNames) with { PowerButtons = true });

    /// <summary>A set built in whichever shape is named.</summary>
    private static MqttEntitySet Shaped(bool powerButtons, params string[] vmNames) =>
        MqttEntityTable.Build(new Spy().Spec(vmNames) with { PowerButtons = powerButtons });

    /// <summary>One VM's power-button ids, in the order the gate declares the verbs.</summary>
    private static IReadOnlyList<string> PowerButtonIds(string slug) =>
        [.. MqttCommandGate.PowerVerbs.Select(kind => $"vm_{slug}{MqttEntityTable.PowerButtonSuffix(kind)}")];

    /// <summary>Every suffix the per-VM ids of one shape carry, for a VM whose slug is known.</summary>
    private static IReadOnlyList<string> EmittedSuffixes(bool powerButtons)
    {
        var set = Shaped(powerButtons, "Dev");
        return
        [
            .. set.All
                .Where(e => e.EntityId.StartsWith(MqttEntityTable.VmIdPrefix, StringComparison.Ordinal))
                .Select(e => e.EntityId[(MqttEntityTable.VmIdPrefix.Length + "dev".Length)..]),
        ];
    }

    // ── The entity set ──────────────────────────────────────────────────────────

    /// <summary>The host-network entities, exactly. Two of them file under Diagnostics rather than
    /// Host network, which is why this asserts the whole list rather than a count.</summary>
    [Fact]
    public void Build_ProducesTheHostNetworkEntities()
    {
        var set = MqttEntityTable.Build(new Spy().Spec());

        Assert.Equal(
            ["network_rule", "network_switch", "network_adapter", "network_host_ip", "network_gateway",
             "network_apply_status", "network_bridge_healthy", "network_recheck", "network_repair"],
            set.All.Select(e => e.EntityId));
    }

    /// <summary>Twelve per VM, and the id of each is the state topic AND the command topic — so this is
    /// the list a receiver's registry records. A change here re-registers every entity of every VM.</summary>
    [Fact]
    public void Build_ProducesTwelveEntitiesPerVm()
    {
        var set = MqttEntityTable.Build(new Spy().Spec("Dev"));

        Assert.Equal(
            ["vm_dev_state", "vm_dev_running", "vm_dev_switch", "vm_dev_ip", "vm_dev_uptime",
             "vm_dev_operation", "vm_dev_cpu", "vm_dev_memory", "vm_dev_vhd",
             "vm_dev", "vm_dev_power", "vm_dev_switch_override"],
            set.All.Where(e => e.EntityId.StartsWith("vm_", StringComparison.Ordinal))
                   .Select(e => e.EntityId));
    }

    /// <summary>The select is what a spec that says nothing about the shape gets, and what an existing
    /// installation therefore keeps. The buttons are opt-in.</summary>
    [Fact]
    public void ThePowerSelectIsTheDefaultShape()
    {
        var set = MqttEntityTable.Build(new Spy().Spec("Dev"));

        Assert.IsType<MqttSelect>(Get(set, "vm_dev_power"));
        Assert.All(PowerButtonIds("dev"), id => Assert.Null(set.Find(id)));
    }

    /// <summary>Sixteen per VM in the button shape: the same twelve minus the select, plus one button per
    /// verb. The ids are the command topics a receiver registers, so this list is the published surface
    /// the option switches to.</summary>
    [Fact]
    public void Build_ProducesSixteenEntitiesPerVm_WhenPowerButtonsAreOn()
    {
        var set = Buttons(new Spy(), "Dev");

        Assert.Equal(
            ["vm_dev_state", "vm_dev_running", "vm_dev_switch", "vm_dev_ip", "vm_dev_uptime",
             "vm_dev_operation", "vm_dev_cpu", "vm_dev_memory", "vm_dev_vhd",
             "vm_dev",
             "vm_dev_power_start", "vm_dev_power_shutdown", "vm_dev_power_pause",
             "vm_dev_power_save", "vm_dev_power_resume",
             "vm_dev_switch_override"],
            set.All.Where(e => e.EntityId.StartsWith("vm_", StringComparison.Ordinal))
                   .Select(e => e.EntityId));
    }

    /// <summary>A VM name is a runtime string, so the id is slugged. The slug is per VM rather than per
    /// entity, so every entity of one VM carries the same one.</summary>
    [Fact]
    public void Build_SlugsAVmNameIntoTheTopicSafeAlphabet()
    {
        var set = MqttEntityTable.Build(new Spy().Spec("Web Server (2)"));

        Assert.NotNull(set.Find("vm_web_server_2_state"));
        Assert.NotNull(set.Find("vm_web_server_2"));
        Assert.NotNull(set.Find("vm_web_server_2_switch_override"));
    }

    /// <summary>Two names that slug alike must not share a slug: the id is the command topic, so one
    /// VM's commands would run on the other.</summary>
    [Fact]
    public void Build_SeparatesTwoVmNamesThatSlugAlike()
    {
        var set = MqttEntityTable.Build(new Spy().Spec("Web Server (2)", "web-server-2"));

        Assert.NotNull(set.Find("vm_web_server_2_state"));
        Assert.NotNull(set.Find("vm_web_server_2_2_state"));
        Assert.Equal(set.All.Count, set.All.Select(e => e.EntityId).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A hand-edited <c>"name": null</c>, or one of nothing but punctuation, must not throw the
    /// whole table out — the app would then publish nothing at all because of one bad config line.</summary>
    [Fact]
    public void Build_SurvivesAVmNameWithNothingSluggableInIt()
    {
        var set = MqttEntityTable.Build(new Spy().Spec(null!, "..."));

        Assert.NotNull(set.Find("vm_entity_state"));
        Assert.NotNull(set.Find("vm_entity_2_state"));
    }

    /// <summary>The cap is on the COMPOSED id, so the slug budget has to leave room for the longest
    /// suffix any of a VM's entities carries. "Windows Server 2022 Domain Controller" is an ordinary
    /// Hyper-V name, and an over-length id throws the whole table out — at startup, outside the
    /// publisher's guard, which takes the app down with it.</summary>
    [Fact]
    public void Build_SurvivesAVmNameLongerThanTheEntityIdCapAllows()
    {
        var set = MqttEntityTable.Build(new Spy().Spec("Windows Server 2022 Domain Controller"));

        Assert.All(set.All, e => Assert.True(e.EntityId.Length <= MqttEntityId.MaxLength, e.EntityId));
        // Pinned, not merely bounded: an id that moves between versions is a new entity to a receiver,
        // and the longest of them sits exactly on the cap.
        Assert.NotNull(set.Find("vm_windows_server_2022_domain_co"));
        Assert.NotNull(set.Find("vm_windows_server_2022_domain_co_switch_override"));
    }

    /// <summary>
    /// The budget is cut from both shapes at once, so the same VM name reaches the same slug under
    /// either. If it followed the active shape instead, a name that fits today would throw the whole
    /// table out the moment the setting is flipped — the same startup crash, triggered by a setting
    /// rather than by a rename — and every one of that VM's entities would move to a new id.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASlugIsTheSameUnderEitherPowerShape(bool powerButtons)
    {
        var set = Shaped(powerButtons, "Windows Server 2022 Domain Controller");

        Assert.All(set.All, e => Assert.True(e.EntityId.Length <= MqttEntityId.MaxLength, e.EntityId));
        Assert.NotNull(set.Find("vm_windows_server_2022_domain_co"));
        Assert.NotNull(set.Find("vm_windows_server_2022_domain_co_switch_override"));
    }

    /// <summary>The longest power-button id, pinned. It is shorter than the switch override's, so it
    /// does not set the budget — but nothing else fixes it in place, and an id that moves between
    /// versions is a new entity to a receiver.</summary>
    [Fact]
    public void ThePowerButtonIdsFitALongVmName()
    {
        var set = Buttons(new Spy(), "Windows Server 2022 Domain Controller");

        Assert.NotNull(set.Find("vm_windows_server_2022_domain_co_power_shutdown"));
        Assert.All(
            PowerButtonIds("windows_server_2022_domain_co"),
            id => Assert.NotNull(set.Find(id)));
    }

    /// <summary>Truncation happens before the collision check, not after it: two names that differ only
    /// past the slug budget still have to reach distinct ids.</summary>
    [Fact]
    public void Build_SeparatesTwoLongVmNamesThatTruncateAlike()
    {
        var set = MqttEntityTable.Build(new Spy().Spec(
            "Windows Server 2022 Domain Controller", "Windows Server 2022 Domain Controller (spare)"));

        Assert.All(set.All, e => Assert.True(e.EntityId.Length <= MqttEntityId.MaxLength, e.EntityId));
        Assert.Equal(set.All.Count, set.All.Select(e => e.EntityId).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Uniqueness has to hold over the ids actually emitted, not over the slugs they are
    /// composed from: "X switch" slugs to <c>x_switch</c>, whose own switch control lands on the id "X"'s
    /// diagnostics sensor already claims. Asserted both ways round, because whichever VM is allocated
    /// first is the one that keeps the plain id.</summary>
    [Theory]
    [InlineData("Dev", "Dev switch")]
    [InlineData("Dev switch", "Dev")]
    public void Build_SeparatesAVmWhoseIdWouldLandOnAnothersEntity(string first, string second)
    {
        var set = MqttEntityTable.Build(new Spy().Spec(first, second));

        Assert.Equal(set.All.Count, set.All.Select(e => e.EntityId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, set.All.Count(e => e is MqttSwitch));   // one power switch per VM, both present
    }

    /// <summary>The slug budget and the collision check are both composed from the declared suffix list,
    /// so an entity carrying a suffix missing from it would be sized and de-duplicated against an id
    /// nothing publishes. Asserted for EACH shape, because only one shape's power suffixes are emitted
    /// at a time.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryPerVmIdIsTheSlugPlusADeclaredSuffix(bool powerButtons)
        => Assert.All(
            EmittedSuffixes(powerButtons),
            suffix => Assert.Contains(suffix, MqttEntityTable.VmIdSuffixes));

    /// <summary>
    /// The declared list is the UNION of both power shapes, not whichever is in force. The budget is cut
    /// from the longest suffix in it, so a list following the active shape would be recomputed the moment
    /// the setting is flipped — and a VM name that fitted under one shape would throw the whole table out
    /// under the other, at startup, outside the publisher's guard.
    ///
    /// <para>Equality both ways: nothing emitted is missing from the list, and nothing in the list goes
    /// unemitted by both shapes — a suffix declared for nothing would shrink every slug for an id that
    /// is never published.</para>
    /// </summary>
    [Fact]
    public void TheDeclaredSuffixesAreExactlyTheUnionOfBothPowerShapes()
        => Assert.Equal(
            MqttEntityTable.VmIdSuffixes.Order(StringComparer.Ordinal),
            EmittedSuffixes(false).Concat(EmittedSuffixes(true))
                                  .Distinct(StringComparer.Ordinal)
                                  .Order(StringComparer.Ordinal));

    /// <summary>Each shape's own power suffixes are declared — spelled out, so a rename of either shape's
    /// id stem is caught here rather than only where the union happens to still balance.</summary>
    [Fact]
    public void BothPowerShapesDeclareTheirSuffixes()
    {
        Assert.Contains("_power", MqttEntityTable.VmIdSuffixes);
        Assert.All(
            ["_power_start", "_power_shutdown", "_power_pause", "_power_save", "_power_resume"],
            suffix => Assert.Contains(suffix, MqttEntityTable.VmIdSuffixes));
    }

    /// <summary>Ids are allocated in config order, so the same VM list always produces the same ids —
    /// an entity whose id moved between runs looks like a different entity to a receiver.</summary>
    [Fact]
    public void Build_AllocatesIdsInConfigOrder()
    {
        var forwards = MqttEntityTable.Build(new Spy().Spec("Web Server (2)", "web-server-2"));
        var reversed = MqttEntityTable.Build(new Spy().Spec("web-server-2", "Web Server (2)"));

        Assert.Equal("Web Server (2) state", Get(forwards, "vm_web_server_2_state").Name);
        Assert.Equal("web-server-2 state",   Get(reversed, "vm_web_server_2_state").Name);
    }

    // ── The VM list changing at runtime ─────────────────────────────────────────

    /// <summary>The table is rebuilt whenever the managed VM list changes. The set is immutable, so the
    /// rebuild REPLACES it — a pass reading the old one cannot see half of a change.</summary>
    [Fact]
    public void Build_ReflectsAVmAddedAtRuntime_WithoutDisturbingTheSetAlreadyInUse()
    {
        var spy = new Spy();
        var before = MqttEntityTable.Build(spy.Spec("Dev"));

        var after = MqttEntityTable.Build(spy.Spec("Dev", "Build"));

        Assert.NotNull(after.Find("vm_dev_state"));
        Assert.NotNull(after.Find("vm_build_state"));
        Assert.Null(before.Find("vm_build_state"));   // the set in flight is untouched
    }

    [Fact]
    public void Build_DropsAVmRemovedAtRuntime()
    {
        var spy = new Spy();
        spy.State.SetVms([new VmStatus { Name = "Build", State = "Running" }]);
        MqttEntityTable.Build(spy.Spec("Dev", "Build"));

        var after = MqttEntityTable.Build(spy.Spec("Dev"));

        Assert.Null(after.Find("vm_build_state"));
        Assert.NotNull(after.Find("vm_dev_state"));
    }

    [Fact]
    public void Build_WithNoManagedVms_PublishesTheHostNetworkAlone()
    {
        var set = MqttEntityTable.Build(new Spy().Spec());

        Assert.DoesNotContain(set.All, e => e.EntityId.StartsWith("vm_", StringComparison.Ordinal));
    }

    /// <summary>An entity reads through the cache on every pass, so a VM's status arriving after the
    /// table was built reaches the entity that was already announced.</summary>
    [Fact]
    public void AnEntityReadsTheCacheLive_NotTheValueItHeldAtBuildTime()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        var state = Get(set, "vm_dev_state");

        Assert.Equal(MqttPayload.None, state.ReadState());

        spy.State.SetVms([new VmStatus { Name = "Dev", State = "Running" }]);

        Assert.Equal("Running", state.ReadState());
    }

    // ── What the entities read ──────────────────────────────────────────────────

    /// <summary>An absent reading publishes the receiver's own "no value" literal. An EMPTY payload is
    /// ignored on every platform here, so the stale value would go on standing.</summary>
    [Fact]
    public void AnAbsentReadingPublishesTheNoValueLiteralRatherThanEmptyingTheTopic()
    {
        var set = MqttEntityTable.Build(new Spy().Spec("Dev"));

        Assert.All(
            set.All.Where(e => e.HasState),
            e => Assert.Equal(MqttPayload.None, e.ReadState()));
    }

    /// <summary>A reading that is present but blank is the same as no reading: an empty payload would be
    /// ignored and leave the previous value standing, which is exactly the stale state the sentinel
    /// exists to prevent. A VM with no switch attached, and a host read that produced no adapter, are
    /// both ordinary.</summary>
    [Fact]
    public void ABlankReadingPublishesTheNoValueLiteralToo()
    {
        var spy = new Spy();
        spy.Ips["Dev"] = "   ";
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        spy.State.SetVms([new VmStatus { Name = "Dev", State = "Off", Switch = "" }]);
        spy.State.SetNetwork(new MatchResult("Office", "Bridged", []) { HostAdapterName = "" });

        Assert.Equal(MqttPayload.None, Get(set, "vm_dev_switch").ReadState());
        Assert.Equal(MqttPayload.None, Get(set, "vm_dev_ip").ReadState());
        Assert.Equal(MqttPayload.None, Get(set, "network_adapter").ReadState());
        // An Off VM has no uptime, so the formatter returns the empty string rather than null.
        Assert.Equal(MqttPayload.None, Get(set, "vm_dev_uptime").ReadState());
    }

    [Fact]
    public void TheNetworkEntitiesReadTheLastAppliedOutcome()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec());
        spy.State.SetNetwork(new MatchResult("Office", "Bridged", ["Dev"])
        {
            HostAdapterName = "Dock LAN",
            HostIp          = "10.0.0.5",
            Gateway         = "10.0.0.1",
            ApplyStatus     = NetworkStatusUi.SwitchApplyStatus.Applied,
        });

        Assert.Equal("Office",   Get(set, "network_rule").ReadState());
        Assert.Equal("Bridged",  Get(set, "network_switch").ReadState());
        Assert.Equal("Dock LAN", Get(set, "network_adapter").ReadState());
        Assert.Equal("10.0.0.5", Get(set, "network_host_ip").ReadState());
        Assert.Equal("10.0.0.1", Get(set, "network_gateway").ReadState());
        Assert.Equal("Applied",  Get(set, "network_apply_status").ReadState());
    }

    /// <summary>"Bridge healthy" is a connectivity binary sensor, whose ON means "fine" — while the
    /// status it reads answers the opposite question. Reading it straight through publishes a confident
    /// green over a failed bind, which is the #37 defect in a second surface.</summary>
    [Theory]
    [InlineData(NetworkStatusUi.SwitchApplyStatus.Applied,         MqttPayload.On)]
    [InlineData(NetworkStatusUi.SwitchApplyStatus.NotEvaluated,    MqttPayload.On)]
    [InlineData(NetworkStatusUi.SwitchApplyStatus.BindFailed,      MqttPayload.Off)]
    [InlineData(NetworkStatusUi.SwitchApplyStatus.VmConnectFailed, MqttPayload.Off)]
    public void BridgeHealthy_IsTheInverseOfAFailedApply(
        NetworkStatusUi.SwitchApplyStatus status, string expected)
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec());
        spy.State.SetNetwork(new MatchResult("Office", "Bridged", []) { ApplyStatus = status });

        Assert.Equal(expected, Get(set, "network_bridge_healthy").ReadState());
    }

    [Fact]
    public void TheVmEntitiesReadTheCachedStatusAndTheCachedGuestIp()
    {
        var spy = new Spy();
        spy.Ips["Dev"] = "10.0.0.42";
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        spy.State.SetVms([new VmStatus
        {
            Name = "Dev", State = "Running", Switch = "Bridged", Uptime = "03:14:00",
        }]);
        spy.State.SetOperation(new VmOperationProgress("Dev", VmOpKind.Start, VmOpPhase.Succeeded, null, null));

        Assert.Equal("Running",         Get(set, "vm_dev_state").ReadState());
        Assert.Equal(MqttPayload.On,    Get(set, "vm_dev_running").ReadState());
        Assert.Equal("Bridged",         Get(set, "vm_dev_switch").ReadState());
        Assert.Equal("10.0.0.42",       Get(set, "vm_dev_ip").ReadState());
        Assert.Equal("3h 14m",          Get(set, "vm_dev_uptime").ReadState());
        Assert.Equal("Start Succeeded", Get(set, "vm_dev_operation").ReadState());
        Assert.Equal(MqttPayload.On,    Get(set, "vm_dev").ReadState());
    }

    /// <summary>A verb is an event, not a state. Announcing the last one requested would read as the VM
    /// being IN it — a select showing "Shutdown" over a VM that is running.</summary>
    [Fact]
    public void ThePowerSelectReportsNoCurrentVerb()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        spy.State.SetVms([new VmStatus { Name = "Dev", State = "Running" }]);

        Send(set.Find("vm_dev_power")!, "Shutdown").Run!(CancellationToken.None).Wait();

        Assert.Equal(MqttPayload.None, Get(set, "vm_dev_power").ReadState());
    }

    /// <summary>These payloads are protocol values, not display text. Under a comma-decimal locale an
    /// unpinned format writes "1177,4", which a receiver in another locale reads as a thousands
    /// separator — or not at all.</summary>
    [Fact]
    public void TheMetricSensorsPublishMachineReadableNumbers()
    {
        var culture   = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture   = new CultureInfo("nb-NO");
            CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
            Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);

            var spy = new Spy();
            var set = MqttEntityTable.Build(spy.Spec("Dev"));
            spy.State.SetVms([new VmStatus
            {
                Name = "Dev", State = "Running", Cpu = 17,
                MemAssigned = 1_234_567_890, VhdBytes = 1_234_567_890,
            }]);

            Assert.Equal("17",     Get(set, "vm_dev_cpu").ReadState());
            Assert.Equal("1177.4", Get(set, "vm_dev_memory").ReadState());
            Assert.Equal("1.15",   Get(set, "vm_dev_vhd").ReadState());
        }
        finally
        {
            CultureInfo.CurrentCulture   = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    // ── What an inbound command reaches ─────────────────────────────────────────

    [Fact]
    public void TheNetworkButtonsRunTheWorkTheyWereHandedIn()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec());

        Press(Get(set, "network_recheck"));
        Press(Get(set, "network_repair"));

        Assert.Equal(1, spy.ReChecks);
        Assert.Equal(1, spy.Repairs);
    }

    /// <summary>The switch routes through the same gate as the dashboard's own buttons, so it reaches a
    /// verb only when the VM's state allows it.</summary>
    [Theory]
    [InlineData("Off",     MqttPayload.On,  VmOpKind.Start)]
    [InlineData("Paused",  MqttPayload.On,  VmOpKind.Resume)]
    [InlineData("Running", MqttPayload.Off, VmOpKind.Shutdown)]
    public void TheVmSwitchRequestsTheVerbTheStateAllows(string state, string payload, VmOpKind expected)
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        spy.State.SetVms([new VmStatus { Name = "Dev", State = state }]);

        var verdict = Send(Get(set, "vm_dev"), payload);
        Assert.True(verdict.IsAccepted);
        verdict.Run!(CancellationToken.None).Wait();

        Assert.Equal(("Dev", expected), Assert.Single(spy.Power));
    }

    [Fact]
    public void TheVmSwitchRefusesAVerbTheStateDoesNotAllow()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        spy.State.SetVms([new VmStatus { Name = "Dev", State = "Off" }]);

        var verdict = Send(Get(set, "vm_dev"), MqttPayload.Off);

        Assert.Equal(MqttCommandOutcome.Refused, verdict.Outcome);
        Assert.Equal("'Shutdown' is not available while the VM is Off.", verdict.Detail);
        Assert.Empty(spy.Power);
    }

    [Fact]
    public void ThePowerSelectRequestsTheNamedVerbForTheNamedVm()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev", "Build"));
        spy.State.SetVms([new VmStatus { Name = "Build", State = "Running" }]);

        var verdict = Send(Get(set, "vm_build_power"), "Pause");
        Assert.True(verdict.IsAccepted);
        verdict.Run!(CancellationToken.None).Wait();

        Assert.Equal(("Build", VmOpKind.Pause), Assert.Single(spy.Power));
    }

    [Fact]
    public void ThePowerSelectRefusesAVerbTheStateDoesNotAllow()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        spy.State.SetVms([new VmStatus { Name = "Dev", State = "Running" }]);

        var verdict = Send(Get(set, "vm_dev_power"), "Start");

        Assert.Equal(MqttCommandOutcome.Refused, verdict.Outcome);
        Assert.Equal("'Start' is not available while the VM is Running.", verdict.Detail);
        Assert.Empty(spy.Power);
    }

    /// <summary>A payload the select never offered is not a power verb at all. The wording is the app's,
    /// so the operator is told which value was rejected.</summary>
    [Fact]
    public void ThePowerSelectRejectsAPayloadThatNamesNoVerb()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        var entity = (MqttSelect)Get(set, "vm_dev_power");

        // Straight at Apply: the component's own Accept screens anything not in Options() first.
        var verdict = entity.Apply("Reboot");

        Assert.Equal(MqttCommandOutcome.NotAnOption, verdict.Outcome);
        Assert.Equal("'Reboot' is not a power verb.", verdict.Detail);
        Assert.Empty(spy.Power);
    }

    [Fact]
    public void ThePowerSelectOffersTheGatesOwnVerbs()
        => Assert.Equal(
            MqttCommandGate.PowerOptions,
            ((MqttSelect)Get(MqttEntityTable.Build(new Spy().Spec("Dev")), "vm_dev_power")).Options());

    // ── The power buttons (the opt-in shape) ────────────────────────────────────

    /// <summary>One button per verb the gate declares, and nothing else under that stem: a verb without a
    /// button cannot be requested at all, and a button without a verb presses nothing.</summary>
    [Fact]
    public void ThereIsOnePowerButtonPerVerbTheGateDeclares()
    {
        var set = Buttons(new Spy(), "Dev");

        Assert.Equal(
            MqttCommandGate.PowerVerbs.Count,
            set.All.Count(e => e.EntityId.StartsWith("vm_dev_power_", StringComparison.Ordinal)));
        Assert.All(PowerButtonIds("dev"), id => Assert.IsType<MqttButton>(Get(set, id)));
    }

    /// <summary>A button carries the verb as the app words it — "shut down", not "Shutdown" (issue #42).
    /// The name is what an operator reads; the enum name stays in the refusal, which names the verb the
    /// gate declined rather than a control.</summary>
    [Theory]
    [InlineData("vm_dev_power_start",    "Dev start")]
    [InlineData("vm_dev_power_shutdown", "Dev shut down")]
    [InlineData("vm_dev_power_pause",    "Dev pause")]
    [InlineData("vm_dev_power_save",     "Dev save")]
    [InlineData("vm_dev_power_resume",   "Dev resume")]
    public void APowerButtonIsNamedForItsVerbInTheAppsOwnWords(string entityId, string expected)
        => Assert.Equal(expected, Get(Buttons(new Spy(), "Dev"), entityId).Name);

    /// <summary>A button has nothing to report between presses and declares no state channel, so it
    /// publishes nothing — which is the whole difference from the select, and the reason the option
    /// exists. The VM's actual power state is carried by vm_dev_state and vm_dev_running regardless.</summary>
    [Fact]
    public void ThePowerButtonsDeclareNoStateAtAll()
    {
        var spy = new Spy();
        var set = Buttons(spy, "Dev");
        spy.State.SetVms([new VmStatus { Name = "Dev", State = "Running" }]);

        Assert.All(PowerButtonIds("dev"), id =>
        {
            var button = Get(set, id);
            Assert.False(button.HasState);
            Assert.Null(button.ReadState());
        });
    }

    /// <summary>Each button carries exactly its own verb, for its own VM — the id is the command topic,
    /// so a button wired to the wrong verb or the wrong VM would act on the wrong thing silently.</summary>
    [Theory]
    [InlineData("Running", VmOpKind.Pause)]
    [InlineData("Running", VmOpKind.Save)]
    [InlineData("Running", VmOpKind.Shutdown)]
    [InlineData("Off",     VmOpKind.Start)]
    [InlineData("Paused",  VmOpKind.Resume)]
    public void APowerButtonRequestsItsOwnVerbForItsOwnVm(string state, VmOpKind kind)
    {
        var spy = new Spy();
        var set = Buttons(spy, "Dev", "Build");
        spy.State.SetVms([new VmStatus { Name = "Build", State = state }]);

        string id = $"vm_build{MqttEntityTable.PowerButtonSuffix(kind)}";
        var verdict = Send(Get(set, id), MqttButton.DefaultPress);
        Assert.True(verdict.IsAccepted);
        verdict.Run!(CancellationToken.None).Wait();

        Assert.Equal(("Build", kind), Assert.Single(spy.Power));
    }

    /// <summary>The gate is the select's, unchanged, so the refusal is word for word what the select
    /// gives — and refused still means not attempted.</summary>
    [Fact]
    public void APowerButtonRefusesAVerbTheStateDoesNotAllow()
    {
        var spy = new Spy();
        var set = Buttons(spy, "Dev");
        spy.State.SetVms([new VmStatus { Name = "Dev", State = "Running" }]);

        var verdict = Send(Get(set, "vm_dev_power_start"), MqttButton.DefaultPress);

        Assert.Equal(MqttCommandOutcome.Refused, verdict.Outcome);
        Assert.Equal("'Start' is not available while the VM is Running.", verdict.Detail);
        Assert.Empty(spy.Power);
    }

    /// <summary>Every verb against every state, so the whole gate is shown to reach the buttons. Each row
    /// is in the order <see cref="MqttCommandGate.PowerVerbs"/> declares, which is the order the buttons
    /// are built in. Hard-coded rather than read back out of <see cref="VmStateUi.AllowedVerbs"/>, which
    /// would pass against any table at all.</summary>
    [Theory]
    [InlineData("Running",  new[] { VmOpKind.Shutdown, VmOpKind.Pause, VmOpKind.Save })]
    [InlineData("Paused",   new[] { VmOpKind.Save, VmOpKind.Resume })]
    [InlineData("Saved",    new[] { VmOpKind.Start })]
    [InlineData("Off",      new[] { VmOpKind.Start })]
    [InlineData("Starting", new VmOpKind[0])]
    [InlineData("Unknown",  new VmOpKind[0])]
    public void ThePowerButtonsAcceptExactlyTheVerbsTheStateAllows(string state, VmOpKind[] allowed)
    {
        var spy = new Spy();
        var set = Buttons(spy, "Dev");
        spy.State.SetVms([new VmStatus { Name = "Dev", State = state }]);

        var accepted = MqttCommandGate.PowerVerbs
            .Where(kind => Send(
                Get(set, $"vm_dev{MqttEntityTable.PowerButtonSuffix(kind)}"),
                MqttButton.DefaultPress).IsAccepted)
            .ToList();

        Assert.Equal(allowed, accepted);
    }

    // ── Switching between the two shapes ────────────────────────────────────────

    /// <summary>
    /// The shape being switched away from leaves the set ENTIRELY — not withheld. The distinction is what
    /// the publisher acts on: an entity the table no longer contains is announced as removed and its
    /// retained state topic emptied, whereas a withheld one keeps its whole entry and reads as
    /// permanently unavailable. Withholding the old shape would leave a dead control on the device page
    /// for ever.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingShape_DropsTheOtherShapeRatherThanWithholdingIt(bool powerButtons)
    {
        var set = Shaped(powerButtons, "Dev");

        var gone = powerButtons ? (string[])["vm_dev_power"] : [.. PowerButtonIds("dev")];

        Assert.All(gone, id => Assert.Null(set.Find(id)));
        // Not merely unpublished: withheld entities are still in the set, and these must not be.
        Assert.All(gone, id => Assert.DoesNotContain(id, set.Withheld(null).Select(e => e.EntityId)));
        Assert.All(gone, id => Assert.DoesNotContain(id, set.All.Select(e => e.EntityId)));
    }

    /// <summary>Everything except the power controls survives the switch untouched, so flipping the
    /// option costs a receiver nothing beyond the controls it replaces.</summary>
    [Fact]
    public void SwitchingShape_LeavesEveryOtherEntityWhereItWas()
    {
        var selects = MqttEntityTable.Build(new Spy().Spec("Dev"));
        var buttons = Buttons(new Spy(), "Dev");

        static IEnumerable<string> NonPower(MqttEntitySet set) =>
            set.All.Select(e => e.EntityId)
                   .Where(id => !id.StartsWith("vm_dev_power", StringComparison.Ordinal));

        Assert.Equal(NonPower(selects), NonPower(buttons));
    }

    /// <summary>The signature is what decides whether the document is rebuilt and re-announced. A shape
    /// change has to move it — otherwise the flip is saved, nothing is re-announced, and the old shape
    /// stands on the broker until something unrelated changes the VM list.</summary>
    [Fact]
    public void Signature_MovesWhenThePowerShapeChanges()
        => Assert.NotEqual(
            MqttEntityTable.Signature(["Dev", "Build"], powerButtons: false),
            MqttEntityTable.Signature(["Dev", "Build"], powerButtons: true));

    [Fact]
    public void Signature_MovesWhenTheVmListChanges()
        => Assert.NotEqual(
            MqttEntityTable.Signature(["Dev"], powerButtons: false),
            MqttEntityTable.Signature(["Dev", "Build"], powerButtons: false));

    /// <summary>…and stands still otherwise, including for a hand-edited <c>"name": null</c>: a config
    /// write that left the table alone must not re-announce the whole document.</summary>
    [Fact]
    public void Signature_StandsStillWhenNothingTheTableReadsMoved()
    {
        Assert.Equal(
            MqttEntityTable.Signature(["Dev", "Build"], powerButtons: true),
            MqttEntityTable.Signature(["Dev", "Build"], powerButtons: true));
        Assert.Equal(
            MqttEntityTable.Signature([null, "Build"], powerButtons: false),
            MqttEntityTable.Signature(["", "Build"], powerButtons: false));
    }

    // ── The switch override ─────────────────────────────────────────────────────

    /// <summary>A receiver rejects a select with no options, so with no rules configured the entity is
    /// WITHHELD rather than dropped — it keeps its entry and its registry record.</summary>
    [Fact]
    public void TheSwitchOverrideIsWithheldWhileNoRuleNamesASwitch()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        var entity = Get(set, "vm_dev_switch_override");

        Assert.False(entity.IsPublished(null));
        Assert.Contains(entity, set.All);                       // still in the set…
        Assert.Contains(entity, set.Withheld(null));            // …and reported as withheld, not gone
    }

    /// <summary>The options are read on every announcement pass, so a rule edit reaches the receiver
    /// without the table being rebuilt.</summary>
    [Fact]
    public void TheSwitchOverrideReturnsTheMomentARuleNamesASwitch()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        var entity = (MqttSelect)Get(set, "vm_dev_switch_override");

        spy.Switches = ["Bridged", "Default Switch"];

        Assert.True(entity.IsPublished(null));
        Assert.Equal(["Bridged", "Default Switch"], entity.Options());
    }

    [Fact]
    public void TheSwitchOverrideBindsTheNamedVmToTheNamedSwitch()
    {
        var spy = new Spy();
        spy.Switches = ["Bridged"];
        var set = MqttEntityTable.Build(spy.Spec("Dev"));

        var verdict = Send(Get(set, "vm_dev_switch_override"), "Bridged");
        Assert.True(verdict.IsAccepted);
        verdict.Run!(CancellationToken.None).Wait();

        Assert.Equal(("Dev", "Bridged"), Assert.Single(spy.Overrides));
    }

    /// <summary>A receiver holding a stale option list must not be able to bind a switch no rule names.</summary>
    [Fact]
    public void TheSwitchOverrideRefusesASwitchNoRuleNames()
    {
        var spy = new Spy();
        spy.Switches = ["Bridged"];
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        var entity = (MqttSelect)Get(set, "vm_dev_switch_override");

        var verdict = entity.Apply("Guest Only");

        Assert.Equal(MqttCommandOutcome.NotAnOption, verdict.Outcome);
        Assert.Equal("'Guest Only' is not one of the configured rule switches.", verdict.Detail);
        Assert.Empty(spy.Overrides);
    }

    // ── Group membership ────────────────────────────────────────────────────────

    /// <summary>Which group each entity carries — a toggle is what a user switches off, so an entity in
    /// the wrong group is one they cannot turn off, or one that vanishes when they turn off something
    /// else. The metrics three matter most: they are the only ones whose group costs a WMI loop.</summary>
    [Theory]
    [InlineData("network_rule",           MqttEntityTable.NetworkGroup)]
    [InlineData("network_switch",         MqttEntityTable.NetworkGroup)]
    [InlineData("network_adapter",        MqttEntityTable.NetworkGroup)]
    [InlineData("network_apply_status",   MqttEntityTable.NetworkGroup)]
    [InlineData("network_bridge_healthy", MqttEntityTable.NetworkGroup)]
    [InlineData("network_recheck",        MqttEntityTable.NetworkGroup)]
    [InlineData("network_repair",         MqttEntityTable.NetworkGroup)]
    [InlineData("network_host_ip",        MqttEntityTable.DiagnosticsGroup)]
    [InlineData("network_gateway",        MqttEntityTable.DiagnosticsGroup)]
    [InlineData("vm_dev_state",           MqttEntityTable.VmGroup)]
    [InlineData("vm_dev_running",         MqttEntityTable.VmGroup)]
    [InlineData("vm_dev",                 MqttEntityTable.VmGroup)]
    [InlineData("vm_dev_power",           MqttEntityTable.VmGroup)]
    [InlineData("vm_dev_switch_override", MqttEntityTable.VmGroup)]
    [InlineData("vm_dev_switch",          MqttEntityTable.DiagnosticsGroup)]
    [InlineData("vm_dev_ip",              MqttEntityTable.DiagnosticsGroup)]
    [InlineData("vm_dev_uptime",          MqttEntityTable.DiagnosticsGroup)]
    [InlineData("vm_dev_operation",       MqttEntityTable.DiagnosticsGroup)]
    [InlineData("vm_dev_cpu",             MqttEntityTable.MetricsGroup)]
    [InlineData("vm_dev_memory",          MqttEntityTable.MetricsGroup)]
    [InlineData("vm_dev_vhd",             MqttEntityTable.MetricsGroup)]
    public void EveryEntityCarriesItsDeclaredGroup(string entityId, string group)
        => Assert.Equal(group, Get(MqttEntityTable.Build(new Spy().Spec("Dev")), entityId).Group);

    /// <summary>The buttons file under the same group the select did, so switching shape does not move a
    /// power control out from under the toggle that switches it off.</summary>
    [Fact]
    public void EveryPowerButtonCarriesTheVmGroup()
    {
        var set = Buttons(new Spy(), "Dev");

        Assert.All(PowerButtonIds("dev"),
                   id => Assert.Equal(MqttEntityTable.VmGroup, Get(set, id).Group));
    }

    /// <summary>Every entity belongs to a group the app DECLARED. An unknown key reads as "always on" at
    /// the receiver, so the settings panel would offer no way to switch it off.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryEntityBelongsToADeclaredGroup(bool powerButtons)
    {
        var declared = MqttEntityTable.Groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var set = Shaped(powerButtons, "Dev");

        Assert.All(set.All, e => Assert.Contains(e.Group!, declared));
    }

    /// <summary>Switching a group off stops its entities publishing; it does not withdraw them. A
    /// withheld entity keeps its whole entry in the document and reads as unavailable, so a toggle never
    /// costs a receiver's registry record.</summary>
    [Fact]
    public void SwitchingTheMetricsGroupOffWithholdsItsEntitiesWithoutRemovingThem()
    {
        var set = MqttEntityTable.Build(new Spy().Spec("Dev"));
        var off = Snapshot((MqttEntityTable.MetricsGroup, false));

        var withheld = set.Withheld(off).Select(e => e.EntityId).ToList();

        Assert.Contains("vm_dev_cpu", withheld);
        Assert.Contains("vm_dev_memory", withheld);
        Assert.Contains("vm_dev_vhd", withheld);
        Assert.DoesNotContain("vm_dev_state", withheld);
        Assert.NotNull(set.Find("vm_dev_cpu"));   // still declared, just not announced
    }

    // ── The topic root, and what an earlier build left behind ───────────────────

    /// <summary>The topic root is the stem of the default device id and of every topic. Changing it
    /// orphans every retained topic on the broker.</summary>
    [Fact]
    public void TopicRoot_IsTheApplicationsOwn()
        => Assert.Equal("hypervmanagertray", MqttEntityTable.TopicRoot);

    /// <summary>
    /// All three empty, and that is the declaration rather than an omission: no released build of this
    /// app has ever published to a broker, so there is no installed base to carry across. The publisher
    /// empties exactly what is named, ONCE, and writes the fact down permanently — a guessed key would
    /// delete a topic belonging to something else and could not be taken back.
    /// </summary>
    [Fact]
    public void NothingIsDeclaredAsMigratingOrRetired()
    {
        Assert.Empty(MqttEntityTable.Migrating);
        Assert.Empty(MqttEntityTable.Retired);
        Assert.Empty(MqttEntityTable.RetiredChannels);
    }
}
