using System.Management;
using HyperVManagerTray.Helpers;

namespace HyperVManagerTray.Services;

/// <summary>
/// A one-shot, READ-ONLY enumeration of the live host: the Hyper-V virtual switches, the VMs, each VM's
/// synthetic network adapters, and the physical NICs. Exists to feed the Settings window's identity
/// pickers (issue #41) — the fields (virtual switch, target VMs, adapter MAC, a managed VM's NIC name)
/// that name things this app already discovers, but which the user had to retype by hand, making a typo
/// the primary failure mode of the rules editor: a misspelt switch or VM produces a rule that silently
/// never matches (the failure that cost hours on issue #17).
///
/// <para><b>This class never mutates anything.</b> Every query is a SELECT. It does not bind a switch,
/// touch a VM's power state, or rename an adapter — those live in <see cref="HyperVManager"/> and the
/// rename flow, and must stay there. Populating a picker must cost the host nothing but a read.</para>
///
/// <para><b>Never throws, always answers.</b> The Hyper-V half and the adapter half are guarded apart, so
/// an unreachable Hyper-V host (the service stopped, WMI wedged, no virtualization role) still returns the
/// physical adapters. The Hyper-V half is all or nothing and says which: every object is referenced by its
/// identifier, and a partial list must never pass for "this object does not exist".</para>
///
/// <para><b>Blocking.</b> <see cref="Read"/> connects to WMI and can stall for seconds on a degraded
/// host. It must only ever be called from a background thread — see
/// <c>SettingsWindow.LoadHostInventoryAsync</c>. Nothing here may run on the UI thread: Settings opening
/// must never wait on WMI.</para>
///
/// <para><b>Leaves nothing behind.</b> Every query here disposes THREE things, not two: the
/// <see cref="ManagementObjectSearcher"/>, the <see cref="ManagementObjectCollection"/> its
/// <c>Get()</c> returns, and each <see cref="ManagementObject"/> in it. The collection is the one that
/// is easy to miss and the one that costs: <c>Get()</c> defaults to <c>Rewindable=true</c>, so the
/// collection takes its own <c>IEnumWbemClassObject</c> clone that disposing the searcher does not
/// release. This is a long-lived tray process and <see cref="Read"/> runs on EVERY <c>BuildSections()</c>
/// — every Settings open and every <c>RefreshValuesFromConfig</c> (add a VM, stop managing one, add the
/// current network, reload) — so a leak of three per read is a leak per click, stranding live COM
/// enumerators against <c>root\virtualization\v2</c> for as long as the app runs. A picker must cost the
/// host nothing but a read, and that has to include what it hands back.</para>
///
/// <para>Deliberately standalone rather than a method on <see cref="VmService"/>: that service owns a
/// long-lived scope, an event watcher, a metrics loop and refresh-failure recovery, all driven by the
/// dashboard's lifetime. Settings needs one cold read with no subscription, and must work when the
/// dashboard has never been opened.</para>
/// </summary>
public static class HostInventory
{
    private const string Namespace = @"root\virtualization\v2";

    /// <summary>
    /// What the host currently has. <see cref="HyperV"/> says whether Hyper-V could be read at all; when it
    /// could not, its lists are empty and mean nothing — see <see cref="HyperVInventory"/>. The adapters
    /// come from the network stack and are offered either way.
    /// </summary>
    public sealed record Snapshot(HyperVInventory HyperV, IReadOnlyList<PhysicalAdapterInfo> Adapters)
    {
        public static readonly Snapshot Empty = new(HyperVInventory.Unreadable, []);

        /// <summary>The adapters of the VM with <paramref name="vmId"/>; empty when the VM is unknown to the
        /// host — a normal state, since the host may not have been readable.</summary>
        public IReadOnlyList<HostNic> NicsFor(string? vmId) => HyperV.NicsFor(vmId);
    }

    /// <summary>
    /// Reads the host. BLOCKING — background threads only (see the class remarks). Never throws.
    /// </summary>
    public static Snapshot Read()
    {
        // Physical adapters first, and outside the Hyper-V scope entirely: they come from
        // NetworkInterface/registry, not WMI virtualization, so they must still be offered when the
        // Hyper-V side is unreachable. This is the same enumeration the Adapters section already uses.
        IReadOnlyList<PhysicalAdapterInfo> adapters;
        try   { adapters = AdapterMatcher.GetPhysicalAdapters(); }
        catch { adapters = []; }

        return new Snapshot(ReadHyperV(), adapters);
    }

