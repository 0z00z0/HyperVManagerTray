using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Services;

/// <summary>
/// Watches the host's network state (via <see cref="NetworkChange"/>), re-evaluates the
/// config rules on each change (debounced), and drives <see cref="HyperVManager"/> to bind
/// the virtual switch and reconnect the VMs.  Redundant switch changes are skipped.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    private readonly ConfigManager _config;
    private readonly HyperVManager _hyperV;   // switch binding (PowerShell)
    private readonly VmService     _vm;       // VM power (WMI)
    private readonly ILogger<NetworkMonitor> _logger;
    // Dedicated "vm-power" category logger → vm-power.log (issue #20): records the automatic power
    // actions this monitor triggers (autostart, on-bridge-lost) with their triggering rule/reason,
    // alongside the begin+outcome lines VmService writes for the action itself.
    private readonly ILogger _powerLog;
    private readonly System.Threading.Timer _debounceTimer;
    // Single-flight guard: only one evaluate/apply runs at a time.  '_evaluatePending'
    // coalesces changes that arrive while one is running into exactly one follow-up pass.
    private readonly SemaphoreSlim _evalLock = new(1, 1);
    private volatile bool _evaluatePending;
    private MatchResult? _lastApplied;
    // Tracks which physical adapter each virtual SWITCH was last successfully bound to, so we can skip
    // redundant re-binds (which cause a brief VM network drop) when nothing has changed. Keyed by switch
    // name (issue #29, finding 2): a single scalar wrongly suppressed binding a 2nd bridged switch that
    // happened to sit on the same NIC as the first. An entry exists only after a Bound/AlreadyBound
    // outcome — a failed bind is never recorded, so the next network change retries it (finding 1).
    // ConcurrentDictionary because ManualOverrideAsync clears it WITHOUT _evalLock (it bypasses the
    // evaluate path) while ApplyAsync mutates it under _evalLock — a plain Dictionary would corrupt under
    // that concurrent structural mutation (host-networking safety: a torn skip-cache could wrongly SKIP a
    // required rebind). Every access here is a single atomic operation, so no compound lock is needed.
    private readonly ConcurrentDictionary<string, string> _lastBoundAdapterBySwitch = new(StringComparer.OrdinalIgnoreCase);

    // Per-VM cancellation tokens for the bridge-lost delay timers.
    // Protected by _disconnectLock (not _evalLock) so Dispose() can safely cancel pending
    // actions while an evaluation is still in flight without deadlocking on the semaphore.
    private readonly object _disconnectLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _pendingDisconnect = new();

    /// <summary>
    /// Raised after an apply pass, carrying the result AND what actually happened to it
    /// (<see cref="MatchResult.ApplyStatus"/>) — the tray icon/tooltip and the dashboard host card are
    /// driven from this. Before issue #37 this fired unconditionally at the end of
    /// <see cref="ApplyAsync"/> with an outcome-free payload, so the UI could only ever show the
    /// rules' intent.
    /// </summary>
    public event EventHandler<MatchResult>? SwitchApplied;

    /// <summary>The most recently applied match result (with its <see cref="MatchResult.ApplyStatus"/>),
    /// or null if nothing has been applied yet.</summary>
    public MatchResult? LastApplied => _lastApplied;

    public NetworkMonitor(ConfigManager config, HyperVManager hyperV, VmService vm,
                          ILogger<NetworkMonitor> logger, ILogger powerLog)
    {
        _config   = config;
        _hyperV   = hyperV;
        _vm       = vm;
        _logger   = logger;
        _powerLog = powerLog;
        _debounceTimer = new System.Threading.Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _config.ConfigReloaded += (_, _) => Schedule();
    }

    /// <summary>Triggers an immediate first evaluation (called once at startup).</summary>
    public void Start() => Schedule();

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        _debounceTimer.Change(1500, Timeout.Infinite);

    private void Schedule() =>
        _debounceTimer.Change(0, Timeout.Infinite);

    private async void OnDebounceElapsed(object? _)
    {
        // Guard against ObjectDisposedException when the timer fires after Dispose() is called
        // (e.g. rapid dock disconnect followed by app exit).  In async void this would become
        // an unhandled exception and crash the process via AppDomain.UnhandledException.
        bool acquired;
        try { acquired = await _evalLock.WaitAsync(0); }
        catch (ObjectDisposedException) { return; }

        // If an evaluation is already running, just flag that another is needed and bail —
        // the in-flight pass will pick it up.  This stops overlapping timer callbacks from
        // applying switch changes concurrently.  Crucially, a rebind briefly drops the host's
        // bridged vNIC and fires its own NetworkChange events; coalescing them into one
        // follow-up pass (run after the rebind settles) prevents the VM flip-flopping
        // Bridged → Fallback → Bridged mid-operation.
        if (!acquired)
        {
            _evaluatePending = true;
            return;
        }

        try
        {
            do
            {
                _evaluatePending = false;

                // Breadcrumb BEFORE the native adapter enumeration: GetAllNetworkInterfaces /
                // GetIPProperties run on an adapter that may be tearing down during a dock
                // disconnect.  If a native fault kills the process here, this is the last line
                // written, pinpointing evaluation as the area to inspect in the minidump.
                _logger.LogInformation("Network change — re-evaluating adapters...");

                var result = AdapterMatcher.Evaluate(_config.Current);
                _logger.LogInformation("Evaluated: rule='{Rule}' switch='{Switch}'", result.RuleName, result.VirtualSwitch);

                bool switchUnchanged = _lastApplied?.VirtualSwitch == result.VirtualSwitch &&
                                       _lastApplied?.TargetVms.SequenceEqual(result.TargetVms) == true;
                if (switchUnchanged)
                {
                    // Same Hyper-V switch — skip rebind to avoid a VM network drop. But the
                    // host adapter/IP/gateway may have changed (e.g. two different mobile
                    // networks both resolving to Fallback), so still update _lastApplied and
                    // fire SwitchApplied so the dashboard reflects the current network.
                    // Also run bridge-transition detection: an adapter change that keeps the
                    // same switch might cross a bridge-lost or bridge-restored boundary and
                    // needs to schedule or cancel the per-VM disconnect actions.
                    _logger.LogDebug("No switch change needed");
                    HandleBridgeTransition(_lastApplied?.RuleName, result);

                    // Nothing was applied on this pass, so this result has no outcome of its own — carry
                    // the previous pass's outcome forward (issue #37). The switch and target VMs are
                    // identical to _lastApplied (that is what switchUnchanged tested), so the last
                    // confirmed outcome is still the true one. Publishing the default NotEvaluated here
                    // would flick the icon to grey on every no-change network blip; publishing an
                    // optimistic Applied would be the exact lie #37 exists to remove.
                    result = result with
                    {
                        ApplyStatus = _lastApplied?.ApplyStatus ?? NetworkStatusUi.SwitchApplyStatus.NotEvaluated,
                        FailedVms   = _lastApplied?.FailedVms   ?? [],
                    };

                    _lastApplied = result;
                    SwitchApplied?.Invoke(this, result);
                }
                else
                {
                    await ApplyAsync(result);
                }
            }
            while (_evaluatePending);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during network evaluation");
        }
        finally
        {
            _evalLock.Release();
        }
    }

    /// <summary>
    /// Re-evaluates rules and applies the result immediately, bypassing the "no change" check.
    ///
    /// <para>Returns the applied result — including <see cref="MatchResult.ApplyStatus"/> — so the
    /// caller can report what happened, or <b>null</b> if the evaluation could not be run at all (the
    /// lock was held for 5 s, the monitor was disposed, or the pass threw). Null means "no answer", NOT
    /// "nothing to report": the tray reports it as a failure to re-check rather than staying silent,
    /// which is what "Re-check network now" did for every one of these cases before issue #37.</para>
    /// </summary>
    public async Task<MatchResult?> ForceEvaluateAsync()
    {
        bool acquired;
        try { acquired = await _evalLock.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (ObjectDisposedException) { return null; }
        if (!acquired) return null;

        try
        {
            // AdapterMatcher.Evaluate enumerates all NICs (GetAllNetworkInterfaces + GetIPProperties),
            // which can block for hundreds of ms. ForceEvaluateAsync is invoked from the tray "Re-check
            // network now" command on the UI thread, so run the enumeration on the thread pool to keep
            // the UI responsive (issue #29, finding 3). The debounce path already runs on a timer thread.
            var result = await Task.Run(() => AdapterMatcher.Evaluate(_config.Current));
            return await ApplyAsync(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during forced evaluation");
            return null;   // no answer — the caller must not report this as a successful re-check
        }
        finally
        {
            _evalLock.Release();
        }
    }

    /// <summary>Outcome of <see cref="ManualOverrideAsync"/> (issue #37) — the override previously
    /// returned void and gave the user no confirmation, and silently did nothing at all when the VM was
    /// not in config.</summary>
    public enum OverrideOutcome
    {
        /// <summary>The VM's NIC is now on the requested switch. Transient: the next network change
        /// re-evaluates the rules and reverts it.</summary>
        Applied,
        /// <summary>The reconnect failed — nothing changed.</summary>
        Failed,
        /// <summary>The VM isn't in config.json, so its NIC name is unknown and no override is possible.
        /// Nothing was attempted.</summary>
        NotConfigured,
    }

    /// <summary>
    /// Forces a specific VM onto a specific switch, ignoring rules (used by the tray override menu), and
    /// reports what happened so the caller can confirm it to the user (issue #37).
    ///
    /// <para><b>This override is transient</b> — it deliberately bypasses the rule engine and does not
    /// change config, so the next <c>NetworkChange</c> re-evaluation puts the VM back on whatever the
    /// rules say. That lifespan was previously documented nowhere in the UI; the caller is expected to
    /// state it (see <see cref="NetworkStatusUi.OverrideAppliedMessage"/>).</para>
    /// </summary>
    public async Task<OverrideOutcome> ManualOverrideAsync(string vmName, string switchName)
    {
        _logger.LogInformation("Manual override: {Vm} → {Switch}", vmName, switchName);
        if (_config.Current.VirtualMachines.FirstOrDefault(v => v.Name == vmName) is not { } vm)
        {
            _logger.LogWarning("Manual override: VM '{Vm}' not found in config — nothing done", vmName);
            // Nothing was attempted, so nothing is published: _lastApplied still describes the state the
            // app last confirmed, and claiming "Manual (…)" here would invent a state that never existed.
            return OverrideOutcome.NotConfigured;
        }

        bool ok = await _hyperV.ApplySwitchAsync(vmName, vm.NicName, switchName);

        // Manual override bypasses the binding logic; force a re-bind next time a rule fires.
        _lastBoundAdapterBySwitch.Clear();

        // Publish the attempt WITH its real outcome. A failed override still updates the surfaces —
        // reporting "we tried to move this VM here and could not" is truthful and actionable, whereas
        // the pre-#37 behaviour published the override as an accomplished fact either way.
        var result = new MatchResult($"Manual ({switchName})", switchName, [vmName])
        {
            ApplyStatus = ok ? NetworkStatusUi.SwitchApplyStatus.Applied
                             : NetworkStatusUi.SwitchApplyStatus.VmConnectFailed,
            FailedVms   = ok ? [] : new[] { vmName },
        };
        _lastApplied = result;
        SwitchApplied?.Invoke(this, result);
        return ok ? OverrideOutcome.Applied : OverrideOutcome.Failed;
    }

    /// <summary>
    /// Applies <paramref name="result"/> (bind the switch, reconnect the target VMs) and returns the
    /// SAME result stamped with what actually happened — see <see cref="MatchResult.ApplyStatus"/>.
    /// That stamped result is what <see cref="_lastApplied"/> holds and what
    /// <see cref="SwitchApplied"/> publishes, so every status surface renders the outcome rather than
    /// the intent (issue #37).
    /// </summary>
    private async Task<MatchResult> ApplyAsync(MatchResult result)
    {
        // Capture the previously-active rule before any state changes so autostart and
        // bridge-transition detection both see a consistent before/after snapshot.
        var previousRule = _lastApplied?.RuleName;

        // When a specific rule matched, re-bind the Hyper-V virtual switch to the detected
        // physical adapter before connecting any VMs.  This is what makes an "Internal"
        // switch become an "External" (bridged) switch pointing at the right LAN NIC.
        // Skip for fallback — the fallback switch (Default Switch / NAT) needs no binding.
        //
        // Only call Set-VMSwitch when the physical adapter actually changed for THIS switch: repeated
        // calls with the same adapter cause a brief VM network drop even if nothing changed. Clear the
        // whole skip-cache when falling back so the next rule-match always re-binds (a switch may have
        // been left on a different adapter). The cache is keyed by switch (finding 2) and only recorded
        // on a non-failed bind (finding 1), so a distinct switch on the same NIC still binds, and a
        // failed bind is retried on the next change instead of being cached as done.
        // The bind half of the outcome (issue #37). Fallback needs no bind, and a skip-cache hit means
        // this session already CONFIRMED this switch on this adapter — both are legitimately NotNeeded.
        var bindStep = NetworkStatusUi.BindStep.NotNeeded;

        if (result.RuleName == "Fallback")
        {
            _lastBoundAdapterBySwitch.Clear();
        }
        else if (result.HostAdapterInterfaceName != "—")
        {
            _lastBoundAdapterBySwitch.TryGetValue(result.VirtualSwitch, out var lastAdapter);
            if (result.HostAdapterInterfaceName != lastAdapter)
            {
                var outcome = await _hyperV.UpdateSwitchBindingAsync(result.VirtualSwitch, result.HostAdapterInterfaceName);
                bindStep = NetworkStatusUi.FromBindOutcome(outcome);
                if (outcome == SwitchBindOutcome.Failed)
                    // Leave the cache clear for this switch so the next NetworkChange retries the bind.
                    _lastBoundAdapterBySwitch.TryRemove(result.VirtualSwitch, out _);
                else
                    _lastBoundAdapterBySwitch[result.VirtualSwitch] = result.HostAdapterInterfaceName;
            }
        }
        else
        {
            // A rule matched but no host interface alias was resolved, so there is nothing to bind the
            // switch to. Today AdapterMatcher can only produce "—" for the Fallback branch (a matched
            // rule always carries the NIC it matched), so this arm is unreachable in practice — but if
            // that ever changes, a non-fallback rule with no adapter is a bind that cannot happen, and
            // it must read as a failure rather than fall through to Applied.
            _logger.LogWarning("Rule '{Rule}' matched but no host adapter was resolved — cannot bind '{Switch}'",
                result.RuleName, result.VirtualSwitch);
            bindStep = NetworkStatusUi.BindStep.Failed;
        }

        // Collect the VMs whose NIC could not be attached, so the UI can name them (issue #37). Before
        // #37 both failure paths below were log-only and the pass reported success regardless.
        var failedVms = new List<string>();
        foreach (var vmName in result.TargetVms)
        {
            var vm = _config.Current.VirtualMachines.FirstOrDefault(v => v.Name == vmName);
            if (vm is null)
            {
                // A rule targets a VM that isn't in config (typically a typo in TargetVms): its NIC name
                // is unknown, so it can never be reconnected. That is a real failure to put this VM on
                // the intended network, not something to skip quietly.
                _logger.LogWarning("VM '{Vm}' not found in config", vmName);
                failedVms.Add(vmName);
                continue;
            }
            if (!await _hyperV.ApplySwitchAsync(vmName, vm.NicName, result.VirtualSwitch))
                failedVms.Add(vmName);
        }

        var status = NetworkStatusUi.Classify(bindStep, failedVms.Count);
        if (NetworkStatusUi.IsFailure(status))
            _logger.LogWarning("Apply INCOMPLETE: rule='{Rule}' switch='{Switch}' status={Status} failedVms=[{Vms}]",
                result.RuleName, result.VirtualSwitch, status, string.Join(", ", failedVms));

        // Stamp the outcome onto the result BEFORE it is published/remembered — from here on this is
        // what the icon, tooltip and dashboard render.
        result = result with { ApplyStatus = status, FailedVms = failedVms };

        // Per-network autostart: when this rule has just become active and opts in, start (or
        // resume) its target VMs.  Never auto-stop on leaving — by design.
        if (result.RuleName != previousRule && result.RuleName != "Fallback")
        {
            var rule = _config.Current.Rules.FirstOrDefault(r => r.Name == result.RuleName);
            if (rule?.AutoStart == true)
            {
                foreach (var vmName in rule.TargetVms)
                {
                    _logger.LogInformation("Autostart: starting/resuming {Vm} for rule '{Rule}'", vmName, rule.Name);
                    _powerLog.LogInformation("AUTO Start '{Vm}': rule '{Rule}' autostart (network became active)", vmName, rule.Name);
                    _vm.BeginPowerAction(vmName, VmOpKind.Start, VmOpOrigin.Auto);
                }
            }
        }

        HandleBridgeTransition(previousRule, result);

        _lastApplied = result;
        SwitchApplied?.Invoke(this, result);
        return result;
    }

    // ── Bridge-lost / bridge-restored transition ────────────────────────────────

    // Called from both the switchUnchanged fast path and ApplyAsync (full path) so that
    // disconnect actions are scheduled or cancelled regardless of whether the Hyper-V
    // switch binding itself needed to change.
    private void HandleBridgeTransition(string? previousRule, MatchResult result)
    {
        // previousRule == null means first evaluation at startup — never trigger on startup
        // even if the initial result is Fallback.
        bool bridgeJustLost     = previousRule != null
                               && previousRule != "Fallback"
                               && result.RuleName == "Fallback";
        bool bridgeJustRestored = previousRule == "Fallback"
                               && result.RuleName != "Fallback";

        if (bridgeJustLost)
            ScheduleDisconnectActions();
        else if (bridgeJustRestored)
            CancelDisconnectActions();
    }

    // ── Bridge-lost delayed actions ─────────────────────────────────────────────

    private void ScheduleDisconnectActions()
    {
        lock (_disconnectLock)
        {
            foreach (var vm in _config.Current.VirtualMachines)
            {
                var action = vm.OnBridgeLostAction;
                if (string.IsNullOrEmpty(action) || action == "none") continue;

                // Cancel any existing timer for this VM (bridge may have flapped).
                if (_pendingDisconnect.TryGetValue(vm.Name, out var existing))
                {
                    existing.Cancel();
                    existing.Dispose();
                }

                var cts      = new CancellationTokenSource();
                var vmName   = vm.Name;
                var delaySec = vm.OnBridgeLostDelaySeconds > 0 ? vm.OnBridgeLostDelaySeconds : 30;
                _pendingDisconnect[vmName] = cts;

                _logger.LogInformation(
                    "Bridge lost — scheduling '{Action}' for {Vm} in {Delay}s",
                    action, vmName, delaySec);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySec), cts.Token);

                        _logger.LogInformation(
                            "Bridge-lost action: {Action} {Vm} (bridge absent for {Delay}s)",
                            action, vmName, delaySec);
                        _powerLog.LogInformation(
                            "AUTO {Action} '{Vm}': bridged network lost (absent for {Delay}s)",
                            action, vmName, delaySec);

                        switch (action)
                        {
                            case "pause":    _vm.BeginPowerAction(vmName, VmOpKind.Pause,    VmOpOrigin.Auto); break;
                            case "save":     _vm.BeginPowerAction(vmName, VmOpKind.Save,     VmOpOrigin.Auto); break;
                            case "shutdown": _vm.BeginPowerAction(vmName, VmOpKind.Shutdown, VmOpOrigin.Auto); break;
                        }
                    }
                    catch (OperationCanceledException) { /* bridge restored — expected */ }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Bridge-lost action failed for {Vm}", vmName);
                    }
                    finally
                    {
                        // Remove the completed entry so the dictionary doesn't accumulate
                        // stale CTS objects after actions have already fired.
                        lock (_disconnectLock)
                        {
                            if (_pendingDisconnect.TryGetValue(vmName, out var current) &&
                                ReferenceEquals(current, cts))
                                _pendingDisconnect.Remove(vmName);
                        }
                    }
                }, CancellationToken.None);
            }
        }
    }

    private void CancelDisconnectActions()
    {
        lock (_disconnectLock)
        {
            foreach (var kv in _pendingDisconnect)
            {
                _logger.LogInformation("Bridge restored — cancelling pending action for {Vm}", kv.Key);
                kv.Value.Cancel();
                kv.Value.Dispose();
            }
            _pendingDisconnect.Clear();
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _debounceTimer.Dispose();
        CancelDisconnectActions();
        _evalLock.Dispose();
    }
}
