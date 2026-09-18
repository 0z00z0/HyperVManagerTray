using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>A virtual switch on the host: the ID that identifies it, and its name, which is only shown.</summary>
public sealed record HostSwitch(string Id, string Name);

/// <summary>A VM on the host: the VM ID that identifies it, and its name, which is only shown.</summary>
public sealed record HostVm(string Id, string Name);

/// <summary>A VM's network adapter: the adapter ID that identifies it, and its name, which is only shown.</summary>
public sealed record HostNic(string Id, string Name);

/// <summary>One VM row as <c>Msvm_ComputerSystem</c> reports it.</summary>
public readonly record struct VmRow(string Id, string Name, ushort EnabledState);

/// <summary>
/// What a read of the Hyper-V host found. <see cref="Readable"/> is false when the host could not be read
/// at all — Hyper-V absent, or Virtual Machine Management stopped — and then the lists say nothing: an
/// empty list from an unreadable host is not "no such object", and nothing may be concluded from it.
/// </summary>
public sealed record HyperVInventory(
    bool Readable,
    IReadOnlyList<HostSwitch> Switches,
    IReadOnlyList<HostVm> Vms,
    IReadOnlyDictionary<string, IReadOnlyList<HostNic>> NicsByVmId)
{
    public static readonly HyperVInventory Unreadable = new(
        false, [], [], new Dictionary<string, IReadOnlyList<HostNic>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The adapters of the VM with <paramref name="vmId"/>; empty when the VM is unknown.</summary>
    public IReadOnlyList<HostNic> NicsFor(string? vmId) =>
        vmId is not null && NicsByVmId.TryGetValue(vmId, out var nics) ? nics : [];
}

/// <summary>
/// Identity rules shared by every reader of the host. Pure, so the one defect they exist to prevent —
/// two objects with one display name collapsing into one — is testable without a host.
/// </summary>
public static class HostIdentity
{
    /// <summary>Whether two identifiers name the same object. GUIDs are compared without braces and
    /// without regard to case, since WMI and the network stack spell them differently.</summary>
    public static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(Bare(a), Bare(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>An identifier without surrounding braces or whitespace.</summary>
    public static string Bare(string? id) => (id ?? "").Trim().Trim('{', '}');

    /// <summary>
    /// The VMs keyed by VM ID. Two VMs that share a name stay two entries — a map keyed by name kept only
    /// the last, which could hide a running VM from the guard that refuses a service stop. A row without
    /// an ID identifies nothing and is dropped.
    /// </summary>
    public static IReadOnlyDictionary<string, VmRow> IndexVms(IEnumerable<VmRow> rows)
    {
        var map = new Dictionary<string, VmRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            if (!string.IsNullOrWhiteSpace(row.Id)) map[Bare(row.Id)] = row with { Id = Bare(row.Id) };
        return map;
    }

    /// <summary>
    /// The adapter ID inside a <c>Msvm_SyntheticEthernetPortSettingData.InstanceID</c>, which has the shape
    /// <c>Microsoft:&lt;vm id&gt;\&lt;adapter id&gt;</c>; null when it does not.
    /// </summary>
    public static string? NicIdFromInstanceId(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        int slash = instanceId.LastIndexOf('\\');
        if (slash < 0 || slash == instanceId.Length - 1) return null;
        return Bare(instanceId[(slash + 1)..]);
    }

    /// <summary>
    /// A label per ID that tells objects sharing a name apart: the name alone where it is unique, the name
    /// and the start of the ID where it is not. For lists a person picks from; never parsed back.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Labels(IEnumerable<(string Id, string Name)> items)
    {
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i.Id)).ToList();
        var counts = list.GroupBy(i => i.Name ?? "", StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in list)
        {
            var shown = string.IsNullOrWhiteSpace(name) ? Bare(id) : name;
            labels[id] = counts[name ?? ""] > 1 ? $"{shown} ({Short(id)})" : shown;
        }
        return labels;
    }

    /// <summary>The first eight characters of an ID, enough to tell two same-named objects apart.</summary>
    public static string Short(string id)
    {
        var bare = Bare(id);
        return bare.Length <= 8 ? bare : bare[..8];
    }
}
