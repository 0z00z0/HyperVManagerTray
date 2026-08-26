using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.Discovery;

namespace HyperVManagerTray.Services;

/// <summary>Everything the entity table needs from the app: what to publish about, where to read it, and
/// what an inbound command runs. Side effects arrive as delegates, so the table composes with no WMI, no
/// broker and no WinUI.</summary>
public sealed record MqttEntitySpec
{
    /// <summary>The managed VMs, in config order.</summary>
    public required IReadOnlyList<string> VmNames { get; init; }

    /// <summary>The switches the rules name — the options a switch-override may pick from. Read on
    /// every announcement pass, so a rule edit reaches the receiver without the table being rebuilt.</summary>
    public required Func<IReadOnlyList<string>> RuleSwitches { get; init; }

    public required MqttStateCache State { get; init; }

    /// <summary>A VM's cached guest IP, or null when none is known.</summary>
    public required Func<string, string?> VmIp { get; init; }

    public required Func<CancellationToken, Task> ReCheckNetwork { get; init; }

    public required Func<CancellationToken, Task> RepairHostNetworking { get; init; }

    /// <summary>Requests a power verb for one VM. Reached only through <see cref="MqttCommandGate"/>.</summary>
    public required Func<string, VmOpKind, CancellationToken, Task> Power { get; init; }

    /// <summary>Forces one VM onto one switch.</summary>
    public required Func<string, string, CancellationToken, Task> OverrideSwitch { get; init; }
}

/// <summary>
/// This app's MQTT entities (issue #75), composed from the state the five existing events already
/// deliver. Nothing here samples anything.
/// </summary>
/// <remarks>
/// <para>Per-VM ids are composed from a slug, and <see cref="VmSlugAllocator"/> settles it against the
/// ids actually emitted rather than against the slug alone: a VM name is a runtime string, the id is
/// both the state topic and the command topic, and a shared one routes one VM's commands to another.
/// Slugs that differ still compose ids that clash — "X switch" reaches the very id "X"'s diagnostics
/// sensor claims — so a slug is taken only once every id composed from it is free, which also keeps one
/// collision suffix applying uniformly to that VM's entities. The slug is cut to leave room for the
/// longest suffix as well, because <see cref="MqttEntityId.MaxLength"/> caps the composed id and a VM
/// name of ordinary length would otherwise throw the whole table out.</para>
/// <para>Group membership is what a user switches off. A withheld entity keeps its whole entry in the
/// document and reads as unavailable, so a toggle never costs a receiver's registry record.</para>
/// </remarks>
public static class MqttEntityTable
{
    /// <summary>The topic root every entity of this app publishes under, and the stem of the default
    /// device id.</summary>
    public const string TopicRoot = "hypervmanagertray";

    /// <summary>The host-network entities and their two command buttons.</summary>
    public const string NetworkGroup = "network";

    /// <summary>Each VM's state and its power, on/off and switch-override controls.</summary>
    public const string VmGroup = "vm";

    /// <summary>Each VM's switch, IP, uptime and last operation, plus the host's IP and gateway.</summary>
    public const string DiagnosticsGroup = "diagnostics";

    /// <summary>CPU, memory and VHD. Off by default: they only flow while
    /// <c>VmService.SubscribeMetrics()</c> is held, which is a 2.5 s WMI loop.</summary>
    public const string MetricsGroup = "metrics";

    /// <summary>The publish groups this app declares, in the order the settings panel renders them.</summary>
    public static IReadOnlyList<PublishGroup> Groups =>
    [
        new PublishGroup(NetworkGroup, "Host network",
            Info: "The rule in force, the virtual switch and host adapter it bound, and two buttons: "
                + "re-check the network, and repair host networking."),
        new PublishGroup(VmGroup, "Virtual machines",
            Info: "Each managed VM's state, an on/off switch, a power verb, and a switch override."),
        new PublishGroup(DiagnosticsGroup, "Diagnostics",
            Info: "Host IP and gateway, and each VM's switch, guest IP, uptime and last operation."),
        new PublishGroup(MetricsGroup, "VM metrics",
            Description: "Off by default: these need a 2.5-second Hyper-V query loop the app "
                       + "otherwise never runs.",
            DefaultOn: false,
            Info: "Each VM's CPU share, assigned memory and virtual disk size."),
    ];

