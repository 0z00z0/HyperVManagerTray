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
/// <para><b>Never throws, always answers.</b> Each section is independently guarded, so an unreachable
/// Hyper-V host (the service stopped, WMI wedged, no virtualization role) still returns the physical
/// adapters, and a total failure returns <see cref="Snapshot.Empty"/> rather than propagating. That is
/// the whole degradation story for the UI: an editable picker with no items IS the plain text box it
/// replaced, so "enumeration failed" costs the user nothing but the convenience.</para>
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
    /// What the host currently has. Every list is possibly empty — see the class remarks: empty means
    /// "could not be enumerated OR genuinely none", and the UI treats both the same way (no suggestions,
    /// free text only), because it cannot act differently on the difference anyway.
    /// </summary>
    public sealed record Snapshot(
        IReadOnlyList<string> SwitchNames,
        IReadOnlyList<string> VmNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>> NicNamesByVm,
        IReadOnlyList<PhysicalAdapterInfo> Adapters)
    {
        public static readonly Snapshot Empty = new(
            [], [], new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), []);

        /// <summary>
        /// The synthetic adapter names of a managed VM, or an empty list when the VM is unknown to the
        /// host — which is a normal, expected state, not an error: a config may name a VM that has not
        /// been created yet, and the NIC editor must still accept free text for it.
        /// </summary>
        public IReadOnlyList<string> NicNamesFor(string vmName) =>
            NicNamesByVm.TryGetValue(vmName, out var nics) ? nics : [];
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

        ManagementScope scope;
        try
        {
            // No EnablePrivileges: this connection only ever reads. The mutating paths ask for
            // privileges because they need them; a picker must not.
            scope = new ManagementScope(Namespace, new ConnectionOptions());
            scope.Connect();
        }
        catch
        {
            // No Hyper-V host to talk to. The adapters are still real and still worth offering.
            return Snapshot.Empty with { Adapters = adapters };
        }

        var switches = ReadSwitchNames(scope);
        var vms      = ReadVmGuids(scope);
        var nics     = ReadNicNames(scope, vms);

        return new Snapshot(
            switches,
            [.. vms.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)],
            nics,
            adapters);
    }

    /// <summary>Every virtual switch on the host, by friendly name.</summary>
    private static IReadOnlyList<string> ReadSwitchNames(ManagementScope scope)
    {
        try
        {
            var names = new List<string>();
            using var s = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT ElementName FROM Msvm_VirtualEthernetSwitch"));
            // `using` on the COLLECTION too, not just the searcher and each object: Get() defaults to
            // Rewindable=true, so the returned collection holds its own IEnumWbemClassObject clone which
            // disposing the searcher does NOT release. See the Read() remarks for why that matters here.
            using var results = s.Get();
            foreach (ManagementObject o in results)
                using (o)
                {
                    var name = o["ElementName"] as string;
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
                }
            return names;
        }
        catch { return []; }
    }

    /// <summary>VM name → VM GUID, for the InstanceID matching every per-VM settings class needs.</summary>
    private static Dictionary<string, string> ReadVmGuids(ManagementScope scope)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT ElementName, Name FROM Msvm_ComputerSystem WHERE Caption='Virtual Machine'"));
            using var results = s.Get();   // Rewindable collection — its own enumerator to release
            foreach (ManagementObject o in results)
                using (o)
                {
                    var name = o["ElementName"] as string ?? "";
                    if (name.Length == 0) continue;
                    map[name] = o["Name"] as string ?? "";
                }
        }
        catch { /* leave the map as far as it got — a partial VM list still beats none */ }
        return map;
    }

    /// <summary>
    /// Every synthetic adapter name, grouped by owning VM. Unlike <c>VmService.ReadDiscovered</c> — which
    /// keeps ONE NIC per VM because the dashboard only needs the primary — this keeps them ALL: the whole
    /// point of the NIC editor is the VM with a second or renamed adapter, which is exactly the VM that
    /// silently never reconnects today.
    ///
    /// <para>A per-VM settings class embeds its owning VM's GUID in its InstanceID, so the association is
    /// a substring test — the same matching <c>VmService.MatchVm</c> does.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadNicNames(
        ManagementScope scope, Dictionary<string, string> vmGuids)
    {
        var byVm = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT InstanceID, ElementName FROM Msvm_SyntheticEthernetPortSettingData"));
            using var results = s.Get();   // Rewindable collection — its own enumerator to release
            foreach (ManagementObject o in results)
                using (o)
                {
                    var instanceId = o["InstanceID"] as string ?? "";
                    var nicName    = (o["ElementName"] as string)?.Trim();
                    if (string.IsNullOrEmpty(nicName)) continue;
                    if (MatchVm(instanceId, vmGuids) is not { } vmName) continue;

                    var list = byVm.TryGetValue(vmName, out var existing) ? existing : byVm[vmName] = [];
                    if (!list.Contains(nicName, StringComparer.OrdinalIgnoreCase)) list.Add(nicName);
                }
        }
        catch { /* partial is fine — see the class remarks */ }

        return byVm.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)[.. kv.Value.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)],
            StringComparer.OrdinalIgnoreCase);
    }

    private static string? MatchVm(string instanceId, Dictionary<string, string> vmGuids)
    {
        foreach (var (name, guid) in vmGuids)
            if (guid.Length > 0 && instanceId.Contains(guid, StringComparison.OrdinalIgnoreCase))
                return name;
        return null;
    }
}
