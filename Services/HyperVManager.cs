using System.Management;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Services;

/// <summary>
/// Safety-critical host-networking operations — connecting a VM NIC to a switch, re-homing an external
/// switch onto a physical adapter, and collapsing duplicate host (management-OS) vNICs — implemented on
/// native WMI (<c>System.Management</c> against <c>root\virtualization\v2</c>).
///
/// <para><b>PowerShell eliminated (issue #17).</b> This class previously drove three
/// <c>powershell.exe</c> cmdlet operations (<c>Connect-VMNetworkAdapter</c>, <c>Set-VMSwitch</c>) through
/// a persistent Base64 worker. It now talks to the Hyper-V WMI providers directly, the same way
/// <see cref="VmService"/> does for status/metrics/power — removing the ~80 MB idle worker, the 1-2 s
/// cold start, the Base64 stdin/stdout protocol, and the last non-.NET dependency. The public surface
/// (<see cref="ApplySwitchAsync"/>, <see cref="UpdateSwitchBindingAsync"/>,
/// <see cref="RepairHostVNicAsync"/>, the <see cref="HostVNicState"/> enum, and the SKIP / bound /
/// repaired / reshared semantics) is unchanged, so the rest of the app is unaffected.</para>
///
/// <para><b>Validated live (issue #17 close-out).</b> The WMI sequences below have been exercised
/// against a live Hyper-V host following <c>docs/wmi-switch-binding-test-protocol.md</c>: the VM
/// follows the network in both directions, and the <see cref="FindNicConnection"/> escaping-proof
/// correlation fix came out of that validation run. The retired PowerShell implementation is parked on
/// the <c>parked/main-pre-wmi</c> branch. See the protocol doc and DEVELOPMENT_NOTES.md for the
/// atomic-bind and duplicate-vNIC history these sequences preserve.</para>
///
/// <para><b>Model (Microsoft Hyper-V WMI v2).</b> A switch's connections are
/// <c>Msvm_EthernetPortAllocationSettingData</c> (EPASD) instances. An EPASD's <c>HostResource[0]</c>
/// identifies the endpoint: a <c>Msvm_ExternalEthernetPort</c>/<c>Msvm_WiFiPort</c> for the external
/// (physical NIC) uplink, or the host <c>Msvm_ComputerSystem</c> for the internal management-OS vNIC.
/// A VM NIC's connection is an EPASD whose <c>Parent</c> is the VM's
/// <c>Msvm_SyntheticEthernetPortSettingData</c> and whose <c>HostResource[0]</c> is the switch path.
/// VM-owned settings are changed through <c>Msvm_VirtualSystemManagementService</c>; switch-owned
/// settings through <c>Msvm_VirtualEthernetSwitchManagementService</c> (Add/Modify/RemoveResourceSettings).</para>
///
/// <para><b>Threading.</b> <c>System.Management</c> is MTA and these calls can block for tens of seconds;
/// every public method serialises on <see cref="_lock"/> and does its WMI work on the thread pool
/// (<c>Task.Run</c>), so the WinUI UI thread is never blocked — matching the single-flight invariant the
/// PowerShell worker had.</para>
/// </summary>
public sealed class HyperVManager : IDisposable
{
    private const string Namespace = @"root\virtualization\v2";

    // Re-homing an external switch can take tens of seconds; give async WMI jobs the same generous
    // budget the PowerShell BindTimeout had rather than the default.
    private static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(120);

