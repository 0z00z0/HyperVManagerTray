using System.Management;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;

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
    /// <para><b>Only <see cref="SwitchMoveOutcome.Failed"/> means the VM is NOT on
    /// <paramref name="sw"/></b> — the reconnect could not be performed (switch/VM/NIC not found, or the
    /// WMI modify failed). Before issue #37 this returned <c>void</c> and swallowed every failure into a
    /// log line, so <see cref="NetworkMonitor"/> had no way to tell the UI that a reconnect had failed.
    /// The two success cases are told apart because the caller acts on them differently: only a real
    /// <see cref="SwitchMoveOutcome.Moved"/> changed the network under the guest, and only that may lead
    /// to asking the guest for a new address (see <see cref="SwitchMoveOutcome"/>).</para>
    /// </summary>
    /// <param name="vm">The VM, found by its VM ID; the name is for the log.</param>
    /// <param name="nicId">The adapter's ID, or null for the VM's only adapter.</param>
    /// <param name="sw">The switch, found by its ID; the name is for the log.</param>
    public Task<SwitchMoveOutcome> ApplySwitchAsync(VmRef vm, string? nicId, SwitchRef sw) =>
        WithLock(() => ApplySwitchCore(vm, nicId, sw));

    /// <summary>
    /// Opens this manager's WMI connection ahead of the first caller that needs it, on the thread pool.
    /// Pure warm-up: it connects and nothing else — no query, no write, no lock. Idempotent, and safe to
    /// skip entirely (every entry point still calls <see cref="EnsureScope"/> for itself).
    ///
    /// <para><b>What this buys, precisely (issue #56 part 2).</b> The cold-start chain at logon is
    /// serialised: <c>AdapterMatcher.Evaluate</c> enumerates NICs for ~2 s, and only THEN does the apply
    /// pass reach <see cref="EnsureScope"/> and pay the cold <c>root\virtualization\v2</c> connect. The
    /// connect needs nothing the enumeration produces — it is a namespace constant — so the two can
    /// overlap, and the connect leaves the critical path entirely. This is the only serialisation in that
    /// chain that is ours: the ~2 s of NIC enumeration is Win32 IP Helper, the ~3–4 s after it is the WMI
    /// round-trips that CONFIRM the bind, and #37 is exactly the rule that those may not be skipped.</para>
    ///
    /// <para><b>Do not expect seconds.</b> <c>VmService.SubscribeStateWatcher</c> already connects to the
    /// same namespace on the thread pool moments earlier, so the expensive server-side half — spinning up
    /// the WmiPrvSE host and the vmms handshake — is very likely already paid by the time this runs. What
    /// is left for this to overlap is a DCOM connect against a warm provider, on a distinct connection
    /// (this one asks for <c>EnablePrivileges</c>; the watcher's does not, so the two cannot share). That
    /// is a real cost and it is on the critical path today, but it is hundreds of milliseconds at best,
    /// not the fix for #52's ~8 s. See the issue for why most of that 8 s is not ours to remove.</para>
    ///
    /// <para><b>Never throws.</b> On a host with no Hyper-V role the connect fails, exactly as it would
    /// have on the first real call, which handles it. A warm-up that could take the app down at launch
    /// would be a strictly worse trade than the milliseconds it saves.</para>
    /// </summary>
    public Task PrewarmAsync() => Task.Run(() =>
    {
        try { EnsureScope(); }
        catch (Exception ex)
        {
            // Debug, not Warning: on a non-Hyper-V host this is expected and says nothing the first real
            // call will not say louder. It is logged at all only so a "why was the first apply still
            // slow?" question has an answer.
            _logger.LogDebug(ex, "WMI pre-warm failed — the first real call will connect instead");
        }
    });

    /// <summary>Performs the VM-NIC reconnect and says which of the three things happened.</summary>
    private SwitchMoveOutcome ApplySwitchCore(VmRef vm, string? nicId, SwitchRef target)
    {
        var vmName     = $"{vm.Shown} ({vm.Id})";
        var switchName = $"{target.Shown} ({target.Id})";
        try
        {
            EnsureScope();
            var scope = _scope!;

            using var sw = FindSwitch(scope, target.Id);
            if (sw is null) { _logger.LogError("ApplySwitchAsync: switch '{Switch}' not found", switchName); return SwitchMoveOutcome.Failed; }
            var switchPath = sw.Path.Path;
            var switchId   = sw["Name"] as string ?? "";   // switch GUID, embedded in a connection's HostResource path

            using var vmSettings = FindVmSettings(scope, vm.Id);
            if (vmSettings is null) { _logger.LogError("ApplySwitchAsync: VM '{Vm}' not found", vmName); return SwitchMoveOutcome.Failed; }

            using var nic = FindSyntheticNic(vmSettings, nicId);
            if (nic is null)
            {
                _logger.LogError("ApplySwitchAsync: network adapter '{Nic}' on VM '{Vm}' not found", nicId ?? "(the only adapter)", vmName);
                return SwitchMoveOutcome.Failed;
            }

            using var connection = FindNicConnection(scope, nic);

            // SKIP guard: the NIC's existing connection already points at this switch (re-applying an
            // unchanged connection briefly bounces the guest's network, so it must be a true no-op).
            if (connection is not null &&
                SwitchWmiHelpers.ConnectionTargetsSwitch(connection["HostResource"] as string[], switchId))
            {
                _logger.LogInformation("VM {Vm} already on '{Switch}' — no reconnect", vmName, switchName);
                return SwitchMoveOutcome.AlreadyThere;   // already where the caller wants it — a success, not a failure
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
            return SwitchMoveOutcome.Moved;
        }
        // AddVmResource/ModifyVmResource throw via CheckJob on a WMI failure — the reconnect did NOT
        // happen, so this must report a failure rather than let the caller assume success (issue #37).
        catch (Exception ex) { _logger.LogError(ex, "ApplySwitchAsync error"); return SwitchMoveOutcome.Failed; }
    }

    /// <summary>
    /// Takes a VM's network link down and brings it straight back up, so the guest's address client sees
    /// the medium disappear and return and asks for a new address. Nothing is run inside the guest and no
    /// guest credentials are needed: the host disconnects the adapter's existing allocation from its
    /// switch and reconnects it to the same switch.
    ///
    /// <para><b>Why disconnect-and-reconnect rather than disabling the port.</b> Both produce a link
    /// cycle, and they fail differently. A cycle that disconnects clears the allocation's
    /// <c>HostResource</c>, so if the process dies between the two halves the adapter is left pointing at
    /// no switch — which the next evaluation repairs by itself, because
    /// <see cref="ApplySwitchCore"/>'s skip guard sees a connection that does not target the switch and
    /// re-points it. Disabling the port instead leaves <c>HostResource</c> untouched, so that same guard
    /// would skip the adapter and a half-finished cycle would strand the guest with no network until
    /// somebody noticed. The recoverable failure is the one to choose.</para>
    ///
    /// <para><b>Never throws, and answers only for what it confirmed.</b> False means the link was not
    /// cycled — no adapter, no existing connection, or the modify was refused — and the caller must not
    /// then wait for an address that was never going to change. A host that refuses to clear
    /// <c>HostResource</c> at all fails on the first half, before anything has been changed.</para>
    ///
    /// <para><b>Not verified against a running machine.</b> Whether a guest's address client treats this
    /// as grounds to ask for a new address, rather than resuming its existing lease, is only visible from
    /// inside the guest.</para>
    /// </summary>
    /// <param name="vm">The VM, found by its VM ID; the name is for the log.</param>
    /// <param name="nicId">The adapter's ID, or null for the VM's only adapter.</param>
    public Task<bool> CycleNicLinkAsync(VmRef vm, string? nicId) =>
        WithLock(() => CycleNicLinkCore(vm, nicId));

    /// <summary>How long the link is held down. Long enough for the guest's driver to report the medium
    /// gone rather than filter out a change that came and went inside one poll.</summary>
    private static readonly TimeSpan LinkDownHold = TimeSpan.FromSeconds(3);

    private bool CycleNicLinkCore(VmRef vm, string? nicId)
    {
        var vmName = $"{vm.Shown} ({vm.Id})";
        try
        {
            EnsureScope();
            var scope = _scope!;

            using var vmSettings = FindVmSettings(scope, vm.Id);
            if (vmSettings is null) { _logger.LogWarning("Link cycle: VM '{Vm}' not found", vmName); return false; }

            using var nic = FindSyntheticNic(vmSettings, nicId);
            if (nic is null)
            {
                _logger.LogWarning("Link cycle: network adapter '{Nic}' on VM '{Vm}' not found", nicId ?? "(the only adapter)", vmName);
                return false;
            }

            using var connection = FindNicConnection(scope, nic);
            if (connection is null)
            {
                _logger.LogWarning("Link cycle: VM '{Vm}' has no switch connection to cycle", vmName);
                return false;
            }

            // The switch to come back to, captured before anything is changed: the reconnect must restore
            // exactly what was there, never whatever the rules would choose a moment later.
            if (connection["HostResource"] is not string[] hostResource || hostResource.Length == 0)
            {
                _logger.LogWarning("Link cycle: VM '{Vm}' connection names no switch", vmName);
                return false;
            }

            connection["HostResource"] = Array.Empty<string>();
            ModifyVmResource(scope, connection);

            try { Thread.Sleep(LinkDownHold); }
            finally
            {
                connection["HostResource"] = hostResource;
                ModifyVmResource(scope, connection);
            }

            _logger.LogInformation("Link cycled: {Vm}", vmName);
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "Link cycle error on VM '{Vm}'", vmName); return false; }
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
    /// <param name="target">The switch, found by its ID.</param>
    /// <param name="adapterInterfaceId">The physical adapter's interface GUID (<c>NetworkInterface.Id</c>).</param>
    public Task<SwitchBindOutcome> UpdateSwitchBindingAsync(SwitchRef target, string adapterInterfaceId) =>
        WithLock(() =>
        {
            var outcome = UpdateSwitchBindingCore(target, adapterInterfaceId);
            // The vNIC-repair safety net only makes sense after a REAL rebind — an AlreadyBound no-op
            // touched nothing, and a Failed attempt must not be papered over as success.
            if (outcome == SwitchBindOutcome.Bound)
                RepairHostVNicCore(target);
            return outcome;
        });

    /// <summary>Performs the bind and reports its <see cref="SwitchBindOutcome"/> — only <see cref="SwitchBindOutcome.Bound"/> did real work.</summary>
    private SwitchBindOutcome UpdateSwitchBindingCore(SwitchRef target, string adapterInterfaceId)
    {
        var switchName  = $"{target.Shown} ({target.Id})";
        var adapterName = adapterInterfaceId;
        try
        {
            EnsureScope();
            var scope = _scope!;

            // The caller passes the physical NIC's interface GUID (NetworkInterface.Id), which neither a
            // rename nor a new connection alias changes. Map it to the adapter's MAC so we can find the
            // matching wired or Wi-Fi port, which has no notion of the Windows alias.
            var (mac, desc) = ResolveAdapter(adapterInterfaceId);
            if (mac is null)
            {
                // Adapter genuinely absent (e.g. USB NIC unplugged). Treat as Failed so the caller leaves
                // its skip-cache clear and retries once the adapter reappears, rather than caching it as bound.
                _logger.LogInformation("Adapter '{Adapter}' not present — switch '{Switch}' left unchanged", adapterName, switchName);
                return SwitchBindOutcome.Failed;
            }

            using var sw = FindSwitch(scope, target.Id);
            if (sw is null) { _logger.LogWarning("Virtual switch '{Switch}' not found — cannot bind", switchName); return SwitchBindOutcome.Failed; }
            using var settings = SwitchSettings(sw);
            if (settings is null) { _logger.LogWarning("Switch '{Switch}' has no settings data — cannot bind", switchName); return SwitchBindOutcome.Failed; }

            using var extPort = FindExternalPort(scope, mac, desc);
            if (extPort is null) { _logger.LogWarning("No Hyper-V wired or Wi-Fi port matches adapter '{Adapter}' — cannot bind '{Switch}'", adapterName, switchName); return SwitchBindOutcome.Failed; }
            _logger.LogInformation("Binding '{Switch}' to adapter '{Adapter}' through {PortClass}", switchName, adapterName, extPort.ClassPath.ClassName);
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
    public async Task<HostVNicState> RepairHostVNicAsync(SwitchRef target)
    {
        var state = HostVNicState.Ok;
        await WithLock(() => state = RepairHostVNicCore(target)).ConfigureAwait(false);
        return state;
    }

    private HostVNicState RepairHostVNicCore(SwitchRef target)
    {
        var switchName = $"{target.Shown} ({target.Id})";
        try
        {
            EnsureScope();
            var scope = _scope!;

            using var sw = FindSwitch(scope, target.Id);
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

    private static ManagementObject? FindVmSettings(ManagementScope scope, string vmId)
    {
        using var vm = FindVm(scope, vmId);
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

    /// <summary>
    /// The VM's adapter with <paramref name="nicId"/>, or — when no ID is stored — the VM's only adapter.
    /// A VM with several adapters and no stored ID gets none: which one is meant cannot be known.
    /// </summary>
    private static ManagementObject? FindSyntheticNic(ManagementObject vmSettings, string? nicId)
    {
        ManagementObject? onlyOne = null;
        int count = 0;
        foreach (ManagementObject sepsd in vmSettings.GetRelated("Msvm_SyntheticEthernetPortSettingData"))
        {
            count++;
            if (nicId is not null)
            {
                if (HostIdentity.Same(HostIdentity.NicIdFromInstanceId(sepsd["InstanceID"] as string), nicId))
                {
                    onlyOne?.Dispose();
                    return sepsd;
                }
                sepsd.Dispose();
                continue;
            }
            if (onlyOne is null) onlyOne = sepsd; else sepsd.Dispose();
        }
        if (nicId is not null) return null;
        if (count == 1) return onlyOne;
        onlyOne?.Dispose();
        return null;
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

    /// <summary>The switch with this switch ID (<c>Msvm_VirtualEthernetSwitch.Name</c>), or null.</summary>
    private static ManagementObject? FindSwitch(ManagementScope scope, string switchId)
    {
        ManagementObject? found = null;
        foreach (ManagementObject sw in Query(scope, "SELECT * FROM Msvm_VirtualEthernetSwitch"))
        {
            if (found is null && HostIdentity.Same(sw["Name"] as string, switchId))
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

    /// <summary>Resolves an adapter's interface GUID to its (normalised MAC, interface GUID), or
    /// (null, null) if no such live adapter exists.</summary>
    private static (string? Mac, string? Guid) ResolveAdapter(string interfaceId)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => HostIdentity.Same(n.Id, interfaceId));
        if (nic is null) return (null, null);
        var mac = AdapterMatcher.NormalizeMac(nic.GetPhysicalAddress().ToString());
        return (mac.Length == 12 ? mac : null, HostIdentity.Bare(nic.Id));
    }

    /// <summary>Every port Hyper-V can bind an external switch to: wired and Wi-Fi.</summary>
    private List<ManagementObject> UplinkPortCandidates(ManagementScope scope)
    {
        var candidates = Query(scope, "SELECT * FROM Msvm_ExternalEthernetPort").ToList();
        // A host without Wi-Fi support in Hyper-V may not serve the class; wired binding must not fail for that.
        try { candidates.AddRange(Query(scope, "SELECT * FROM Msvm_WiFiPort")); }
        catch (Exception ex) { _logger.LogDebug(ex, "Msvm_WiFiPort query failed — searching wired ports only"); }
        return candidates;
    }

    /// <summary>Finds the Hyper-V port for a physical adapter — a wired <c>Msvm_ExternalEthernetPort</c> or a
    /// <c>Msvm_WiFiPort</c> — preferring a MAC (<c>PermanentAddress</c>) match and falling back to the
    /// adapter's interface GUID in <c>DeviceID</c>. Both classes carry the same two properties, so one
    /// matcher serves wired and Wi-Fi adapters alike.
    ///
    /// <para>On Wi-Fi, Hyper-V puts a single-adapter Microsoft bridge between the switch and the adapter
    /// that rewrites each VM's hardware address to the adapter's own. Microsoft documents that bridge for
    /// switch creation; whether re-pointing an existing switch's uplink creates it the same way has not
    /// been measured on a host.</para></summary>
    private ManagementObject? FindExternalPort(ManagementScope scope, string mac, string? desc)
    {
        var candidates = UplinkPortCandidates(scope);
        ManagementObject? byMac = null, byDesc = null;
        foreach (var p in candidates)
        {
            var portMac  = p["PermanentAddress"] as string;
            var portDesc = p["DeviceID"] as string;
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
    /// interface GUID). Dereferences the allocation's <c>HostResource</c> path to the live
    /// <c>Msvm_ExternalEthernetPort</c> and compares; a broken/stale path reads as "no match".</summary>
    private static bool ExternalPortMatches(ManagementScope scope, ManagementObject externalEpasd, string mac, string? desc)
    {
        if (externalEpasd["HostResource"] is not string[] hr || hr.Length == 0 || string.IsNullOrEmpty(hr[0]))
            return false;
        try
        {
            using var port = new ManagementObject(scope, new ManagementPath(hr[0]), WmiLimits.Get());
            port.Get();
            return SwitchWmiHelpers.ExternalPortMatchesAdapter(
                port["PermanentAddress"] as string, port["DeviceID"] as string, mac, desc);
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
            WmiLimits.ConnectBounded(scope);   // a stopped or stopping vmms must fail the pass, not hang it
            _scope = scope;
        }
    }

    /// <summary>Streams a WQL query's results. Each yielded <see cref="ManagementObject"/> is owned by the
    /// caller. (Objects outlive the searcher — the same pattern <see cref="VmService"/> relies on.)</summary>
    private static IEnumerable<ManagementObject> Query(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), WmiLimits.Enumeration());
        foreach (ManagementObject o in searcher.Get())
            yield return o;
    }

    private static ManagementObject VmSystemService(ManagementScope scope) =>
        Query(scope, "SELECT * FROM Msvm_VirtualSystemManagementService").First();

    private static ManagementObject SwitchService(ManagementScope scope) =>
        Query(scope, "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService").First();

    /// <summary>The VM with this VM ID (<c>Msvm_ComputerSystem.Name</c>), or null.</summary>
    private static ManagementObject? FindVm(ManagementScope scope, string vmId)
    {
        ManagementObject? found = null;
        foreach (ManagementObject vm in Query(scope, "SELECT * FROM Msvm_ComputerSystem WHERE Caption='Virtual Machine'"))
        {
            if (found is null && HostIdentity.Same(vm["Name"] as string, vmId))
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
                return new ManagementObject(scope, new ManagementPath(partPath), WmiLimits.Get());
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
        using var outp = svc.InvokeMethod("AddResourceSettings", inp, WmiLimits.Method());
        CheckJob(scope, outp);
    }

    private void ModifyVmResource(ManagementScope scope, ManagementObject settingData)
    {
        using var svc = VmSystemService(scope);
        using var inp = svc.GetMethodParameters("ModifyResourceSettings");
        inp["ResourceSettings"] = new[] { settingData.GetText(TextFormat.WmiDtd20) };
        using var outp = svc.InvokeMethod("ModifyResourceSettings", inp, WmiLimits.Method());
        CheckJob(scope, outp);
    }

    // Add/Modify/Remove on the switch management service (switch-owned port allocations).
    private void AddSwitchResource(ManagementScope scope, ManagementObject switchSettings, ManagementObject settingData)
    {
        using var svc = SwitchService(scope);
        using var inp = svc.GetMethodParameters("AddResourceSettings");
        inp["AffectedConfiguration"] = switchSettings.Path.Path;
        inp["ResourceSettings"]      = new[] { settingData.GetText(TextFormat.WmiDtd20) };
        using var outp = svc.InvokeMethod("AddResourceSettings", inp, WmiLimits.Method());
        CheckJob(scope, outp);
    }

    private void ModifySwitchResource(ManagementScope scope, ManagementObject settingData)
    {
        using var svc = SwitchService(scope);
        using var inp = svc.GetMethodParameters("ModifyResourceSettings");
        inp["ResourceSettings"] = new[] { settingData.GetText(TextFormat.WmiDtd20) };
        using var outp = svc.InvokeMethod("ModifyResourceSettings", inp, WmiLimits.Method());
        CheckJob(scope, outp);
    }

    private void RemoveSwitchResource(ManagementScope scope, ManagementObject settingData)
    {
        using var svc = SwitchService(scope);
        using var inp = svc.GetMethodParameters("RemoveResourceSettings");
        // RemoveResourceSettings takes REFERENCES (object paths), not embedded instances.
        inp["ResourceSettings"] = new[] { settingData.Path.Path };
        using var outp = svc.InvokeMethod("RemoveResourceSettings", inp, WmiLimits.Method());
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
        using var job = new ManagementObject(scope, new ManagementPath(jobPath), WmiLimits.Get());
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