    /// <summary>
    /// Reads the switches, VMs and VM adapters by identifier. Readable only when every query answered: a
    /// partial read would let the identity migration conclude "no such VM" from a list that simply stopped
    /// short. BLOCKING. Never throws.
    /// </summary>
    public static HyperVInventory ReadHyperV()
    {
        try
        {
            // No EnablePrivileges: this connection only ever reads. The mutating paths ask for
            // privileges because they need them; a picker must not.
            var scope = new ManagementScope(Namespace, new ConnectionOptions());
            WmiLimits.ConnectBounded(scope);   // a stopped or stopping vmms must not hold the picker

            var switches = ReadSwitches(scope);
            var vms      = ReadVms(scope);
            var nics     = ReadNics(scope, vms);
            return new HyperVInventory(true, switches, vms, nics);
        }
        catch
        {
            // No Hyper-V host to talk to, or a query failed part-way: nothing here may be concluded from.
            return HyperVInventory.Unreadable;
        }
    }

    /// <summary>Every virtual switch on the host: its ID (<c>Name</c>) and its name (<c>ElementName</c>).</summary>
    private static IReadOnlyList<HostSwitch> ReadSwitches(ManagementScope scope)
    {
        var list = new List<HostSwitch>();
        using var s = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch"), WmiLimits.Enumeration());
        // `using` on the COLLECTION too, not just the searcher and each object: Get() defaults to
        // Rewindable=true, so the returned collection holds its own IEnumWbemClassObject clone which
        // disposing the searcher does NOT release. See the class remarks for why that matters here.
        using var results = s.Get();
        foreach (ManagementObject o in results)
            using (o)
            {
                var id = HostIdentity.Bare(o["Name"] as string);
                if (id.Length > 0) list.Add(new HostSwitch(id, (o["ElementName"] as string ?? "").Trim()));
            }
        return [.. list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Every VM on the host: its VM ID (<c>Name</c>) and its name (<c>ElementName</c>).</summary>
    private static IReadOnlyList<HostVm> ReadVms(ManagementScope scope)
    {
        var rows = new List<VmRow>();
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery(
            "SELECT ElementName, Name FROM Msvm_ComputerSystem WHERE Caption='Virtual Machine'"), WmiLimits.Enumeration());
        using var results = s.Get();   // Rewindable collection — its own enumerator to release
        foreach (ManagementObject o in results)
            using (o) rows.Add(new VmRow(o["Name"] as string ?? "", o["ElementName"] as string ?? "", 0));
        return [.. HostIdentity.IndexVms(rows).Values
                    .Select(r => new HostVm(r.Id, r.Name))
                    .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Every synthetic adapter, grouped by owning VM ID. All of them, not one per VM: the adapter editor
    /// exists for the VM with a second adapter.
    ///
    /// <para>A per-VM settings class embeds its owning VM's ID in its InstanceID, so the association is a
    /// substring test — the same matching <c>VmService.MatchVm</c> does.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<HostNic>> ReadNics(
        ManagementScope scope, IReadOnlyList<HostVm> vms)
    {
        var byVm = new Dictionary<string, List<HostNic>>(StringComparer.OrdinalIgnoreCase);
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery(
            "SELECT InstanceID, ElementName FROM Msvm_SyntheticEthernetPortSettingData"), WmiLimits.Enumeration());
        using var results = s.Get();   // Rewindable collection — its own enumerator to release
        foreach (ManagementObject o in results)
            using (o)
            {
                var instanceId = o["InstanceID"] as string ?? "";
                if (HostIdentity.NicIdFromInstanceId(instanceId) is not { } nicId) continue;
                var owner = vms.FirstOrDefault(v => instanceId.Contains(v.Id, StringComparison.OrdinalIgnoreCase));
                if (owner is null) continue;

                var list = byVm.TryGetValue(owner.Id, out var existing) ? existing : byVm[owner.Id] = [];
                if (!list.Any(n => HostIdentity.Same(n.Id, nicId)))
                    list.Add(new HostNic(nicId, (o["ElementName"] as string ?? "").Trim()));
            }

        return byVm.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<HostNic>)[.. kv.Value.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)],
            StringComparer.OrdinalIgnoreCase);
    }
}
