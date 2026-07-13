using System.Management;
using Microsoft.Extensions.Logging;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;

namespace HyperVManagerTray.Services;

/// <summary>
/// Native WMI (<c>root\virtualization\v2</c>) replacement for the per-second PowerShell polling of
/// VM state/metrics/IPs and for the VM power actions. This is what Hyper-V Manager (MMC) itself uses:
/// push state changes via a WMI event watcher, batch metrics via
/// <c>Msvm_VirtualSystemManagementService.GetSummaryInformation</c>, and async power actions tracked
/// through <c>Msvm_ConcreteJob</c> (live percent + the exact failure text).
///
/// THREADING: System.Management is MTA; the WinUI UI thread is STA. All WMI work here runs on the
/// thread pool (MTA). The UI never calls into WMI directly — it subscribes to <see cref="StatusesChanged"/>
/// and <see cref="OperationProgress"/> and marshals to its dispatcher. Nothing blocks the UI thread.
///
/// Phase 1 scope: status, metrics, power, guest IPs. Switch binding / host-vNIC repair stay on the
/// PowerShell worker in <see cref="HyperVManager"/> (Phase 2).
/// </summary>
public sealed class VmService : IDisposable
{
    private const string Namespace = @"root\virtualization\v2";

    // Two DISTINCT Hyper-V WMI state enumerations that must not be conflated (doing so returned
    // 32775 / 0x8007 "Invalid state for this operation" on Pause and Save):
    //   • EnabledState   — the CURRENT power state read back from Msvm_ComputerSystem. The
    //     already-in-state no-op guard maps it to a friendly name via WmiVmMapper.MapState (the
    //     single source of truth), so BOTH the user-pause code 9 and the vendor 32768 count as
    //     "Paused" — comparing raw codes here would miss the 9 case and re-issue a spurious Pause.
    //   • RequestedState — the target code RequestStateChange ACCEPTS to drive a transition.
    // Msvm_ComputerSystem.RequestStateChange RequestedState (target request). These are the STANDARD
    // CIM values a V2 host accepts; the vendor codes the docs list for save/pause/resume (32773/32776/
    // 32777) are "Hyper-V V1 only" and a V2 host rejects them with 32775 — which is why the earlier
    // 32773/32779 Save guesses failed. Verified against Microsoft's fsharplu ManagementHypervisor.fs
    // (Pause = Quiesce 9, Save = Offline 6); Pause 9 is confirmed working live on this host.
    // https://learn.microsoft.com/windows/win32/hyperv_v2/requeststatechange-msvm-computersystem
    private const ushort ReqEnabled = 2;   // Start / Resume → Running
    private const ushort ReqQuiesce = 9;   // Pause          → Paused (EnabledState 9)
    private const ushort ReqOffline = 6;   // Save           → Saved  (suspends to disk → EnabledState 32769)

    private static readonly TimeSpan MetricsInterval = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan VhdInterval     = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SwitchInterval  = TimeSpan.FromSeconds(10);

    /// <summary>Identity of a VM as seen through <c>Msvm_ComputerSystem</c>: its live power state and
    /// the GUID used to correlate it against every other Msvm_* class (settings, storage, network).</summary>
    private readonly record struct VmIdentity(ushort EnabledState, string Guid);

    private readonly ILogger<VmService> _logger;
    private readonly object _scopeLock = new();
    private ManagementScope? _scope;
    private ManagementEventWatcher? _watcher;

    // RefreshCore has THREE independent entry points (the metrics PeriodicTimer, the event
    // watcher via TriggerRefresh, and RefreshOnceAsync) that can fire concurrently on the thread
    // pool. It mutates plain (non-concurrent) Dictionary caches, so every call must be serialised —
    // _refreshing below only coalesces TriggerRefresh's own callers, it does not protect against a
    // periodic tick or a manual RefreshOnceAsync running at the same time.
    private readonly object _refreshLock = new();

    // Metrics loop (only runs while the dashboard is subscribed).
    private int _subscribers;
    private CancellationTokenSource? _metricsCts;
    private int _refreshing;   // 0/1 coalescing guard (Interlocked)

