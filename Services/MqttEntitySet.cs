using System.Globalization;
using System.Text;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using ZeroZero.Mqtt.HomeAssistant;

namespace HyperVManagerTray.Services;

/// <summary>
/// Everything the entity set needs from the app: what to publish about, where to read it, and what an
/// inbound command runs. Side effects arrive as delegates, so the set composes with no WMI, no broker
/// and no WinUI (issue #75).
/// </summary>
public sealed record MqttEntitySpec
{
    /// <summary>The managed VMs, in config order.</summary>
    public required IReadOnlyList<string> VmNames { get; init; }

    /// <summary>The switches the rules name — the options a switch-override may pick from.</summary>
    public required IReadOnlyList<string> RuleSwitches { get; init; }

    public required MqttStateCache State { get; init; }

    /// <summary>A VM's cached guest IP, or null when none is known.</summary>
    public required Func<string, string?> VmIp { get; init; }

    /// <summary>Whether CPU, memory and VHD are announced at all. Read per discovery pass, so the
    /// toggle takes effect on a reload without the set being rebuilt.</summary>
    public required Func<bool> PublishMetrics { get; init; }

    public required Func<CancellationToken, Task> ReCheckNetwork { get; init; }

    public required Func<CancellationToken, Task> RepairHostNetworking { get; init; }

    /// <summary>Requests a power verb for one VM. Reached only through
    /// <see cref="MqttCommandGate.Power"/>.</summary>
    public required Func<string, VmOpKind, CancellationToken, Task> Power { get; init; }

    /// <summary>Forces one VM onto one switch.</summary>
    public required Func<string, string, CancellationToken, Task> OverrideSwitch { get; init; }

    /// <summary>Reports a command the gate turned down. Nothing runs; the reason is recorded.</summary>
    public required Action<string> Refuse { get; init; }
}

/// <summary>The stable topic segment each VM owns, derived from its name.</summary>
public static class MqttObjectIds
{
    /// <summary>Longest slug taken from a VM name — long enough to stay recognisable inside a topic.</summary>
    public const int MaxLength = 32;

    /// <summary>VM name → topic-safe slug, in input order. Two names that reduce to the same slug are
    /// separated by a numeric suffix: the slug is the state and command topic, so a collision would
    /// route one VM's commands to another.</summary>
    public static IReadOnlyDictionary<string, string> ForVms(IEnumerable<string> vmNames)
    {
        var map  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (string name in vmNames ?? [])
        {
            if (map.ContainsKey(name)) continue;
            string root = Slug(name);
            string slug = root;
            for (int n = 2; !used.Add(slug); n++) slug = $"{root}_{n}";
            map[name] = slug;
        }
        return map;
    }

    /// <summary>A name reduced to lower-case ASCII alphanumerics and single underscores.</summary>
    public static string Slug(string? name)
    {
        var sb = new StringBuilder((name ?? string.Empty).Length);
        foreach (char c in (name ?? string.Empty).ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        string slug = sb.ToString().Trim('_');
        if (slug.Length > MaxLength) slug = slug[..MaxLength].TrimEnd('_');
        return slug.Length == 0 ? "vm" : slug;
    }
}

/// <summary>
/// HyperVManagerTray's Home Assistant entities (issue #75), declared once. Everything published comes
/// from the events the app already raises — the network monitor's applied result and VmService's
/// status and operation pushes — so nothing here adds a poll.
/// </summary>
public static class MqttEntitySet
{
    /// <summary>The topic root every entity of this app publishes under.</summary>
    public const string TopicRoot = "hypervmanagertray";

    public static HaEntitySet Build(MqttEntitySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var entities = new List<HaEntity>();
        entities.AddRange(NetworkEntities(spec));

        var ids = MqttObjectIds.ForVms(spec.VmNames);
        foreach (var (name, slug) in ids) entities.AddRange(VmEntities(spec, name, slug));

        return new HaEntitySet(entities);
    }

    // ── Host network ────────────────────────────────────────────────────────────