    private readonly ILogger<HyperVManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);   // serialise concurrent host-network writes
    private readonly object _scopeLock = new();
    private ManagementScope? _scope;

    public HyperVManager(ILogger<HyperVManager> logger) => _logger = logger;

    // ── Public API (unchanged contract) ──────────────────────────────────────────

    /// <summary>
    /// Connects a VM's NIC to the given virtual switch, but only if it isn't already there. Re-applying
    /// an unchanged connection briefly bounces the VM's network, so the "already on that switch" case is
    /// a no-op (e.g. on every app launch, where the in-session guards start empty).
    ///
    /// <para><b>Returns true only when the NIC is confirmed to be on <paramref name="switchName"/></b> —
    /// whether this call moved it there or it was already there. False means the reconnect could not be
    /// performed (switch/VM/NIC not found, or the WMI modify failed) and the VM is NOT on that switch.
    /// Before issue #37 this returned <c>void</c> and swallowed every failure into a log line, so
    /// <see cref="NetworkMonitor"/> had no way to tell the UI that a reconnect had failed.</para>
    /// </summary>
    public Task<bool> ApplySwitchAsync(string vmName, string nicName, string switchName) =>
        WithLock(() => ApplySwitchCore(vmName, nicName, switchName));

    /// <summary>Performs the VM-NIC reconnect; true = the NIC is now on <paramref name="switchName"/>.</summary>
    private bool ApplySwitchCore(string vmName, string nicName, string switchName)
    {
        try
        {
            EnsureScope();
            var scope = _scope!;

            using var sw = FindSwitch(scope, switchName);
            if (sw is null) { _logger.LogError("ApplySwitchAsync: switch '{Switch}' not found", switchName); return false; }
            var switchPath = sw.Path.Path;
            var switchId   = sw["Name"] as string ?? "";   // switch GUID, embedded in a connection's HostResource path

            using var vmSettings = FindVmSettings(scope, vmName);
            if (vmSettings is null) { _logger.LogError("ApplySwitchAsync: VM '{Vm}' not found", vmName); return false; }

            using var nic = FindSyntheticNic(vmSettings, nicName);
            if (nic is null) { _logger.LogError("ApplySwitchAsync: NIC '{Nic}' on VM '{Vm}' not found", nicName, vmName); return false; }

            using var connection = FindNicConnection(scope, nic);

            // SKIP guard: the NIC's existing connection already points at this switch (re-applying an
            // unchanged connection briefly bounces the guest's network, so it must be a true no-op).
            if (connection is not null &&
                SwitchWmiHelpers.ConnectionTargetsSwitch(connection["HostResource"] as string[], switchId))
            {
                _logger.LogInformation("VM {Vm} already on '{Switch}' — no reconnect", vmName, switchName);
                return true;   // already where the caller wants it — a success, not a failure
            }

            if (connection is not null)
            {
                // Already connected somewhere else — re-point the existing allocation (Connect-VMNetworkAdapter).
                connection["HostResource"] = new[] { switchPath };
                ModifyVmResource(scope, connection);
            }
            else
            {
                // Never connected — add a fresh Ethernet Connection from the primordial template.
                using var template = DefaultEthernetConnectionTemplate(scope);
                template["Parent"]       = nic.Path.Path;
                template["HostResource"] = new[] { switchPath };
                AddVmResource(scope, vmSettings, template);
            }

            _logger.LogInformation("Switch applied: {Vm} → {Switch}", vmName, switchName);
            return true;
        }
        // AddVmResource/ModifyVmResource throw via CheckJob on a WMI failure — the reconnect did NOT
        // happen, so this must report false rather than let the caller assume success (issue #37).
        catch (Exception ex) { _logger.LogError(ex, "ApplySwitchAsync error"); return false; }
    }

    /// <summary>
    /// Binds a Hyper-V virtual switch to a physical NIC (makes it External, with the host sharing the
    /// adapter) — but only when it isn't already in that exact state.
    ///
    /// <para><b>Crash/kill safety (replicating the atomic <c>Set-VMSwitch</c>).</b> The re-home is a
    /// single <c>ModifyResourceSettings</c> on the switch's EXTERNAL port allocation (its
    /// <c>HostResource</c> is re-pointed at the new <c>Msvm_ExternalEthernetPort</c>). The INTERNAL
    /// (management-OS) port allocation is never removed or disabled, so — exactly like the single atomic
    /// <c>Set-VMSwitch -NetAdapterName … -AllowManagementOS $true</c> it replaces — there is no window in
    /// which a mid-sequence failure leaves the host adapter with no management vNIC and therefore no IP.
    /// A failed/partial modify leaves the pre-existing external+internal ports intact.</para>
    ///
    /// <para><b>No-op fast path.</b> If the switch is already External, sharing with the management OS,
    /// and bound to the target adapter, nothing is changed — stops host-network flicker on every launch.</para>
    ///
    /// <para>If the target adapter isn't present (e.g. the USB NIC is unplugged), the switch is left
    /// untouched. After a real rebind, <see cref="RepairHostVNicAsync"/> runs to collapse any duplicate
    /// host vNIC (kept as a safety net; the WMI re-home is not expected to create one).</para>
    /// </summary>
    public Task<SwitchBindOutcome> UpdateSwitchBindingAsync(string switchName, string adapterName) =>
        WithLock(() =>
        {
            var outcome = UpdateSwitchBindingCore(switchName, adapterName);
            // The vNIC-repair safety net only makes sense after a REAL rebind — an AlreadyBound no-op
            // touched nothing, and a Failed attempt must not be papered over as success.
            if (outcome == SwitchBindOutcome.Bound)
                RepairHostVNicCore(switchName);
            return outcome;
        });

    /// <summary>Performs the bind and reports its <see cref="SwitchBindOutcome"/> — only <see cref="SwitchBindOutcome.Bound"/> did real work.</summary>
    private SwitchBindOutcome UpdateSwitchBindingCore(string switchName, string adapterName)
    {
        try
        {
            EnsureScope();
            var scope = _scope!;

            // The caller passes the physical NIC's Windows connection alias (NetworkInterface.Name, what
            // Set-VMSwitch -NetAdapterName took). Map it to the adapter's MAC + description so we can find
            // the matching Msvm_ExternalEthernetPort (which has no notion of the Windows alias).
            var (mac, desc) = ResolveAdapter(adapterName);
            if (mac is null)
            {
                // Adapter genuinely absent (e.g. USB NIC unplugged). Treat as Failed so the caller leaves
                // its skip-cache clear and retries once the adapter reappears, rather than caching it as bound.
                _logger.LogInformation("Adapter '{Adapter}' not present — switch '{Switch}' left unchanged", adapterName, switchName);
                return SwitchBindOutcome.Failed;
            }

            using var sw = FindSwitch(scope, switchName);
            if (sw is null) { _logger.LogWarning("Virtual switch '{Switch}' not found — cannot bind", switchName); return SwitchBindOutcome.Failed; }
            using var settings = SwitchSettings(sw);
            if (settings is null) { _logger.LogWarning("Switch '{Switch}' has no settings data — cannot bind", switchName); return SwitchBindOutcome.Failed; }

            using var extPort = FindExternalPort(scope, mac, desc);
            if (extPort is null) { _logger.LogWarning("No external Ethernet port matches adapter '{Adapter}' — cannot bind '{Switch}'", adapterName, switchName); return SwitchBindOutcome.Failed; }
            var extPortPath = extPort.Path.Path;

            var ports = SwitchPorts(scope, sw);
            try
            {
                var external = ports.FirstOrDefault(p => p.Kind == SwitchWmiHelpers.PortKind.External);
                var host     = ports.FirstOrDefault(p => p.Kind == SwitchWmiHelpers.PortKind.Internal);

                // SKIP guard: External + management-OS sharing + already bound to this adapter.
                if (external.Epasd is not null && host.Epasd is not null &&
                    ExternalPortMatches(scope, external.Epasd, mac, desc))
                {
                    _logger.LogInformation("Switch '{Switch}' already bound to '{Adapter}' — no rebind", switchName, adapterName);
                    return SwitchBindOutcome.AlreadyBound;
                }

                if (external.Epasd is not null)
                {
                    // Re-point the existing external uplink in a single call. Management-OS port untouched.
                    external.Epasd["HostResource"] = new[] { extPortPath };
                    ModifySwitchResource(scope, external.Epasd);
                }
                else
                {
                    // Switch is currently Internal/Private — add an external uplink.
                    using var template = DefaultEthernetConnectionTemplate(scope);
                    template["HostResource"] = new[] { extPortPath };
                    AddSwitchResource(scope, settings, template);
                }

                // Ensure management-OS sharing exists (the "-AllowManagementOS $true" half). Pure add when
                // absent — never toggled off, so it cannot strand the host.
                if (host.Epasd is null)
                    AddInternalPort(scope, settings);

                _logger.LogInformation("Switch '{Switch}' bound to '{Adapter}'", switchName, adapterName);
                return SwitchBindOutcome.Bound;
            }
            finally { DisposePorts(ports); }
        }
        catch (Exception ex) { _logger.LogError(ex, "UpdateSwitchBindingAsync error"); return SwitchBindOutcome.Failed; }
    }

    /// <summary>Outcome of <see cref="RepairHostVNicAsync"/>.</summary>
    public enum HostVNicState { Ok, Repaired, Reshared, NoSwitch, Error }

    /// <summary>
    /// Ensures a switch has exactly ONE host (management-OS) vNIC, repairing the failure mode where the
    /// host loses its own network while the VM stays connected (typically after a dock undock/redock).
    ///
    /// <para>The WMI signal is the count of INTERNAL port allocations on the switch (each maps to a host
    /// vNIC — the equivalent of <c>Get-VMNetworkAdapter -ManagementOS -SwitchName</c>). If more than one
    /// exists, the EXTRA allocations are removed, keeping exactly one. Unlike the PowerShell
    /// <c>AllowManagementOS $false→$true</c> reset — which removed ALL host vNICs then re-added one,
    /// briefly leaving the host with zero — removing only the extras never drops below one, so the host
    /// keeps a management vNIC throughout (strictly safer). If the switch is External but sharing was
    /// left off (count 0), one internal port is added back. No-op when already healthy.</para>
    /// </summary>
    public async Task<HostVNicState> RepairHostVNicAsync(string switchName)
    {
        var state = HostVNicState.Ok;
        await WithLock(() => state = RepairHostVNicCore(switchName)).ConfigureAwait(false);
        return state;
    }

    private HostVNicState RepairHostVNicCore(string switchName)
    {
        try
        {
            EnsureScope();
            var scope = _scope!;

            using var sw = FindSwitch(scope, switchName);
            if (sw is null) return HostVNicState.NoSwitch;
            using var settings = SwitchSettings(sw);
            if (settings is null) return HostVNicState.NoSwitch;

            var ports = SwitchPorts(scope, sw);
            try
            {
                var hostPorts   = ports.Where(p => p.Kind == SwitchWmiHelpers.PortKind.Internal).ToList();
                bool isExternal = ports.Any(p => p.Kind == SwitchWmiHelpers.PortKind.External);

                switch (SwitchWmiHelpers.DecideHostVNicRepair(hostPorts.Count, isExternal))
                {
                    case SwitchWmiHelpers.HostVNicRepair.RemoveExtraInternalPorts:
                        // Remove down to exactly one — never to zero, so the host keeps a management vNIC
                        // (and therefore an IP) throughout the collapse.
                        foreach (var extra in hostPorts.Skip(1))
                            RemoveSwitchResource(scope, extra.Epasd!);
                        _logger.LogWarning("Collapsed duplicate host vNIC(s) on switch '{Switch}' to one (was {Count})", switchName, hostPorts.Count);
                        return HostVNicState.Repaired;

                    case SwitchWmiHelpers.HostVNicRepair.AddInternalPort:
                        AddInternalPort(scope, settings);
                        _logger.LogInformation("Restored host sharing on switch '{Switch}'", switchName);
                        return HostVNicState.Reshared;

                    default:
                        return HostVNicState.Ok;   // already healthy
                }
            }
            finally { DisposePorts(ports); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RepairHostVNicAsync('{Switch}') error", switchName);
            return HostVNicState.Error;
        }
    }

    // ── Serialisation ────────────────────────────────────────────────────────────

    /// <summary>Runs synchronous WMI work on the thread pool under the single-flight lock, so the UI
    /// thread never blocks and two host-network writes never overlap. The lock is non-reentrant, so
    /// <c>Core</c> helpers must be called WITHOUT it (public methods take it once).</summary>
    private async Task WithLock(Action wmiWork)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(wmiWork).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    /// <summary>Result-returning <see cref="WithLock(Action)"/> — same single-flight + off-UI-thread guarantees.</summary>
    private async Task<T> WithLock<T>(Func<T> wmiWork)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(wmiWork).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    // ── VM NIC lookups ───────────────────────────────────────────────────────────

    private static ManagementObject? FindVmSettings(ManagementScope scope, string vmName)
    {
        using var vm = FindVm(scope, vmName);
        if (vm is null) return null;
        // Msvm_SettingsDefineState associates a computer system to its single REALIZED (active) settings —
        // NOT any checkpoint's snapshot settings (those hang off Msvm_SnapshotOfVirtualSystem /
        // Msvm_MostCurrentSnapshotInBranch). So on a VM with checkpoints this still targets the running
        // config (U6, resolved from documentation).
        foreach (ManagementObject vssd in vm.GetRelated("Msvm_VirtualSystemSettingData",
                     "Msvm_SettingsDefineState", null, null, null, null, false, null))
            return vssd;
        return null;
    }

    private static ManagementObject? FindSyntheticNic(ManagementObject vmSettings, string nicName)
    {
        ManagementObject? onlyOne = null;
        int count = 0;
        foreach (ManagementObject sepsd in vmSettings.GetRelated("Msvm_SyntheticEthernetPortSettingData"))
        {
            count++;
            if (string.Equals(sepsd["ElementName"] as string, nicName, StringComparison.OrdinalIgnoreCase))
                return sepsd;
            // Remember a lone NIC as a lenient fallback for a config name mismatch, but dispose extras.
            if (onlyOne is null) onlyOne = sepsd; else sepsd.Dispose();
        }
        return count == 1 ? onlyOne : null;
    }

    /// <summary>Finds the VM NIC's existing Ethernet port allocation (its switch connection), or null if
    /// the NIC has never been connected. Correlated on the shared <c>&lt;vmguid&gt;\&lt;portguid&gt;</c>
    /// identity via <see cref="SwitchWmiHelpers.NicConnectionMatches"/> — a backslash-escaping-proof match
    /// that does NOT rely on a raw substring of the escaped <c>Parent</c> REF path (the issue #17 bug: a
    /// raw match missed the already-connected NIC and drove <see cref="ApplySwitchCore"/> into the failing
    /// "add a second Ethernet Connection" branch). A NIC connected to ANY switch is now found, so the
    /// caller re-points the existing allocation instead.</summary>
    private static ManagementObject? FindNicConnection(ManagementScope scope, ManagementObject nic)
    {
        var nicInstanceId = nic["InstanceID"] as string ?? "";
        if (nicInstanceId.Length == 0) return null;
        foreach (ManagementObject epasd in Query(scope, "SELECT * FROM Msvm_EthernetPortAllocationSettingData"))
        {
            if (SwitchWmiHelpers.NicConnectionMatches(
                    epasd["InstanceID"] as string, epasd["Parent"] as string, nicInstanceId))
                return epasd;
            epasd.Dispose();
        }
        return null;
    }

    // ── Switch lookups ───────────────────────────────────────────────────────────

    private static ManagementObject? FindSwitch(ManagementScope scope, string name)
    {
        ManagementObject? found = null;
        foreach (ManagementObject sw in Query(scope, "SELECT * FROM Msvm_VirtualEthernetSwitch"))
        {
            if (found is null && string.Equals(sw["ElementName"] as string, name, StringComparison.OrdinalIgnoreCase))
                found = sw;
            else sw.Dispose();
        }
        return found;
    }

    private static ManagementObject? SwitchSettings(ManagementObject sw)
    {
        foreach (ManagementObject ssd in sw.GetRelated("Msvm_VirtualEthernetSwitchSettingData",
                     "Msvm_SettingsDefineState", null, null, null, null, false, null))
            return ssd;
        return null;
    }

    /// <summary>A switch port and its connection setting data, classified. <c>Port</c>/<c>Epasd</c> are
    /// owned by the caller (dispose via <see cref="DisposePorts"/>).</summary>
    private readonly record struct SwitchPort(ManagementObject? Port, ManagementObject? Epasd, SwitchWmiHelpers.PortKind Kind);

    /// <summary>Enumerates a switch's ports and their Ethernet port allocations via the proven Microsoft
    /// traversal (switch → <c>Msvm_EthernetSwitchPort</c> via <c>Msvm_SystemDevice</c> → EPASD via
    /// <c>Msvm_ElementSettingData</c>), classifying each as external / internal / VM.</summary>
    private static List<SwitchPort> SwitchPorts(ManagementScope scope, ManagementObject sw)
    {
        var result = new List<SwitchPort>();
        foreach (ManagementObject port in sw.GetRelated("Msvm_EthernetSwitchPort",
                     "Msvm_SystemDevice", null, null, null, null, false, null))
        {
            ManagementObject? epasd = null;
            foreach (ManagementObject e in port.GetRelated("Msvm_EthernetPortAllocationSettingData",
                         "Msvm_ElementSettingData", null, null, null, null, false, null))
            { epasd = e; break; }

            if (epasd is null) { port.Dispose(); continue; }
            result.Add(new SwitchPort(port, epasd, ClassifyEpasd(epasd)));
        }
        return result;
    }

    private static SwitchWmiHelpers.PortKind ClassifyEpasd(ManagementObject epasd)
    {
        string? hostClass = null, parentClass = null;
        if (epasd["HostResource"] is string[] hr && hr.Length > 0 && !string.IsNullOrEmpty(hr[0]))
            hostClass = ClassNameOf(hr[0]);
        if (epasd["Parent"] is string parent && !string.IsNullOrEmpty(parent))
            parentClass = ClassNameOf(parent);
        return SwitchWmiHelpers.Classify(hostClass, parentClass);
    }

    private static void DisposePorts(List<SwitchPort> ports)
    {
        foreach (var p in ports) { p.Port?.Dispose(); p.Epasd?.Dispose(); }
    }

    // ── External-adapter resolution ──────────────────────────────────────────────

    /// <summary>Resolves a Windows connection alias to its (normalised MAC, description), or (null, null)
    /// if no such live adapter exists.</summary>
    private static (string? Mac, string? Desc) ResolveAdapter(string alias)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Name, alias, StringComparison.OrdinalIgnoreCase));
        if (nic is null) return (null, null);
        var mac = AdapterMatcher.NormalizeMac(nic.GetPhysicalAddress().ToString());
        return (mac.Length == 12 ? mac : null, nic.Description);
    }

    /// <summary>Finds the <c>Msvm_ExternalEthernetPort</c> for a physical adapter, preferring a MAC
    /// (<c>PermanentAddress</c>) match and falling back to the adapter description (<c>ElementName</c>).
    ///
    /// <para><b>Wi-Fi caveat (U5):</b> this queries only <c>Msvm_ExternalEthernetPort</c>. A wireless
    /// adapter surfaces as <c>Msvm_WiFiPort</c> instead and would not be found here — binding a switch onto
    /// Wi-Fi is out of scope for this path (and unusual for this app's docked-Ethernet use case).</para></summary>
    private static ManagementObject? FindExternalPort(ManagementScope scope, string mac, string? desc)
    {
        var candidates = Query(scope, "SELECT * FROM Msvm_ExternalEthernetPort").ToList();
        ManagementObject? byMac = null, byDesc = null;
        foreach (var p in candidates)
        {
            var portMac  = p["PermanentAddress"] as string;
            var portDesc = p["ElementName"] as string;
            // A MAC hit is authoritative; only accept a description hit if it's NOT also a MAC mismatch we
            // could distinguish — but MAC always wins, so track the two independently and prefer byMac.
            if (byMac is null && SwitchWmiHelpers.ExternalPortMatchesAdapter(portMac, null, mac, null))
                byMac = p;
            else if (byDesc is null && SwitchWmiHelpers.ExternalPortMatchesAdapter(null, portDesc, null, desc))
                byDesc = p;
        }
        var chosen = byMac ?? byDesc;
        foreach (var p in candidates) if (!ReferenceEquals(p, chosen)) p.Dispose();
        return chosen;
    }

    /// <summary>True when an external port allocation currently points at the given adapter (MAC or
    /// description). Dereferences the allocation's <c>HostResource</c> path to the live
    /// <c>Msvm_ExternalEthernetPort</c> and compares; a broken/stale path reads as "no match".</summary>
    private static bool ExternalPortMatches(ManagementScope scope, ManagementObject externalEpasd, string mac, string? desc)
    {
        if (externalEpasd["HostResource"] is not string[] hr || hr.Length == 0 || string.IsNullOrEmpty(hr[0]))
            return false;
        try
        {
            using var port = new ManagementObject(scope, new ManagementPath(hr[0]), null);
            port.Get();
            return SwitchWmiHelpers.ExternalPortMatchesAdapter(
                port["PermanentAddress"] as string, port["ElementName"] as string, mac, desc);
        }
        catch { return false; }
    }

    /// <summary>Adds a management-OS (internal) port allocation to a switch — <c>HostResource</c> is the
    /// host <c>Msvm_ComputerSystem</c>. This is the WMI equivalent of turning <c>AllowManagementOS</c> on.</summary>
    private void AddInternalPort(ManagementScope scope, ManagementObject switchSettings)
    {
        using var host = FindHostComputerSystem(scope);
        if (host is null) throw new InvalidOperationException("Host Msvm_ComputerSystem not found");
        using var template = DefaultEthernetConnectionTemplate(scope);
        template["HostResource"] = new[] { host.Path.Path };
        AddSwitchResource(scope, switchSettings, template);
    }

    // ── WMI plumbing ─────────────────────────────────────────────────────────────

    private void EnsureScope()
    {
        if (_scope is { IsConnected: true }) return;
        lock (_scopeLock)
        {
            if (_scope is { IsConnected: true }) return;
            var scope = new ManagementScope(Namespace, new ConnectionOptions { EnablePrivileges = true });
            scope.Connect();
            _scope = scope;
        }
    }

    /// <summary>Streams a WQL query's results. Each yielded <see cref="ManagementObject"/> is owned by the
    /// caller. (Objects outlive the searcher — the same pattern <see cref="VmService"/> relies on.)</summary>
    private static IEnumerable<ManagementObject> Query(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        foreach (ManagementObject o in searcher.Get())
            yield return o;
    }

    private static ManagementObject VmSystemService(ManagementScope scope) =>
        Query(scope, "SELECT * FROM Msvm_VirtualSystemManagementService").First();

    private static ManagementObject SwitchService(ManagementScope scope) =>
        Query(scope, "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService").First();

    private static ManagementObject? FindVm(ManagementScope scope, string name)
    {
        ManagementObject? found = null;
        foreach (ManagementObject vm in Query(scope, "SELECT * FROM Msvm_ComputerSystem WHERE Caption='Virtual Machine'"))
        {
            if (found is null && string.Equals(vm["ElementName"] as string, name, StringComparison.OrdinalIgnoreCase))
                found = vm;
            else vm.Dispose();
        }
        return found;
    }

    /// <summary>
    /// The host partition's own <c>Msvm_ComputerSystem</c> — the endpoint a management-OS (internal) port
    /// allocation must reference (U4).
    ///
    /// <para>The host is documented to carry <c>Caption = 'Hosting Computer System'</c> (VMs carry
    /// <c>'Virtual Machine'</c>), and exactly one such instance exists per host, so we match on that
    /// positively. As a defensive fallback (older/localised providers), we accept the single non-VM
    /// instance. Returns null only if neither yields a unique host, so <see cref="AddInternalPort"/> fails
    /// loudly rather than pointing a host vNIC at the wrong computer system.</para></summary>
    private static ManagementObject? FindHostComputerSystem(ManagementScope scope)
    {
        // Primary: the documented positive filter.
        var host = SingleOrNone(Query(scope, "SELECT * FROM Msvm_ComputerSystem WHERE Caption='Hosting Computer System'"));
        if (host is not null) return host;

        // Fallback: the single computer system that is not a Virtual Machine.
        return SingleOrNone(Query(scope, "SELECT * FROM Msvm_ComputerSystem WHERE Caption<>'Virtual Machine'"));
    }

    /// <summary>Returns the sole object of a query (disposing any others), or null if there are zero or more
    /// than one — an ambiguous host match must not be silently used.</summary>
    private static ManagementObject? SingleOrNone(IEnumerable<ManagementObject> objects)
    {
        ManagementObject? only = null;
        int count = 0;
        foreach (var o in objects)
        {
            count++;
            if (only is null) only = o; else o.Dispose();
        }
        if (count == 1) return only;
        only?.Dispose();
        return null;
    }

    /// <summary>Returns the default (primordial-pool) <c>Msvm_EthernetPortAllocationSettingData</c>
    /// template to clone-and-edit for a new connection, following the documented resource-pool traversal
    /// (pool → <c>Msvm_AllocationCapabilities</c> → the <c>Msvm_SettingsDefineCapabilities</c> whose
    /// <c>ValueRole</c> is 0 = Default).</summary>
    private static ManagementObject DefaultEthernetConnectionTemplate(ManagementScope scope)
    {
        using var pool = Query(scope,
            "SELECT * FROM Msvm_ResourcePool WHERE ResourceSubType='Microsoft:Hyper-V:Ethernet Connection' AND Primordial=True").First();

        foreach (ManagementObject caps in pool.GetRelated("Msvm_AllocationCapabilities",
                     "Msvm_ElementCapabilities", null, null, null, null, false, null))
        using (caps)
        {
            foreach (ManagementObject rel in caps.GetRelationships("Msvm_SettingsDefineCapabilities"))
            using (rel)
            {
                if (Convert.ToInt32(rel["ValueRole"]) != 0) continue;   // 0 = Default
                var partPath = rel["PartComponent"] as string;
                if (string.IsNullOrEmpty(partPath)) continue;
                return new ManagementObject(scope, new ManagementPath(partPath), null);
            }
        }
        throw new InvalidOperationException("Default Ethernet Connection setting-data template not found");
    }

    // Add/Modify on the VM management service (VM-owned port allocations).
    //
    // WMI contract (per Msvm_VirtualSystemManagementService docs, resolved from documentation):
    //  • AffectedConfiguration is a `CIM_VirtualSystemSettingData REF` — System.Management passes a REF
    //    parameter as the target's full object-path string, so `settings.Path.Path` is correct (U7).
    //  • ResourceSettings is a string[] of embedded instances rendered with WMI DTD 2.0; every Microsoft
    //    Hyper-V WMI sample uses `GetText(TextFormat.WmiDtd20)` for this provider (U2).
    private void AddVmResource(ManagementScope scope, ManagementObject vmSettings, ManagementObject settingData)
    {
        using var svc = VmSystemService(scope);
        using var inp = svc.GetMethodParameters("AddResourceSettings");
        inp["AffectedConfiguration"] = vmSettings.Path.Path;   // REF parameter → object-path string (U7)
        inp["ResourceSettings"]      = new[] { settingData.GetText(TextFormat.WmiDtd20) };  // U2
        using var outp = svc.InvokeMethod("AddResourceSettings", inp, null);
        CheckJob(scope, outp);
    }

    private void ModifyVmResource(ManagementScope scope, ManagementObject settingData)
    {
        using var svc = VmSystemService(scope);
        using var inp = svc.GetMethodParameters("ModifyResourceSettings");
        inp["ResourceSettings"] = new[] { settingData.GetText(TextFormat.WmiDtd20) };
        using var outp = svc.InvokeMethod("ModifyResourceSettings", inp, null);
        CheckJob(scope, outp);
    }

    // Add/Modify/Remove on the switch management service (switch-owned port allocations).
    private void AddSwitchResource(ManagementScope scope, ManagementObject switchSettings, ManagementObject settingData)
    {
        using var svc = SwitchService(scope);
        using var inp = svc.GetMethodParameters("AddResourceSettings");
        inp["AffectedConfiguration"] = switchSettings.Path.Path;
        inp["ResourceSettings"]      = new[] { settingData.GetText(TextFormat.WmiDtd20) };
        using var outp = svc.InvokeMethod("AddResourceSettings", inp, null);
        CheckJob(scope, outp);
    }

    private void ModifySwitchResource(ManagementScope scope, ManagementObject settingData)
    {
        using var svc = SwitchService(scope);
        using var inp = svc.GetMethodParameters("ModifyResourceSettings");
        inp["ResourceSettings"] = new[] { settingData.GetText(TextFormat.WmiDtd20) };
        using var outp = svc.InvokeMethod("ModifyResourceSettings", inp, null);
        CheckJob(scope, outp);
    }

    private void RemoveSwitchResource(ManagementScope scope, ManagementObject settingData)
    {
        using var svc = SwitchService(scope);
        using var inp = svc.GetMethodParameters("RemoveResourceSettings");
        // RemoveResourceSettings takes REFERENCES (object paths), not embedded instances.
        inp["ResourceSettings"] = new[] { settingData.Path.Path };
        using var outp = svc.InvokeMethod("RemoveResourceSettings", inp, null);
        CheckJob(scope, outp);
    }

    /// <summary>Interprets a Resource-/System-settings method result: 0 = done, 4096 = async job to wait
    /// on, anything else = failure. Throws on failure/timeout so the calling Core method logs and reports.</summary>
    private void CheckJob(ManagementScope scope, ManagementBaseObject outParams)
    {
        uint rv = Convert.ToUInt32(outParams["ReturnValue"]);
        if (rv == 0) return;
        if (rv == 4096) { WaitForJob(scope, outParams["Job"] as string); return; }
        throw new InvalidOperationException($"WMI resource method failed with 0x{rv:X}");
    }

    private static void WaitForJob(ManagementScope scope, string? jobPath)
    {
        if (string.IsNullOrEmpty(jobPath)) return;
        var deadline = DateTime.UtcNow + BindTimeout;
        using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);
        while (DateTime.UtcNow < deadline)
        {
            job.Get();
            ushort state = Convert.ToUInt16(job["JobState"]);
            if (state == 7) return;                          // Completed
            if (state >= 8)                                  // Terminated / Killed / Exception
                throw new InvalidOperationException(job["ErrorDescription"] as string ?? $"WMI job failed (state {state})");
            Thread.Sleep(200);
        }
        throw new TimeoutException($"WMI job did not complete within {BindTimeout.TotalSeconds:0} s");
    }

    /// <summary>Extracts the WMI class name from an object path (e.g. a <c>HostResource</c>/<c>Parent</c>
    /// string), tolerating a malformed path.</summary>
    private static string? ClassNameOf(string wmiObjectPath)
    {
        try { return new ManagementPath(wmiObjectPath).ClassName; } catch { return null; }
    }

    public void Dispose()
    {
        _lock.Dispose();
        _scope = null;
    }
}
