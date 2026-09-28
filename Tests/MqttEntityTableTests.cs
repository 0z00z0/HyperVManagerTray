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
        /// <summary>The rule switches, by name; each one's switch ID is <see cref="SwitchIdOf"/> of its name.</summary>
        public List<string> Switches = [];
        /// <summary>The fallback's switch by name, or null when it names none.</summary>
        public string? Fallback;
        public readonly Dictionary<string, string> Ips = new(StringComparer.OrdinalIgnoreCase);
        public int ReChecks;
        public int Repairs;
        public readonly List<(string Vm, VmOpKind Kind)> Power = [];
        /// <summary>Each override as the VM ID and the switch ID it was bound by.</summary>
        public readonly List<(string Vm, string Switch)> Overrides = [];
        /// <summary>Each Hyper-V service's state; unset reads as not read yet.</summary>
        public readonly Dictionary<HyperVServiceKind, HyperVServiceState> Services = [];
        /// <summary>Each service command as the service and whether it was a start.</summary>
        public readonly List<(HyperVServiceKind Kind, bool Start)> ServiceCommands = [];

        /// <param name="vms">Each string is one VM's ID and its name at once, which keeps the ids these
        /// tests read short; the tests about the ID itself pass a <see cref="VmRef"/>.</param>
        public MqttEntitySpec Spec(params string[] vms) => Build([.. vms.Select(v => new VmRef(v, v))]);

        public MqttEntitySpec Spec(VmRef first, params VmRef[] more) => Build([first, .. more]);

        private MqttEntitySpec Build(IReadOnlyList<VmRef> vms) => new()
        {
            Vms                 = vms,
            RuleSwitches        = () => [.. Switches.Select(n => new SwitchRef(SwitchIdOf(n), n))],
            FallbackSwitch      = () => Fallback is { } name ? new SwitchRef(SwitchIdOf(name), name) : null,
            State               = State,
            VmIp                = id => Ips.GetValueOrDefault(id),
            ReCheckNetwork      = _ => { ReChecks++; return Task.CompletedTask; },
            RepairHostNetworking = _ => { Repairs++; return Task.CompletedTask; },
            Power               = (vm, kind, _) => { Power.Add((vm.Id, kind)); return Task.CompletedTask; },
            OverrideSwitch      = (vm, sw, _) => { Overrides.Add((vm, sw.Id)); return Task.CompletedTask; },
            ServiceState        = kind => Services.GetValueOrDefault(kind, HyperVServiceState.Unknown),
            ServiceCommand      = (kind, start, _) =>
            {
                ServiceCommands.Add((kind, start));
                return Task.CompletedTask;
            },
        };

        /// <summary>The stand-in switch ID of a rule switch the spy names.</summary>
        public static string SwitchIdOf(string name) => "SW-" + name.ToUpperInvariant().Replace(' ', '-');
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

    /// <summary>One VM's power-button ids, in the order the gate declares the verbs.</summary>
    private static IReadOnlyList<string> PowerButtonIds(string slug) =>
        [.. MqttCommandGate.PowerVerbs.Select(kind => $"vm_{slug}{MqttEntityTable.PowerButtonSuffix(kind)}")];

    /// <summary>Every suffix the per-VM ids carry, for a VM whose slug is known.</summary>
    private static IReadOnlyList<string> EmittedSuffixes()
    {
        var set = MqttEntityTable.Build(new Spy().Spec("Dev"));
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
            set.All.Select(e => e.EntityId).Where(id => id.StartsWith("network_", StringComparison.Ordinal)));
    }

    /// <summary>Sixteen per VM, and the id of each is the state topic AND the command topic — so this is
    /// the list a receiver's registry records. A change here re-registers every entity of every VM.</summary>
    [Fact]
    public void Build_ProducesSixteenEntitiesPerVm()
    {
        var set = MqttEntityTable.Build(new Spy().Spec("Dev"));

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

    /// <summary>Each service carries a state sensor and the buttons its verbs take. The Host Compute
    /// Service has no stop at all — it is stopped only from the dashboard — so the two services publish
    /// different sets, which is why this asserts the whole list.</summary>
    [Fact]
    public void Build_ProducesTheServiceEntities()
        => Assert.Equal(
            ["service_vmms_state", "service_vmms_start", "service_vmms_stop",
             "service_vmcompute_state", "service_vmcompute_start"],
            MqttEntityTable.Build(new Spy().Spec()).All
                .Select(e => e.EntityId)
                .Where(id => id.StartsWith("service_", StringComparison.Ordinal)));

    // Stand-in VM IDs, GUID-shaped as Hyper-V assigns them. A and B share their first 29 hex digits —
    // everything the slug budget keeps — and differ after it.
    private const string GuidA = "3F2A9C1B-0D4E-4F60-8718-293A4B5C6D7E";
    private const string GuidB = "3F2A9C1B-0D4E-4F60-8718-293A4B5C6FFF";
    private const string GuidC = "0123ABCD-4567-89EF-0123-456789ABCDEF";

    /// <summary>What GuidA's entity ids carry: its hex digits, lower case, cut to the slug budget.</summary>
    private const string SlugA = "3f2a9c1b0d4e4f608718293a4b5c6";

    private static VmRef Guest(string id, string name) => new(id, name);

    /// <summary>
    /// A VM's entity ids come from its VM ID, never from its name, and are pinned: an id that moves
    /// between versions is a new entity to a receiver, and one derived from a name moves on a rename and
    /// is shared by two VMs of one name. The longest of them sits exactly on the cap.
    /// </summary>
    [Fact]
    public void Build_KeysEveryVmEntityOnTheVmId()
    {
        var set = MqttEntityTable.Build(new Spy().Spec(Guest(GuidA, "Web Server (2)")));

        Assert.All(set.All, e => Assert.True(e.EntityId.Length <= MqttEntityId.MaxLength, e.EntityId));
        Assert.NotNull(set.Find($"vm_{SlugA}_state"));
        Assert.NotNull(set.Find($"vm_{SlugA}"));
        Assert.NotNull(set.Find($"vm_{SlugA}_switch_override"));
        Assert.Equal("Web Server (2) state", Get(set, $"vm_{SlugA}_state").Name);   // the name is only shown
    }

    /// <summary>Two VMs sharing a name are two sets of entities. Keyed by name they were one, and one
    /// VM's commands ran on the other.</summary>
    [Fact]
    public void Build_GivesTwoVmsSharingANameTheirOwnEntities()
    {
        var set = MqttEntityTable.Build(new Spy().Spec(Guest(GuidA, "Dev"), Guest(GuidC, "Dev")));

        Assert.Equal(set.All.Count, set.All.Select(e => e.EntityId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, set.All.Count(e => e is MqttSwitch));   // one power switch per VM, both present
    }

    /// <summary>A rename changes the entities' names and no id, so a receiver keeps every entity.</summary>
    [Fact]
    public void Build_ARenameMovesNoId()
    {
        var before = MqttEntityTable.Build(new Spy().Spec(Guest(GuidA, "Dev")));
        var after  = MqttEntityTable.Build(new Spy().Spec(Guest(GuidA, "Dev renamed")));

        Assert.Equal(before.All.Select(e => e.EntityId), after.All.Select(e => e.EntityId));
        Assert.Equal("Dev renamed state", Get(after, $"vm_{SlugA}_state").Name);
    }

    /// <summary>The longest power-button id, pinned. It is shorter than the switch override's, so it
    /// does not set the budget — but nothing else fixes it in place.</summary>
    [Fact]
    public void ThePowerButtonIdsFitAVmId()
    {
        var set = MqttEntityTable.Build(new Spy().Spec(Guest(GuidA, "Dev")));

        Assert.All(set.All, e => Assert.True(e.EntityId.Length <= MqttEntityId.MaxLength, e.EntityId));
        Assert.NotNull(set.Find($"vm_{SlugA}_power_shutdown"));
        Assert.All(PowerButtonIds(SlugA), id => Assert.NotNull(set.Find(id)));
    }

    /// <summary>Two VM IDs alike as far as the slug budget reaches still get distinct ids: the collision
    /// check runs on the ids actually emitted, after the cut.</summary>
    [Fact]
    public void Build_SeparatesTwoVmIdsThatTruncateAlike()
    {
        var set = MqttEntityTable.Build(new Spy().Spec(Guest(GuidA, "One"), Guest(GuidB, "Two")));

        Assert.All(set.All, e => Assert.True(e.EntityId.Length <= MqttEntityId.MaxLength, e.EntityId));
        Assert.Equal(set.All.Count, set.All.Select(e => e.EntityId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("One state", Get(set, $"vm_{SlugA}_state").Name);   // the first keeps the plain slug
    }

    /// <summary>A managed VM an older settings document left unidentified has no ID to key entities on,
    /// so it publishes nothing rather than something keyed on its name.</summary>
    [Fact]
    public void Build_LeavesOutAVmNotIdentifiedYet()
    {
        var set = MqttEntityTable.Build(new Spy().Spec(Guest("", "Old entry")));

        Assert.DoesNotContain(set.All, e => e.EntityId.StartsWith(MqttEntityTable.VmIdPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// The slug budget and the collision check are both composed from the declared suffix list, so an
    /// entity carrying a suffix missing from it would be sized and de-duplicated against an id nothing
    /// publishes, and a suffix declared for nothing would shrink every slug for an id never published.
    ///
    /// <para>Equality both ways, which is what makes it fail in either direction.</para>
    /// </summary>
    [Fact]
    public void TheDeclaredSuffixesAreExactlyWhatIsEmitted()
        => Assert.Equal(
            MqttEntityTable.VmIdSuffixes.Order(StringComparer.Ordinal),
            EmittedSuffixes().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    /// <summary>The power buttons' suffixes, spelled out. A rename of the stem they are composed from
    /// passes the equality above, because both of its sides are composed from that stem and move
    /// together — so these literals are what pins the published ids, which a receiver keys its registry
    /// entries on.</summary>
    [Fact]
    public void ThePowerButtonSuffixesAreDeclared()
        => Assert.All(
            ["_power_start", "_power_shutdown", "_power_pause", "_power_save", "_power_resume"],
            suffix => Assert.Contains(suffix, MqttEntityTable.VmIdSuffixes));

    /// <summary>A VM keeps its ids whatever its place in the list: they come from its own VM ID, so an
    /// entity whose id moved between runs — a different entity to a receiver — cannot come from a
    /// reordering.</summary>
    [Fact]
    public void Build_GivesAVmTheSameIdsWhateverItsPlaceInTheList()
    {
        var forwards = MqttEntityTable.Build(new Spy().Spec(Guest(GuidA, "Web"), Guest(GuidC, "Db")));
        var reversed = MqttEntityTable.Build(new Spy().Spec(Guest(GuidC, "Db"), Guest(GuidA, "Web")));

        Assert.Equal("Web state", Get(forwards, $"vm_{SlugA}_state").Name);
        Assert.Equal("Web state", Get(reversed, $"vm_{SlugA}_state").Name);
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
        spy.State.SetVms([new VmStatus { Id = "Build", Name = "Build", State = "Running" }]);
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

        spy.State.SetVms([new VmStatus { Id = "Dev", Name = "Dev", State = "Running" }]);

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
        spy.State.SetVms([new VmStatus { Id = "Dev", Name = "Dev", State = "Off", Switch = "" }]);
        spy.State.SetNetwork(new MatchResult("office", "Office", "SW-BRIDGED", "Bridged", []) { HostAdapterName = "" });

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
        spy.State.SetNetwork(new MatchResult("office", "Office", "SW-BRIDGED", "Bridged", [new VmRef("Dev", "Dev")])
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
        spy.State.SetNetwork(new MatchResult("office", "Office", "SW-BRIDGED", "Bridged", []) { ApplyStatus = status });

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
            Id = "Dev", Name = "Dev", State = "Running", Switch = "Bridged", Uptime = "03:14:00",
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
                Id = "Dev", Name = "Dev", State = "Running", Cpu = 17,
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
        spy.State.SetVms([new VmStatus { Id = "Dev", Name = "Dev", State = state }]);

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
        spy.State.SetVms([new VmStatus { Id = "Dev", Name = "Dev", State = "Off" }]);

        var verdict = Send(Get(set, "vm_dev"), MqttPayload.Off);

        Assert.Equal(MqttCommandOutcome.Refused, verdict.Outcome);
        Assert.Equal("'Shutdown' is not available while the VM is Off.", verdict.Detail);
        Assert.Empty(spy.Power);
    }

    // ── The power buttons ───────────────────────────────────────────────────────

    /// <summary>One button per verb the gate declares, and nothing else under that stem: a verb without a
    /// button cannot be requested at all, and a button without a verb presses nothing.</summary>
    [Fact]
    public void ThereIsOnePowerButtonPerVerbTheGateDeclares()
    {
        var set = MqttEntityTable.Build(new Spy().Spec("Dev"));

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
        => Assert.Equal(expected, Get(MqttEntityTable.Build(new Spy().Spec("Dev")), entityId).Name);

    /// <summary>A button has nothing to report between presses and declares no state channel, so it
    /// publishes nothing. The VM's actual power state is carried by vm_dev_state and vm_dev_running,
    /// which is where a person reads it.</summary>
    [Fact]
    public void ThePowerButtonsDeclareNoStateAtAll()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        spy.State.SetVms([new VmStatus { Id = "Dev", Name = "Dev", State = "Running" }]);

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
        var set = MqttEntityTable.Build(spy.Spec("Dev", "Build"));
        spy.State.SetVms([new VmStatus { Id = "Build", Name = "Build", State = state }]);

        string id = $"vm_build{MqttEntityTable.PowerButtonSuffix(kind)}";
        var verdict = Send(Get(set, id), MqttButton.DefaultPress);
        Assert.True(verdict.IsAccepted);
        verdict.Run!(CancellationToken.None).Wait();

        Assert.Equal(("Build", kind), Assert.Single(spy.Power));
    }

    /// <summary>The refusal is the backstop behind the availability: a button that reached the broker
    /// before the state moved, or a payload sent by hand, is refused rather than attempted.</summary>
    [Fact]
    public void APowerButtonRefusesAVerbTheStateDoesNotAllow()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        spy.State.SetVms([new VmStatus { Id = "Dev", Name = "Dev", State = "Running" }]);

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
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        spy.State.SetVms([new VmStatus { Id = "Dev", Name = "Dev", State = state }]);

        var accepted = MqttCommandGate.PowerVerbs
            .Where(kind => Send(
                Get(set, $"vm_dev{MqttEntityTable.PowerButtonSuffix(kind)}"),
                MqttButton.DefaultPress).IsAccepted)
            .ToList();

        Assert.Equal(allowed, accepted);
    }

    /// <summary>
    /// Which buttons are published in each state, which is what decides whether the receiver shows the
    /// control or greys it out. The same table as the row above, asserted against the availability rather
    /// than against the verdict: a refusal reaches the log alone, so availability is the only part of it a
    /// person sees.
    ///
    /// <para>Hard-coded rather than read back out of <see cref="VmStateUi.AllowedVerbs"/>, which would
    /// pass against any table at all.</para>
    /// </summary>
    [Theory]
    [InlineData("Running",  new[] { VmOpKind.Shutdown, VmOpKind.Pause, VmOpKind.Save })]
    [InlineData("Paused",   new[] { VmOpKind.Save, VmOpKind.Resume })]
    [InlineData("Saved",    new[] { VmOpKind.Start })]
    [InlineData("Off",      new[] { VmOpKind.Start })]
    [InlineData("Starting", new VmOpKind[0])]
    [InlineData("Unknown",  new VmOpKind[0])]
    public void ThePowerButtonsArePublishedForExactlyTheVerbsTheStateAllows(string state, VmOpKind[] allowed)
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        spy.State.SetVms([new VmStatus { Id = "Dev", Name = "Dev", State = state }]);

        var published = MqttCommandGate.PowerVerbs
            .Where(kind => Get(set, $"vm_dev{MqttEntityTable.PowerButtonSuffix(kind)}").IsPublished(null) == true)
            .ToList();

        Assert.Equal(allowed, published);
    }

    /// <summary>A verb that is not valid is WITHHELD, not dropped: it keeps its entry and its registry
    /// record, and returns the moment the state allows it. Dropping it would delete and recreate the
    /// entity on every state change.</summary>
    [Fact]
    public void AnUnavailablePowerButtonIsWithheldRatherThanDropped()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        spy.State.SetVms([new VmStatus { Id = "Dev", Name = "Dev", State = "Off" }]);

        var withheld = set.Withheld(null).Select(e => e.EntityId).ToList();

        Assert.Contains("vm_dev_power_pause", withheld);
        Assert.DoesNotContain("vm_dev_power_start", withheld);
        Assert.NotNull(set.Find("vm_dev_power_pause"));   // still declared, just not announced
    }

    /// <summary>While a Hyper-V service is down only a start is offered, and it starts the service first
    /// (issue #114) — so the availability follows that branch too rather than the plain state table, which
    /// would grey out the one verb that works.</summary>
    [Fact]
    public void WhileAServiceIsDownOnlyTheStartAndResumeButtonsArePublished()
    {
        var spy = new Spy();
        var set = MqttEntityTable.Build(spy.Spec("Dev") with
        {
            AnyServiceDown = () => true,
            VmmsDown       = () => true,
        });
        spy.State.SetVms([new VmStatus { Id = "Dev", Name = "Dev", State = "Running" }]);

        Assert.Equal(
            new[] { VmOpKind.Start, VmOpKind.Resume },
            MqttCommandGate.PowerVerbs
                .Where(kind => Get(set, $"vm_dev{MqttEntityTable.PowerButtonSuffix(kind)}").IsPublished(null) == true));
    }

    // ── The service buttons (issue #114) ────────────────────────────────────────

    /// <summary>A service's start is offered only while it is stopped and its stop only while it is
    /// running, so the button a person sees is the one that can be pressed. Not read yet offers neither.</summary>
    [Theory]
    [InlineData(HyperVServiceState.Unknown, false, false)]
    [InlineData(HyperVServiceState.Stopped, true,  false)]
    [InlineData(HyperVServiceState.Running, false, true)]
    [InlineData(HyperVServiceState.Starting, false, false)]
    public void AServiceButtonIsPublishedOnlyWhileItsVerbApplies(
        HyperVServiceState state, bool startShown, bool stopShown)
    {
        var spy = new Spy();
        spy.Services[HyperVServiceKind.VirtualMachineManagement] = state;
        var set = MqttEntityTable.Build(spy.Spec());

        Assert.Equal(startShown, Get(set, "service_vmms_start").IsPublished(null));
        Assert.Equal(stopShown,  Get(set, "service_vmms_stop").IsPublished(null));
    }

    /// <summary>The Host Compute Service has no stop button at all — it is stopped only from the
    /// dashboard — so its stop is absent rather than merely unavailable.</summary>
    [Fact]
    public void TheHostComputeServiceHasNoStopButton()
    {
        var spy = new Spy();
        spy.Services[HyperVServiceKind.HostCompute] = HyperVServiceState.Running;

        Assert.Null(MqttEntityTable.Build(spy.Spec()).Find("service_vmcompute_stop"));
    }

    /// <summary>A press reaches the service command it names. Refused rather than attempted when the
    /// state does not allow it, which is the backstop behind the availability above.</summary>
    [Fact]
    public void AServiceButtonRunsItsOwnVerbAndRefusesTheOther()
    {
        var spy = new Spy();
        spy.Services[HyperVServiceKind.VirtualMachineManagement] = HyperVServiceState.Running;
        var set = MqttEntityTable.Build(spy.Spec());

        Press(Get(set, "service_vmms_stop"));
        Assert.Equal((HyperVServiceKind.VirtualMachineManagement, false), Assert.Single(spy.ServiceCommands));

        var verdict = Send(Get(set, "service_vmms_start"), MqttButton.DefaultPress);
        Assert.Equal(MqttCommandOutcome.Refused, verdict.Outcome);
        Assert.Single(spy.ServiceCommands);
    }

    // ── Switching between the two shapes ────────────────────────────────────────

    // ── The table signature ─────────────────────────────────────────────────────

    /// <summary>The signature is what decides whether the document is rebuilt and re-announced, so a
    /// change to what the table names has to move it and a config write that left the table alone must
    /// not.</summary>
    private static readonly VmRef Dev   = new(GuidA, "Dev");
    private static readonly VmRef Build = new(GuidC, "Build");

    [Fact]
    public void Signature_MovesWhenTheVmListChanges()
        => Assert.NotEqual(
            MqttEntityTable.Signature([Dev]),
            MqttEntityTable.Signature([Dev, Build]));

    /// <summary>A rename moves no id but does move the entities' names, which only a re-announcement
    /// carries to the receiver.</summary>
    [Fact]
    public void Signature_MovesWhenAVmIsRenamed()
        => Assert.NotEqual(
            MqttEntityTable.Signature([Dev]),
            MqttEntityTable.Signature([Dev with { Name = "Dev renamed" }]));

    /// <summary>…and stands still otherwise, including for a hand-edited <c>"name": null</c>: a config
    /// write that left the table alone must not re-announce the whole document.</summary>
    [Fact]
    public void Signature_StandsStillWhenNothingTheTableReadsMoved()
    {
        Assert.Equal(
            MqttEntityTable.Signature([Dev, Build]),
            MqttEntityTable.Signature([new VmRef(GuidA, "Dev"), new VmRef(GuidC, "Build")]));
        Assert.Equal(
            MqttEntityTable.Signature([new VmRef(GuidA, null!), Build]),
            MqttEntityTable.Signature([new VmRef(GuidA, ""), Build]));
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

    /// <summary>The fallback switch is an option too. Without it the list holds only what the rules bind —
    /// which on a host with one rule is the switch the machine is already on, so there is nothing to
    /// override to. It comes after the rules' own switches.</summary>
    [Fact]
    public void TheSwitchOverrideOffersTheFallbackSwitchBesideTheRules()
    {
        var spy = new Spy();
        spy.Switches = ["Bridged"];
        spy.Fallback = "Default Switch";

        var entity = (MqttSelect)Get(MqttEntityTable.Build(spy.Spec("Dev")), "vm_dev_switch_override");

        Assert.Equal(["Bridged", "Default Switch"], entity.Options());
    }

    /// <summary>A fallback that names a switch a rule already binds appears once: the options are a
    /// select's values, so a repeated one would be two ways to ask for the same switch. By identifier, not
    /// by name — two switches may share a name.</summary>
    [Fact]
    public void TheSwitchOverrideOffersAFallbackThatIsAlsoARuleSwitchOnlyOnce()
    {
        var spy = new Spy();
        spy.Switches = ["Bridged"];
        spy.Fallback = "Bridged";

        var entity = (MqttSelect)Get(MqttEntityTable.Build(spy.Spec("Dev")), "vm_dev_switch_override");

        Assert.Equal(["Bridged"], entity.Options());
    }

    /// <summary>The fallback alone does not publish the override: the entity exists to move a machine off
    /// what a rule bound it to, so a host with no rules has nothing to override and the entity stays
    /// withheld.</summary>
    [Fact]
    public void TheFallbackSwitchAloneDoesNotPublishTheSwitchOverride()
    {
        var spy = new Spy();
        spy.Fallback = "Default Switch";

        var entity = Get(MqttEntityTable.Build(spy.Spec("Dev")), "vm_dev_switch_override");

        Assert.False(entity.IsPublished(null));
    }

    /// <summary>A machine on the fallback switch reads as being on it, and the value it reads back is
    /// accepted — so the option list and the reading cannot disagree.</summary>
    [Fact]
    public void TheSwitchOverrideBindsAndReadsTheFallbackSwitch()
    {
        var spy = new Spy();
        spy.Switches = ["Bridged"];
        spy.Fallback = "Default Switch";
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        var entity = Get(set, "vm_dev_switch_override");

        spy.State.SetVms([new VmStatus
        {
            Id = "Dev", Name = "Dev", State = "Off", SwitchId = Spy.SwitchIdOf("Default Switch"),
        }]);
        Assert.Equal("Default Switch", entity.ReadState());

        var verdict = Send(entity, entity.ReadState()!);
        Assert.True(verdict.IsAccepted);
        verdict.Run!(CancellationToken.None).Wait();

        Assert.Equal(("Dev", Spy.SwitchIdOf("Default Switch")), Assert.Single(spy.Overrides));
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

        Assert.Equal(("Dev", Spy.SwitchIdOf("Bridged")), Assert.Single(spy.Overrides));   // bound by switch ID
    }

    /// <summary>The shown selection is always one of the options (issue #84): a VM on a switch no rule
    /// names reads as no current value, and a rule-named switch reads in the options' own spelling, so
    /// a receiver sending the shown value back is never refused.</summary>
    [Fact]
    public void TheSwitchOverrideShowsOnlyASwitchItOffers()
    {
        var spy = new Spy();
        spy.Switches = ["Bridged"];
        var set = MqttEntityTable.Build(spy.Spec("Dev"));
        var entity = Get(set, "vm_dev_switch_override");

        spy.State.SetVms([new VmStatus { Id = "Dev", Name = "Dev", State = "Running", Switch = "Default Switch", SwitchId = "SW-DEFAULT" }]);
        Assert.Equal(MqttPayload.None, entity.ReadState());
        Assert.Equal("Default Switch", Get(set, "vm_dev_switch").ReadState());   // the actual switch stays readable

        // Matched by switch ID, whatever the case WMI spells it in, and shown in the options' own label.
        spy.State.SetVms([new VmStatus { Id = "Dev", Name = "Dev", State = "Running", Switch = "renamed", SwitchId = Spy.SwitchIdOf("Bridged").ToLowerInvariant() }]);
        Assert.Equal("Bridged", entity.ReadState());
        Assert.True(Send(entity, entity.ReadState()!).IsAccepted);
    }

    /// <summary>A receiver holding a stale option list must not be able to bind a switch no rule names.
    /// Sent through the entity's own Accept, which is the only way a command reaches the table.</summary>
    [Fact]
    public void TheSwitchOverrideRefusesASwitchNoRuleNames()
    {
        var spy = new Spy();
        spy.Switches = ["Bridged"];
        var set = MqttEntityTable.Build(spy.Spec("Dev"));

        var verdict = Send(Get(set, "vm_dev_switch_override"), "Guest Only");

        Assert.Equal(MqttCommandOutcome.NotAnOption, verdict.Outcome);
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
    [InlineData("vm_dev_switch_override", MqttEntityTable.VmGroup)]
    [InlineData("service_vmms_state",     MqttEntityTable.ServicesGroup)]
    [InlineData("service_vmms_start",     MqttEntityTable.ServicesGroup)]
    [InlineData("service_vmms_stop",      MqttEntityTable.ServicesGroup)]
    [InlineData("vm_dev_switch",          MqttEntityTable.DiagnosticsGroup)]
    [InlineData("vm_dev_ip",              MqttEntityTable.DiagnosticsGroup)]
    [InlineData("vm_dev_uptime",          MqttEntityTable.DiagnosticsGroup)]
    [InlineData("vm_dev_operation",       MqttEntityTable.DiagnosticsGroup)]
    [InlineData("vm_dev_cpu",             MqttEntityTable.MetricsGroup)]
    [InlineData("vm_dev_memory",          MqttEntityTable.MetricsGroup)]
    [InlineData("vm_dev_vhd",             MqttEntityTable.MetricsGroup)]
    public void EveryEntityCarriesItsDeclaredGroup(string entityId, string group)
        => Assert.Equal(group, Get(MqttEntityTable.Build(new Spy().Spec("Dev")), entityId).Group);

    /// <summary>Every power button files under the machines' own group, so none of them sits outside the
    /// toggle that switches the machine controls off.</summary>
    [Fact]
    public void EveryPowerButtonCarriesTheVmGroup()
    {
        var set = MqttEntityTable.Build(new Spy().Spec("Dev"));

        Assert.All(PowerButtonIds("dev"),
                   id => Assert.Equal(MqttEntityTable.VmGroup, Get(set, id).Group));
    }

    /// <summary>Every entity belongs to a group the app DECLARED. An unknown key reads as "always on" at
    /// the receiver, so the settings panel would offer no way to switch it off.</summary>
    [Fact]
    public void EveryEntityBelongsToADeclaredGroup()
    {
        var declared = MqttEntityTable.Groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var set = MqttEntityTable.Build(new Spy().Spec("Dev"));

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
    /// The name-derived addresses a pre-2.7 build left retained, pinned exactly. The publisher empties
    /// what is named ONCE and writes the fact down permanently, so a wrong pair deletes a topic
    /// belonging to something else and cannot be taken back, and a missing pair leaves a ghost entity
    /// with nothing left to remove it. Migrating and the channel list stay empty by declaration.
    /// </summary>
    [Fact]
    public void RetiredFor_NamesEveryNameKeyedAddress()
    {
        var retired = MqttEntityTable.RetiredFor([new VmRef("id-1", "Web Server (2)")]);

        Assert.Equal(
            [
                "switch/vm_web_server_2",
                "binary_sensor/vm_web_server_2_running",
                "sensor/vm_web_server_2_state",
                "sensor/vm_web_server_2_switch",
                "sensor/vm_web_server_2_ip",
                "sensor/vm_web_server_2_uptime",
                "sensor/vm_web_server_2_operation",
                "select/vm_web_server_2_power",
                "select/vm_web_server_2_switch_override",
            ],
            retired.Select(r => $"{r.Component}/{r.EntityId}"));

        Assert.Empty(MqttEntityTable.RetiredChannels);
    }

    /// <summary>The host-network addresses handed over from single-component discovery, pinned exactly.
    /// Each is published as a hand-over flag and then emptied, so a wrong pair clears a topic belonging
    /// to something else, and a missing one leaves a second declaration of a live entity standing.</summary>
    [Fact]
    public void Migrating_NamesEveryHandedOverAddress()
        => Assert.Equal(
            [
                "sensor/network_rule",
                "sensor/network_switch",
                "sensor/network_adapter",
                "sensor/network_host_ip",
                "sensor/network_gateway",
                "sensor/network_apply_status",
                "binary_sensor/network_bridge_healthy",
                "button/network_recheck",
                "button/network_repair",
            ],
            MqttEntityTable.Migrating.Select(m => $"{m.Component}/{m.EntityId}"));
}