    private static IEnumerable<HaEntity> NetworkEntities(MqttEntitySpec spec)
    {
        var state = spec.State;

        yield return new HaSensor
        {
            ObjectId = "network_rule",
            Name     = "Active rule",
            Icon     = "mdi:lan",
            State    = () => Text(state.Network?.RuleName),
        };
        yield return new HaSensor
        {
            ObjectId = "network_switch",
            Name     = "Virtual switch",
            Icon     = "mdi:switch",
            State    = () => Text(state.Network?.VirtualSwitch),
        };
        yield return new HaSensor
        {
            ObjectId = "network_adapter",
            Name     = "Host adapter",
            Icon     = "mdi:ethernet",
            State    = () => Text(state.Network?.HostAdapterName),
        };
        yield return new HaSensor
        {
            ObjectId = "network_host_ip",
            Name     = "Host IP",
            Role     = HaEntityRole.Diagnostic,
            Icon     = "mdi:ip-network",
            State    = () => Text(state.Network?.HostIp),
        };
        yield return new HaSensor
        {
            ObjectId = "network_gateway",
            Name     = "Gateway",
            Role     = HaEntityRole.Diagnostic,
            Icon     = "mdi:router-network",
            State    = () => Text(state.Network?.Gateway),
        };
        yield return new HaSensor
        {
            ObjectId = "network_apply_status",
            Name     = "Apply status",
            Icon     = "mdi:check-network",
            State    = () => state.Network is { } r ? r.ApplyStatus.ToString() : null,
        };
        yield return new HaBinarySensor
        {
            ObjectId    = "network_bridge_healthy",
            Name        = "Bridge healthy",
            DeviceClass = "connectivity",
            // Inverted: IsFailure answers "is something wrong", and connectivity's ON means "fine".
            State       = () => state.Network is { } r ? !NetworkStatusUi.IsFailure(r.ApplyStatus) : null,
        };
        yield return new HaButton
        {
            ObjectId = "network_recheck",
            Name     = "Re-check network",
            Icon     = "mdi:refresh",
            Press    = spec.ReCheckNetwork,
        };
        yield return new HaButton
        {
            ObjectId = "network_repair",
            Name     = "Repair host networking",
            Icon     = "mdi:wrench",
            Press    = spec.RepairHostNetworking,
        };
    }

    // ── Per VM ──────────────────────────────────────────────────────────────────