    /// <summary>
    /// What an earlier implementation left retained on a broker: an entity handed over from its own
    /// single-component config (<see cref="MigratingEntity"/>), one that no longer exists at all
    /// (<see cref="RetiredEntity"/>), and a value topic no entity claims (<see cref="RetiredChannel"/>).
    ///
    /// <para><b>All three are empty, and that is a statement rather than an omission.</b> No released
    /// build of this app has ever published to a broker — every tag is free of the integration — so
    /// there is no installed base to carry across and nothing retained under this topic root that a
    /// declaration could reach. A guessed key would be worse than none: the publisher empties exactly
    /// what is named, once, and writes the fact down permanently.</para>
    ///
    /// <para>The entity ids below are the ones the removed pre-release integration used, so a broker a
    /// development build published to is taken over rather than orphaned.</para>
    /// </summary>
    public static IReadOnlyList<MigratingEntity> Migrating => [];

    /// <inheritdoc cref="Migrating"/>
    public static IReadOnlyList<RetiredEntity> Retired => [];

    /// <inheritdoc cref="Migrating"/>
    public static IReadOnlyList<RetiredChannel> RetiredChannels => [];

    /// <summary>The head every per-VM id carries, and every suffix one ends in — the bare power
    /// switch's empty suffix included. The slug budget and the collision check are both composed from
    /// these, so a suffix missing here is one nothing sizes or de-duplicates against.</summary>
    internal const string VmIdPrefix = "vm_";

    /// <inheritdoc cref="VmIdPrefix"/>
    internal static readonly IReadOnlyList<string> VmIdSuffixes =
    [
        "", "_state", "_running", "_switch", "_ip", "_uptime", "_operation",
        "_cpu", "_memory", "_vhd", "_power", "_switch_override",
    ];

    /// <summary>Builds the whole table. Called again — through
    /// <c>DiscoveryPublisher.SetEntities</c> — whenever the managed VM list changes.</summary>
    public static MqttEntitySet Build(MqttEntitySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var entities = new List<MqttEntity>();
        entities.AddRange(NetworkEntities(spec));

        var slugs = new VmSlugAllocator();
        // A hand-edited "name": null must not throw the whole table out; it slugs to the id alphabet's
        // own fallback and is separated from any sibling by the collision suffix.
        foreach (string name in spec.VmNames.Select(n => n ?? string.Empty))
            entities.AddRange(VmEntities(spec, name, slugs.Allocate(name)));

        return new MqttEntitySet(entities);
    }

    /// <summary>Hands out one slug per VM, such that every id composed from it is free and inside
    /// <see cref="MqttEntityId.MaxLength"/>.</summary>
    /// <remarks>Not <see cref="MqttEntityIdAllocator"/>, which resolves one id at a time: a VM's ids
    /// stand or fall together, and a slug free as an id can still compose one another VM has taken.</remarks>
    private sealed class VmSlugAllocator
    {
        /// <summary>The longest a slug may be for its longest composed id to still fit.</summary>
        private static readonly int Budget =
            MqttEntityId.MaxLength - VmIdPrefix.Length - VmIdSuffixes.Max(s => s.Length);

        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

        public string Allocate(string? vmName)
        {
            string stem = Cut(MqttEntityId.Normalise(vmName), Budget);
            if (Claim(stem)) return stem;

            // The stem is cut again to leave room for the suffix, so a truncated name cannot push a
            // slug past the budget and collide once more after that truncation.
            for (int n = 2; ; n++)
            {
                string suffix = $"_{n}";
                string candidate = Cut(stem, Budget - suffix.Length) + suffix;
                if (Claim(candidate)) return candidate;
            }
        }

        /// <summary>Takes the slug, but only if every id composed from it is still free.</summary>
        private bool Claim(string slug)
        {
            var ids = VmIdSuffixes.Select(suffix => VmIdPrefix + slug + suffix).ToList();
            if (ids.Exists(_taken.Contains)) return false;

            foreach (string id in ids) _taken.Add(id);
            return true;
        }

        /// <summary>Cuts to length, dropping any underscore the cut exposed: a trailing one doubles
        /// against the next suffix, and a doubled underscore is not in the id alphabet.</summary>
        private static string Cut(string slug, int length)
        {
            string cut = (slug.Length <= length ? slug : slug[..length]).TrimEnd('_');
            return cut.Length > 0 ? cut : MqttEntityId.Fallback;
        }
    }