    // Caches (whole-object replacement on refresh → readers see a consistent snapshot).
    private volatile IReadOnlyDictionary<string, string> _vmIps =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private volatile List<DiscoveredVm>? _discovered;
    private readonly Dictionary<string, long> _memMax = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Bytes, DateTime At)> _vhd = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _switchByVm = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _switchCacheAt = DateTime.MinValue;

    // Debounce flags so a persistently degraded WMI read warns once, not once per ~2.5s tick.
    private bool _summariesDegradedWarned;
    private bool _switchNamesEmptyWarned;

    /// <summary>Raised (on a background thread) whenever VM status/metrics change. Marshal to the UI.</summary>
    public event Action<IReadOnlyList<VmStatus>>? StatusesChanged;

    /// <summary>Raised (on a background thread) as a power action progresses. Marshal to the UI.</summary>
    public event Action<VmOperationProgress>? OperationProgress;

    public VmService(ILogger<VmService> logger) => _logger = logger;

    // ── Sync cache reads (UI-thread safe, no WMI) ────────────────────────────────

    public string? GetCachedVmIp(string vmName) =>
        _vmIps.TryGetValue(vmName, out var ip) ? ip : null;

    public List<DiscoveredVm>? GetCachedVmsSync() => _discovered;

    // ── Metrics subscription (dashboard open/close) ──────────────────────────────

    /// <summary>Call when the dashboard opens. Starts event push + the metrics loop (ref-counted).</summary>
    public void SubscribeMetrics()
    {
        if (Interlocked.Increment(ref _subscribers) != 1) return;
        StartWatcher();
        _metricsCts = new CancellationTokenSource();
        _ = MetricsLoopAsync(_metricsCts.Token);
        TriggerRefresh();   // immediate first tick
    }

    /// <summary>Call when the dashboard hides. Stops the metrics loop + watcher when no subscribers remain.</summary>
    public void UnsubscribeMetrics()
    {
        if (Interlocked.Decrement(ref _subscribers) > 0) return;
        try { _metricsCts?.Cancel(); _metricsCts?.Dispose(); } catch { }
        _metricsCts = null;
        StopWatcher();
    }

    /// <summary>One-shot refresh (e.g. startup pre-warm), regardless of subscription. Never throws.</summary>
    public Task RefreshOnceAsync() => Task.Run(RefreshCore);

    /// <summary>
    /// Waits until <paramref name="vmName"/> reports the "Running" state (mapped via
    /// <see cref="WmiVmMapper.MapState"/>), or <paramref name="timeout"/> elapses — whichever comes
    /// first. Event-driven, not a new polling loop: <see cref="SubscribeMetrics"/> (ref-counted, safe
    /// to call even when the dashboard already has its own subscription active) guarantees the WMI
    /// event watcher and metrics timer are actively pushing <see cref="StatusesChanged"/> updates for
    /// the duration of the wait, so a real state change is picked up near-instantly. Subscribing our
    /// handler first and then forcing one <see cref="RefreshOnceAsync"/> means a VM that's already
    /// Running by the time this is called resolves immediately, instead of waiting on a change event
    /// that might never come. Never throws; returns <c>false</c> on timeout so callers (e.g. Start
    /// &amp; Connect) can proceed anyway — vmconnect.exe tolerates attaching to a VM that's still
    /// finishing boot. Genuinely async (no blocking waits) — safe to await from the UI thread.
    /// </summary>
    public async Task<bool> WaitUntilRunningAsync(string vmName, TimeSpan timeout)
    {
        SubscribeMetrics();
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnStatuses(IReadOnlyList<VmStatus> statuses)
            {
                var s = statuses.FirstOrDefault(x => x.Name.Equals(vmName, StringComparison.OrdinalIgnoreCase));
                if (s?.IsRunning == true) tcs.TrySetResult(true);
            }

            StatusesChanged += OnStatuses;
            try
            {
                // Forces one immediate push through OnStatuses above, so an already-Running VM
                // resolves the TCS right away rather than waiting for a future change event.
                await RefreshOnceAsync().ConfigureAwait(false);

                var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
                return winner == tcs.Task;
            }
            finally
            {
                StatusesChanged -= OnStatuses;
            }
        }
        finally
        {
            UnsubscribeMetrics();
        }
    }

    private async Task MetricsLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(MetricsInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                RefreshCore();
        }
        catch (OperationCanceledException) { /* unsubscribed */ }
        catch (Exception ex) { _logger.LogWarning(ex, "VM metrics loop stopped"); }
    }

    // ── Event watcher (instant state changes) ────────────────────────────────────

    private void StartWatcher()
    {
        try
        {
            EnsureScope();
            _watcher = new ManagementEventWatcher(_scope,
                new EventQuery("SELECT * FROM __InstanceModificationEvent WITHIN 2 " +
                               "WHERE TargetInstance ISA 'Msvm_ComputerSystem'"));
            _watcher.EventArrived += (_, _) => TriggerRefresh();
            _watcher.Start();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not start VM state watcher (metrics timer still covers it)"); }
    }

    private void StopWatcher()
    {
        try { _watcher?.Stop(); _watcher?.Dispose(); } catch { }
        _watcher = null;
    }

    /// <summary>Coalesced background refresh — skips if one is already running.</summary>
    private void TriggerRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { RefreshCore(); }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    // ── Core read path (MTA / thread pool) ───────────────────────────────────────

    private void RefreshCore()
    {
        lock (_refreshLock)
        {
            try
            {
                EnsureScope();
                var scope = _scope!;

                // State is authoritative from Msvm_ComputerSystem.EnabledState (well-documented);
                // metrics come from GetSummaryInformation (best-effort — degrades to zeros if codes differ).
                var vms       = ReadComputerSystems(scope);
                var summaries = ReadSummaries(scope);
                RefreshMemMax(scope, vms);

                // VHD size and switch binding change rarely; both are refreshed on their own slower
                // cadence to avoid paying their extra WMI round-trips on every metrics tick.
                if (_vhd.Count == 0 || (DateTime.UtcNow - _vhd.Values.Max(v => v.At)) > VhdInterval)
                    RefreshVhd(scope, vms);
                if (DateTime.UtcNow - _switchCacheAt > SwitchInterval)
                {
                    foreach (var (name, sw) in ReadSwitchNames(scope, vms)) _switchByVm[name] = sw;
                    _switchCacheAt = DateTime.UtcNow;
                }

                var list = new List<VmStatus>(vms.Count);
                foreach (var (name, id) in vms)
                {
                    summaries.TryGetValue(name, out var m);
                    _memMax.TryGetValue(name, out var memMax);
                    _switchByVm.TryGetValue(name, out var switchName);
                    var st = WmiVmMapper.BuildStatus(
                        name, id.EnabledState, m.Cpu, m.MemMb, m.UptimeMs, m.StatusDesc, memMax, switchName ?? "");
                    if (_vhd.TryGetValue(name, out var v)) st.VhdBytes = v.Bytes;
                    list.Add(st);
                }

                _discovered = ReadDiscovered(scope, vms);
                _vmIps      = ReadIps(scope, vms);

                StatusesChanged?.Invoke(list);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VM WMI refresh failed");
            }
        }
    }

    private Dictionary<string, VmIdentity> ReadComputerSystems(ManagementScope scope)
    {
        var map = new Dictionary<string, VmIdentity>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT ElementName, EnabledState, Name FROM Msvm_ComputerSystem WHERE Caption='Virtual Machine'"));
        foreach (ManagementObject vm in searcher.Get())
            using (vm)
            {
                var name = vm["ElementName"] as string ?? "";
                if (name.Length == 0) continue;
                map[name] = new VmIdentity(Convert.ToUInt16(vm["EnabledState"]), vm["Name"] as string ?? "");
            }
        return map;
    }

    /// <summary>
    /// Looks up which VM a settings-class InstanceID belongs to. Per-VM child settings (memory,
    /// storage, network port allocation, …) embed the owning VM's GUID inside their InstanceID, so
    /// matching is a substring test against each VM's GUID — the same pattern every read below needs.
    /// </summary>
    private static string? MatchVm(string instanceId, Dictionary<string, VmIdentity> vms)
    {
        foreach (var (name, id) in vms)
            if (id.Guid.Length > 0 && instanceId.Contains(id.Guid, StringComparison.OrdinalIgnoreCase))
                return name;
        return null;
    }

    private Dictionary<string, (int Cpu, long MemMb, ulong UptimeMs, string StatusDesc)> ReadSummaries(ManagementScope scope)
    {
        var result = new Dictionary<string, (int, long, ulong, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // SettingData = empty array asks for every VM's summary in one batch call — a prior
            // per-VM "SELECT __PATH FROM ..." path list silently enumerated zero rows on this host
            // (see DEVELOPMENT_NOTES.md "Flagged assumptions"); this always returns real data.
            using var mgmt = GetManagementService(scope);
            using var inParams = mgmt.GetMethodParameters("GetSummaryInformation");
            inParams["SettingData"] = Array.Empty<string>();
            // Msvm_SummaryInformationRequestType (Microsoft Learn, confirmed live 2026-07-03):
            // 1=ElementName, 101=ProcessorLoad, 103=MemoryUsage, 105=Uptime, 111=StatusDescriptions.
            inParams["RequestedInformation"] = new uint[] { 1, 101, 103, 105, 111 };
            using var outParams = mgmt.InvokeMethod("GetSummaryInformation", inParams, null);

            uint rv = Convert.ToUInt32(outParams["ReturnValue"]);
            if (rv != 0)
            {
                WarnOnce(ref _summariesDegradedWarned, $"GetSummaryInformation returned non-zero ReturnValue {rv} — metrics degraded to zeros");
                return result;
            }

            if (outParams["SummaryInformation"] is ManagementBaseObject[] infos)
                foreach (var info in infos)
                    using (info)
                    {
                        var name = info["ElementName"] as string ?? "";
                        if (name.Length == 0) continue;
                        result[name] = (
                            SafeInt(info["ProcessorLoad"]),
                            SafeLong(info["MemoryUsage"]),
                            SafeULong(info["Uptime"]),
                            (info["StatusDescriptions"] as string[]) is { Length: > 0 } sd ? string.Join(" ", sd) : "");
                    }
            else if (outParams["SummaryInformation"] is not null)
                _logger.LogWarning("GetSummaryInformation returned SummaryInformation as unexpected type {Type}", outParams["SummaryInformation"]!.GetType().Name);

            if (result.Count == 0) WarnOnce(ref _summariesDegradedWarned, "GetSummaryInformation returned no VMs — metrics will read as zero");
            else _summariesDegradedWarned = false;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "GetSummaryInformation failed — metrics degraded to zeros"); }
        return result;
    }

    // NOTE (flagged for validation): every read below matches a child settings class back to its
    // owning VM by checking whether the setting's InstanceID *contains* the VM's Msvm_ComputerSystem
    // GUID (see MatchVm) — true in practice on current Hyper-V, but if a host is ever seen where a
    // VM's fields stay empty, verify this assumption first (e.g. log a raw InstanceID and compare).

    private void RefreshMemMax(ManagementScope scope, Dictionary<string, VmIdentity> vms)
    {
        if (vms.Keys.All(_memMax.ContainsKey)) return;   // configured max rarely changes — cache forever
        try
        {
            using var mem = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT InstanceID, Limit, VirtualQuantity FROM Msvm_MemorySettingData"));
            foreach (ManagementObject o in mem.Get())
                using (o)
                {
                    if (MatchVm(o["InstanceID"] as string ?? "", vms) is not { } name) continue;
                    long mb = SafeLong(o["Limit"]);
                    if (mb <= 0) mb = SafeLong(o["VirtualQuantity"]);
                    _memMax[name] = WmiVmMapper.BytesFromMb(mb);
                }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "MemMax read failed"); }
    }

    private void RefreshVhd(ManagementScope scope, Dictionary<string, VmIdentity> vms)
    {
        try
        {
            var now = DateTime.UtcNow;
            var sums = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT InstanceID, HostResource FROM Msvm_StorageAllocationSettingData"));
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    if (MatchVm(o["InstanceID"] as string ?? "", vms) is not { } name) continue;
                    if (o["HostResource"] is not string[] paths) continue;
                    foreach (var path in paths)
                        try { if (File.Exists(path)) sums[name] = sums.GetValueOrDefault(name) + new FileInfo(path).Length; }
                        catch { /* skip unreadable vhd */ }
                }

            foreach (var (name, bytes) in sums) _vhd[name] = (bytes, now);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "VHD size read failed"); }
    }

    /// <summary>Friendly name of the virtual switch each VM's primary NIC is connected to (empty if none/disconnected).</summary>
    private Dictionary<string, string> ReadSwitchNames(ManagementScope scope, Dictionary<string, VmIdentity> vms)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Same __PATH pitfall as ReadSummaries above (see DEVELOPMENT_NOTES.md "Flagged
            // assumptions") — SELECT * avoids it; the path comes from ManagementObject.Path.Path.
            var switchNameByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var sw = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Msvm_VirtualEthernetSwitch")))
                foreach (ManagementObject o in sw.Get())
                    using (o) switchNameByPath[o.Path.Path] = o["ElementName"] as string ?? "";

            // A NIC's connection to a switch is recorded on its EthernetPortAllocationSettingData;
            // HostResource holds the path to the Msvm_VirtualEthernetSwitch it's plugged into.
            using var eps = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT InstanceID, HostResource FROM Msvm_EthernetPortAllocationSettingData"));
            foreach (ManagementObject o in eps.Get())
                using (o)
                {
                    if (MatchVm(o["InstanceID"] as string ?? "", vms) is not { } name) continue;
                    if (o["HostResource"] is not string[] paths) continue;
                    foreach (var path in paths)
                        if (switchNameByPath.TryGetValue(path, out var swName)) { result[name] = swName; break; }
                }

            // Empty result despite having VMs to match means either every VM is switch-less, or the
            // Path.Path/HostResource path formats silently stopped matching — same failure shape as
            // the __PATH bug above, so warn instead of going quiet like that bug did.
            if (vms.Count > 0 && result.Count == 0)
                WarnOnce(ref _switchNamesEmptyWarned, "ReadSwitchNames matched no VM to a switch — switch names will read as blank");
            else
                _switchNamesEmptyWarned = false;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Switch-name read failed"); }
        return result;
    }

    private List<DiscoveredVm> ReadDiscovered(ManagementScope scope, Dictionary<string, VmIdentity> vms)
    {
        var nicByVm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Guest/synthetic NIC display name per VM (best-effort; falls back to a default below).
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT InstanceID, ElementName FROM Msvm_SyntheticEthernetPortSettingData"));
            foreach (ManagementObject o in s.Get())
                using (o)
                    if (MatchVm(o["InstanceID"] as string ?? "", vms) is { } name)
                        nicByVm[name] = o["ElementName"] as string ?? "Network Adapter";
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Discovered-VM NIC read failed"); }

        return vms.Keys.Select(name => new DiscoveredVm(name, nicByVm.GetValueOrDefault(name, "Network Adapter"))).ToList();
    }

    private Dictionary<string, string> ReadIps(ManagementScope scope, Dictionary<string, VmIdentity> vms)
    {
        var ips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT InstanceID, IPAddresses FROM Msvm_GuestNetworkAdapterConfiguration"));
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    if (o["IPAddresses"] is not string[] addrs) continue;
                    var ipv4 = addrs.FirstOrDefault(a => a.Contains('.') && !a.Contains(':'));
                    if (ipv4 is null) continue;
                    if (MatchVm(o["InstanceID"] as string ?? "", vms) is { } name)
                        ips.TryAdd(name, ipv4);
                }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Guest IP read failed"); }
        return ips;
    }

    // ── Power actions (job-tracked, non-blocking) ────────────────────────────────

    /// <summary>
    /// Requests a VM power change without blocking the caller/UI. Raises <see cref="OperationProgress"/>
    /// immediately ("Requesting …"), then live progress from the WMI job, then success or the exact
    /// failure text. The state watcher/metrics refresh flip the card to the real state afterward.
    /// </summary>
    public void BeginPowerAction(string vmName, VmOpKind kind)
    {
        Emit(vmName, kind, VmOpPhase.Requested, null, null);
        _ = Task.Run(() => RunPowerAction(vmName, kind));
    }

    private void RunPowerAction(string vmName, VmOpKind kind)
    {
        try
        {
            EnsureScope();
            var scope = _scope!;
            using var vm = FindVm(scope, vmName);
            if (vm is null) { Emit(vmName, kind, VmOpPhase.Failed, null, "VM not found"); return; }

            if (kind == VmOpKind.Shutdown)
            {
                RunShutdown(scope, vm, vmName);
                return;
            }

            // Already-in-target-state no-op guard. Compare the MAPPED state NAME (via WmiVmMapper, the
            // single source of truth) rather than a raw EnabledState code, so a user pause — which
            // reports EnabledState 9 (Quiesce), not the vendor 32768 — still counts as "already
            // Paused". Guards a second click (e.g. from the tray VM-power submenu, which offers Pause
            // unconditionally) after a prior job outran TrackJob's deadline but still succeeded;
            // re-issuing RequestStateChange for an already-satisfied state returns 0x8007.
            string targetStateName = kind switch
            {
                VmOpKind.Pause => "Paused",
                VmOpKind.Save  => "Saved",
                _              => "Running",   // Start / Resume
            };
            if (WmiVmMapper.MapState(Convert.ToUInt16(vm["EnabledState"])) == targetStateName)
            {
                Emit(vmName, kind, VmOpPhase.Succeeded, null, null);
                return;
            }

            // The single documented V2 RequestStateChange code that drives the VM to the target state.
            ushort requestCode = kind switch
            {
                VmOpKind.Pause => ReqQuiesce,   // 9
                VmOpKind.Save  => ReqOffline,   // 6
                _              => ReqEnabled,   // 2 — Start / Resume
            };

            using var inParams = vm.GetMethodParameters("RequestStateChange");
            inParams["RequestedState"] = requestCode;
            using var outParams = vm.InvokeMethod("RequestStateChange", inParams, null);
            uint ret = Convert.ToUInt32(outParams["ReturnValue"]);

            if (ret == 0)    { Emit(vmName, kind, VmOpPhase.Succeeded, null, null); return; }
            if (ret == 4096) { TrackJob(scope, (string)outParams["Job"], vmName, kind); return; }

            Emit(vmName, kind, VmOpPhase.Failed, null, $"error 0x{ret:X}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Power action {Kind} on {Vm} failed", kind, vmName);
            Emit(vmName, kind, VmOpPhase.Failed, null, ex.Message);
        }
        finally
        {
            TriggerRefresh();
        }
    }

    private void RunShutdown(ManagementScope scope, ManagementObject vm, string vmName)
    {
        // Graceful guest shutdown via the shutdown integration component (no WMI job; the guest
        // shuts down asynchronously, and the state watcher flips the card to Off).
        var guid = vm["Name"] as string ?? "";
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery(
            $"SELECT * FROM Msvm_ShutdownComponent WHERE SystemName='{guid}'"));
        ManagementObject? sc = s.Get().Cast<ManagementObject>().FirstOrDefault();
        if (sc is null) { Emit(vmName, VmOpKind.Shutdown, VmOpPhase.Failed, null, "Integration services not available"); return; }
        using (sc)
        using (var inP = sc.GetMethodParameters("InitiateShutdown"))
        {
            inP["Force"]  = false;
            inP["Reason"] = "Requested from Hyper-V Manager Tray";
            using var outP = sc.InvokeMethod("InitiateShutdown", inP, null);
            uint ret = Convert.ToUInt32(outP["ReturnValue"]);
            if (ret is 0 or 4096) Emit(vmName, VmOpKind.Shutdown, VmOpPhase.Running, null, null);
            else Emit(vmName, VmOpKind.Shutdown, VmOpPhase.Failed, null, $"error 0x{ret:X}");
        }
    }

    private void TrackJob(ManagementScope scope, string jobPath, string vmName, VmOpKind kind)
    {
        // Pause is near-instant (the VM stays memory-resident); everything else that reaches here
        // (Start, Resume, Save) can legitimately take minutes — e.g. a cold-boot Start under host
        // load, or a large-memory Save/Resume — so give them the same long budget as Save/Shutdown
        // always had. A too-short deadline here previously reported "timed out" on a job that was
        // still genuinely running and went on to succeed seconds later.
        var deadline = DateTime.UtcNow + (kind == VmOpKind.Pause
            ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5));
        try
        {
            using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);
            while (DateTime.UtcNow < deadline)
            {
                job.Get();
                ushort jobState = Convert.ToUInt16(job["JobState"]);
                int pct = SafeInt(job["PercentComplete"]);
                if (jobState == 7) { Emit(vmName, kind, VmOpPhase.Succeeded, null, null); return; }   // Completed
                if (jobState >= 8)                                                                    // Terminated/Killed/Exception
                {
                    Emit(vmName, kind, VmOpPhase.Failed, null, job["ErrorDescription"] as string);
                    return;
                }
                Emit(vmName, kind, VmOpPhase.Running, pct, null);
                Thread.Sleep(400);
            }
            Emit(vmName, kind, VmOpPhase.Failed, null, "timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Job tracking failed for {Vm}", vmName);
            Emit(vmName, kind, VmOpPhase.Failed, null, ex.Message);
        }
    }

    private void Emit(string vm, VmOpKind kind, VmOpPhase phase, int? pct, string? error) =>
        OperationProgress?.Invoke(new VmOperationProgress(vm, kind, phase, pct,
            WmiVmMapper.ProgressMessage(kind, phase, pct, error)));

    // ── WMI plumbing ─────────────────────────────────────────────────────────────

    private void EnsureScope()
    {
        if (_scope is { IsConnected: true }) return;
        lock (_scopeLock)
        {
            if (_scope is { IsConnected: true }) return;
            var scope = new ManagementScope(Namespace,
                new ConnectionOptions { EnablePrivileges = true });
            scope.Connect();
            _scope = scope;
        }
    }

    private static ManagementObject GetManagementService(ManagementScope scope)
    {
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Msvm_VirtualSystemManagementService"));
        return s.Get().Cast<ManagementObject>().First();
    }

    private static ManagementObject? FindVm(ManagementScope scope, string name)
    {
        var esc = name.Replace("'", "\\'");
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery(
            $"SELECT * FROM Msvm_ComputerSystem WHERE Caption='Virtual Machine' AND ElementName='{esc}'"));
        return s.Get().Cast<ManagementObject>().FirstOrDefault();
    }

    private static int   SafeInt(object? o)   { try { return o is null ? 0 : Convert.ToInt32(o);  } catch { return 0; } }
    private static long  SafeLong(object? o)  { try { return o is null ? 0 : Convert.ToInt64(o);  } catch { return 0; } }
    private static ulong SafeULong(object? o) { try { return o is null ? 0 : Convert.ToUInt64(o); } catch { return 0; } }

    /// <summary>Logs a warning only on the first occurrence of a persistent condition, so a WMI read
    /// that stays degraded across ticks doesn't flood the log once per tick.</summary>
    private void WarnOnce(ref bool alreadyWarned, string message)
    {
        if (!alreadyWarned) _logger.LogWarning(message);
        alreadyWarned = true;
    }

    public void Dispose()
    {
        try { _metricsCts?.Cancel(); _metricsCts?.Dispose(); } catch { }
        StopWatcher();
    }
}