    private static IEnumerable<HaEntity> VmEntities(MqttEntitySpec spec, string vmName, string slug)
    {
        var state = spec.State;

        yield return new HaSensor
        {
            ObjectId = $"vm_{slug}_state",
            Name     = $"{vmName} state",
            Icon     = "mdi:server",
            State    = () => Text(state.Vm(vmName)?.State),
        };
        yield return new HaBinarySensor
        {
            ObjectId    = $"vm_{slug}_running",
            Name        = $"{vmName} running",
            DeviceClass = "running",
            State       = () => state.Vm(vmName)?.IsRunning,
        };
        yield return new HaSensor
        {
            ObjectId = $"vm_{slug}_switch",
            Name     = $"{vmName} switch",
            Role     = HaEntityRole.Diagnostic,
            Icon     = "mdi:switch",
            State    = () => Text(state.Vm(vmName)?.Switch),
        };
        yield return new HaSensor
        {
            ObjectId = $"vm_{slug}_ip",
            Name     = $"{vmName} IP",
            Role     = HaEntityRole.Diagnostic,
            Icon     = "mdi:ip-network",
            State    = () => Text(spec.VmIp(vmName)),
        };
        yield return new HaSensor
        {
            ObjectId = $"vm_{slug}_uptime",
            Name     = $"{vmName} uptime",
            Role     = HaEntityRole.Diagnostic,
            Icon     = "mdi:timer-outline",
            State    = () => Text(UptimeFormatter.Format(state.Vm(vmName))),
        };
        yield return new HaSensor
        {
            ObjectId = $"vm_{slug}_operation",
            Name     = $"{vmName} last operation",
            Role     = HaEntityRole.Diagnostic,
            Icon     = "mdi:history",
            State    = () => Text(state.Operation(vmName)),
        };

        // CPU, memory and VHD only flow while VmService.SubscribeMetrics() is held, so they are
        // announced only while the toggle asks for them and evicted when it stops.
        yield return new HaSensor
        {
            ObjectId          = $"vm_{slug}_cpu",
            Name              = $"{vmName} CPU",
            Role              = HaEntityRole.Diagnostic,
            Include           = spec.PublishMetrics,
            StateClass        = "measurement",
            UnitOfMeasurement = "%",
            Icon              = "mdi:cpu-64-bit",
            State             = () => state.Vm(vmName) is { } s ? Number(s.Cpu) : null,
        };
        yield return new HaSensor
        {
            ObjectId          = $"vm_{slug}_memory",
            Name              = $"{vmName} memory",
            Role              = HaEntityRole.Diagnostic,
            Include           = spec.PublishMetrics,
            DeviceClass       = "data_size",
            StateClass        = "measurement",
            UnitOfMeasurement = "MiB",
            State             = () => state.Vm(vmName) is { } s ? Number(Math.Round(s.MemAssignedMb, 1)) : null,
        };
        yield return new HaSensor
        {
            ObjectId          = $"vm_{slug}_vhd",
            Name              = $"{vmName} VHD",
            Role              = HaEntityRole.Diagnostic,
            Include           = spec.PublishMetrics,
            DeviceClass       = "data_size",
            StateClass        = "measurement",
            UnitOfMeasurement = "GiB",
            State             = () => state.Vm(vmName) is { } s ? Number(Math.Round(s.VhdGb, 2)) : null,
        };

        yield return new HaSwitch
        {
            ObjectId = $"vm_{slug}",
            Name     = vmName,
            Icon     = "mdi:power",
            State    = () => state.Vm(vmName)?.IsRunning,
            Apply    = (on, ct) =>
            {
                string vmState = state.Vm(vmName)?.State ?? string.Empty;
                var verdict = MqttCommandGate.Running(vmState, on, out var kind);
                if (!verdict.Allowed) return Refused(spec, vmName, verdict.Reason);
                return spec.Power(vmName, kind, ct);
            },
        };

        yield return new HaSelect
        {
            ObjectId = $"vm_{slug}_power",
            Name     = $"{vmName} power",
            Icon     = "mdi:power-settings",
            Options  = MqttCommandGate.PowerOptions,
            Apply    = (option, ct) =>
            {
                if (MqttCommandGate.ParseVerb(option) is not { } kind)
                    return Refused(spec, vmName, $"'{option}' is not a power verb.");

                string vmState = state.Vm(vmName)?.State ?? string.Empty;
                var verdict = MqttCommandGate.Power(vmState, kind);
                if (!verdict.Allowed) return Refused(spec, vmName, verdict.Reason);
                return spec.Power(vmName, kind, ct);
            },
        };

        // Home Assistant rejects a select with no options, so with no rule switches configured there
        // is nothing to announce and the override is withheld rather than published empty.
        if (spec.RuleSwitches.Count == 0) yield break;

        yield return new HaSelect
        {
            ObjectId = $"vm_{slug}_switch_override",
            Name     = $"{vmName} switch override",
            Role     = HaEntityRole.Config,
            Icon     = "mdi:swap-horizontal",
            Options  = spec.RuleSwitches,
            Apply    = (option, ct) =>
            {
                var verdict = MqttCommandGate.Override(spec.RuleSwitches, option);
                return verdict.Allowed
                    ? spec.OverrideSwitch(vmName, option.Trim(), ct)
                    : Refused(spec, vmName, verdict.Reason);
            },
        };
    }

    private static Task Refused(MqttEntitySpec spec, string vmName, string reason)
    {
        spec.Refuse($"{vmName}: {reason}");
        return Task.CompletedTask;
    }

    /// <summary>A blank reads as unknown in Home Assistant rather than as an empty value, so nothing
    /// is published until there is something to say.</summary>
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>A machine-readable number: the payload is a protocol value, never display text.</summary>
    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
}