    // ── Host network ────────────────────────────────────────────────────────────

    private static IEnumerable<MqttEntity> NetworkEntities(MqttEntitySpec spec)
    {
        var state = spec.State;

        yield return new MqttSensor
        {
            EntityId = "network_rule",
            Name     = "Active rule",
            Group    = NetworkGroup,
            Icon     = "mdi:lan",
            Read     = () => Text(state.Network?.RuleName),
        };
        yield return new MqttSensor
        {
            EntityId = "network_switch",
            Name     = "Virtual switch",
            Group    = NetworkGroup,
            Icon     = "mdi:switch",
            Read     = () => Text(state.Network?.VirtualSwitch),
        };
        yield return new MqttSensor
        {
            EntityId = "network_adapter",
            Name     = "Host adapter",
            Group    = NetworkGroup,
            Icon     = "mdi:ethernet",
            Read     = () => Text(state.Network?.HostAdapterName),
        };
        yield return new MqttSensor
        {
            EntityId = "network_host_ip",
            Name     = "Host IP",
            Group    = DiagnosticsGroup,
            Category = MqttEntityCategory.Diagnostic,
            Icon     = "mdi:ip-network",
            Read     = () => Text(state.Network?.HostIp),
        };
        yield return new MqttSensor
        {
            EntityId = "network_gateway",
            Name     = "Gateway",
            Group    = DiagnosticsGroup,
            Category = MqttEntityCategory.Diagnostic,
            Icon     = "mdi:router-network",
            Read     = () => Text(state.Network?.Gateway),
        };
        yield return new MqttSensor
        {
            EntityId = "network_apply_status",
            Name     = "Apply status",
            Group    = NetworkGroup,
            Icon     = "mdi:check-network",
            Read     = () => state.Network is { } r ? r.ApplyStatus.ToString() : null,
        };
        yield return new MqttBinarySensor
        {
            EntityId    = "network_bridge_healthy",
            Name        = "Bridge healthy",
            Group       = NetworkGroup,
            DeviceClass = "connectivity",
            // Inverted: IsFailure answers "is something wrong", and connectivity's ON means "fine".
            Read        = () => state.Network is { } r ? !NetworkStatusUi.IsFailure(r.ApplyStatus) : null,
        };
        yield return new MqttButton
        {
            EntityId = "network_recheck",
            Name     = "Re-check network",
            Group    = NetworkGroup,
            Icon     = "mdi:refresh",
            Press    = () => MqttCommandVerdict.Accept(spec.ReCheckNetwork),
        };
        yield return new MqttButton
        {
            EntityId = "network_repair",
            Name     = "Repair host networking",
            Group    = NetworkGroup,
            Icon     = "mdi:wrench",
            Press    = () => MqttCommandVerdict.Accept(spec.RepairHostNetworking),
        };
    }

    // ── Per VM ──────────────────────────────────────────────────────────────────

    private static IEnumerable<MqttEntity> VmEntities(MqttEntitySpec spec, string vmName, string slug)
    {
        var state = spec.State;

        yield return new MqttSensor
        {
            EntityId = $"vm_{slug}_state",
            Name     = $"{vmName} state",
            Group    = VmGroup,
            Icon     = "mdi:server",
            Read     = () => Text(state.Vm(vmName)?.State),
        };
        yield return new MqttBinarySensor
        {
            EntityId    = $"vm_{slug}_running",
            Name        = $"{vmName} running",
            Group       = VmGroup,
            DeviceClass = "running",
            Read        = () => state.Vm(vmName)?.IsRunning,
        };
        yield return new MqttSensor
        {
            EntityId = $"vm_{slug}_switch",
            Name     = $"{vmName} switch",
            Group    = DiagnosticsGroup,
            Category = MqttEntityCategory.Diagnostic,
            Icon     = "mdi:switch",
            Read     = () => Text(state.Vm(vmName)?.Switch),
        };
        yield return new MqttSensor
        {
            EntityId = $"vm_{slug}_ip",
            Name     = $"{vmName} IP",
            Group    = DiagnosticsGroup,
            Category = MqttEntityCategory.Diagnostic,
            Icon     = "mdi:ip-network",
            Read     = () => Text(spec.VmIp(vmName)),
        };
        yield return new MqttSensor
        {
            EntityId = $"vm_{slug}_uptime",
            Name     = $"{vmName} uptime",
            Group    = DiagnosticsGroup,
            Category = MqttEntityCategory.Diagnostic,
            Icon     = "mdi:timer-outline",
            Read     = () => Text(UptimeFormatter.Format(state.Vm(vmName))),
        };
        yield return new MqttSensor
        {
            EntityId = $"vm_{slug}_operation",
            Name     = $"{vmName} last operation",
            Group    = DiagnosticsGroup,
            Category = MqttEntityCategory.Diagnostic,
            Icon     = "mdi:history",
            Read     = () => Text(state.Operation(vmName)),
        };

        // CPU, memory and VHD only carry a reading while VmService.SubscribeMetrics() is held —
        // see Helpers\MqttMetricsHold.cs, which holds it exactly while this group is on and the
        // connection is live.
        yield return new MqttSensor
        {
            EntityId   = $"vm_{slug}_cpu",
            Name       = $"{vmName} CPU",
            Group      = MetricsGroup,
            Category   = MqttEntityCategory.Diagnostic,
            StateClass = MqttStateClass.Measurement,
            Unit       = "%",
            Icon       = "mdi:cpu-64-bit",
            Read       = () => state.Vm(vmName) is { } s ? MqttPayload.Number(s.Cpu) : null,
        };
        yield return new MqttSensor
        {
            EntityId    = $"vm_{slug}_memory",
            Name        = $"{vmName} memory",
            Group       = MetricsGroup,
            Category    = MqttEntityCategory.Diagnostic,
            DeviceClass = "data_size",
            StateClass  = MqttStateClass.Measurement,
            Unit        = "MiB",
            Read        = () => state.Vm(vmName) is { } s ? MqttPayload.Number(Math.Round(s.MemAssignedMb, 1)) : null,
        };
        yield return new MqttSensor
        {
            EntityId    = $"vm_{slug}_vhd",
            Name        = $"{vmName} VHD",
            Group       = MetricsGroup,
            Category    = MqttEntityCategory.Diagnostic,
            DeviceClass = "data_size",
            StateClass  = MqttStateClass.Measurement,
            Unit        = "GiB",
            Read        = () => state.Vm(vmName) is { } s ? MqttPayload.Number(Math.Round(s.VhdGb, 2)) : null,
        };

        yield return new MqttSwitch
        {
            EntityId = $"vm_{slug}",
            Name     = vmName,
            Group    = VmGroup,
            Icon     = "mdi:power",
            Read     = () => state.Vm(vmName)?.IsRunning,
            Apply    = on => MqttCommandGate.Running(
                state.Vm(vmName)?.State, on, (kind, ct) => spec.Power(vmName, kind, ct)),
        };

        yield return new MqttSelect
        {
            EntityId = $"vm_{slug}_power",
            Name     = $"{vmName} power",
            Group    = VmGroup,
            Icon     = "mdi:power-settings",
            Options  = () => MqttCommandGate.PowerOptions,
            // A verb is an event, not a state: there is no "current power verb" to report, and
            // announcing the last one requested would read as the VM being in it.
            Read     = () => null,
            Apply    = option => MqttCommandGate.ParseVerb(option) is { } kind
                ? MqttCommandGate.Power(state.Vm(vmName)?.State, kind, ct => spec.Power(vmName, kind, ct))
                : MqttCommandVerdict.NotAnOption($"'{option}' is not a power verb."),
        };

        yield return new MqttSelect
        {
            EntityId = $"vm_{slug}_switch_override",
            Name     = $"{vmName} switch override",
            Group    = VmGroup,
            Category = MqttEntityCategory.Config,
            Icon     = "mdi:swap-horizontal",
            // The receiver rejects a select with no options, so with no rules configured the entity is
            // WITHHELD rather than dropped: it keeps its entry and its registry record and returns the
            // moment a rule names a switch.
            Include  = () => spec.RuleSwitches().Count > 0,
            Options  = spec.RuleSwitches,
            Read     = () => Text(state.Vm(vmName)?.Switch),
            Apply    = option => MqttCommandGate.Override(
                spec.RuleSwitches(), option, (name, ct) => spec.OverrideSwitch(vmName, name, ct)),
        };
    }

    /// <summary>Null publishes the literal <c>None</c>, so a missing reading clears the entity — an
    /// empty payload is ignored by the receiver and the stale value would stand.</summary>
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
