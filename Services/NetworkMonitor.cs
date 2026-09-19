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
    private readonly HyperVManager _hyperV;   // switch binding (native WMI)
    private readonly VmService     _vm;       // VM power (WMI)
    private readonly HyperVServiceControl _services;   // rule-driven service start and stop (issue #114)
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
    // Set first thing in Dispose(). Every pass checks it between steps, so once disposal has begun a pass
    // stops before its next read of config, Hyper-V or VM state, and publishes nothing.
    private volatile bool _disposing;
    // How long Dispose() waits for a pass in flight. Bounded: it runs on the UI thread at exit, a pass can
    // sit inside a switch rebind for seconds, and a UI-bound continuation cannot progress while it waits.
    private static readonly TimeSpan DisposeWaitBudget = TimeSpan.FromSeconds(5);
    private MatchResult? _lastApplied;
    // Tracks which physical adapter each virtual SWITCH was last successfully bound to, so we can skip
    // redundant re-binds (which cause a brief VM network drop) when nothing has changed. Keyed by switch
    // ID (issue #29, finding 2): a single scalar wrongly suppressed binding a 2nd bridged switch that
    // happened to sit on the same NIC as the first. An entry exists only after a Bound/AlreadyBound
    // outcome — a failed bind is never recorded, so the next network change retries it (finding 1).
    // Every writer holds _evalLock (apply passes and the manual override alike); ConcurrentDictionary
    // stays so that a reader added outside the lock can never see a torn skip-cache, which could wrongly
    // SKIP a required rebind.
    private readonly ConcurrentDictionary<string, string> _lastBoundAdapterBySwitch = new(StringComparer.OrdinalIgnoreCase);

    // Per-VM cancellation tokens for the bridge-lost delay timers, keyed by VM ID.
    // Protected by _disconnectLock (not _evalLock) so Dispose() can safely cancel pending
    // actions while an evaluation is still in flight without deadlocking on the semaphore.
    private readonly object _disconnectLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _pendingDisconnect = new(StringComparer.OrdinalIgnoreCase);

    // The active rule's delayed service stops (issue #114), cancelled when another rule becomes active.
    // Its own lock for the same reason as _disconnectLock.
    private readonly object _serviceStopLock = new();
    private CancellationTokenSource? _pendingServiceStop;

    // vmms reports running a little before its WMI provider answers, and the switch bind needs it.
    private static readonly TimeSpan ServiceStatesTimeout = TimeSpan.FromSeconds(60);

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
                          HyperVServiceControl services, ILogger<NetworkMonitor> logger, ILogger powerLog)
    {
        _config   = config;
        _hyperV   = hyperV;
        _vm       = vm;
        _services = services;
        _logger   = logger;
        _powerLog = powerLog;
        _debounceTimer = new System.Threading.Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _config.ConfigReloaded += OnConfigReloaded;
    }

    /// <summary>Triggers an immediate first evaluation (called once at startup).</summary>
    public void Start() => Schedule(0);

    /// <summary>
    /// A config reload re-evaluates ONLY when it moved something this monitor acts on — the rules, the
    /// fallback, or the managed VMs (issue #49). Every config write used to land here, so setting the log
    /// level in Settings ran a full NIC enumeration and a registry Class-key walk, and could reach
    /// <see cref="HyperVManager.UpdateSwitchBindingAsync"/>: a cosmetic change moving a real VM's switch.
    ///
    /// <para><b>The classification is not made here, deliberately.</b> This monitor sees only the new
    /// config; only <see cref="ConfigManager"/> sees the before AND the after, so only it can say what
    /// changed. It errs toward true — see <see cref="ConfigManager.AffectsNetwork"/> — which is the right
    /// direction: a re-evaluation that wasn't needed costs an enumeration, a skipped one that WAS needed
    /// leaves a VM on the wrong switch (or none) and says nothing.</para>
    /// </summary>
    private void OnConfigReloaded(object? sender, ConfigReloadedEventArgs e)
    {
        if (!e.AffectsNetwork)
        {
            _logger.LogDebug("Config reloaded, but nothing this monitor acts on changed — not re-evaluating");
            return;
        }

        // Changed rules are a new instruction: a manual override in force gives way to them.
        if (_hold is not null) _endHoldRequested = true;

        // 250 ms, not 0 — a deliberate choice, not the inherited immediacy (issue #49). The rules editor
        // commits each control edit separately, so one logical edit ("retarget this rule") arrives as a
        // burst of writes; at 0 each one started its own pass. This coalesces the burst into one. It is a
        // DELAY, never a skip: every reload still evaluates, ~a quarter-second later than the save — far
        // below the several seconds the enumeration and bind themselves take. The single-flight _evalLock
        // already prevents overlap, but it can only merge passes that collide; this stops them starting.
        Schedule(250);
    }

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        _debounceTimer.Change(1500, Timeout.Infinite);

    private void Schedule(int dueMs) =>
        _debounceTimer.Change(dueMs, Timeout.Infinite);

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
                ThrowIfDisposing();

                // Breadcrumb BEFORE the native adapter enumeration: GetAllNetworkInterfaces /
                // GetIPProperties run on an adapter that may be tearing down during a dock
                // disconnect.  If a native fault kills the process here, this is the last line
                // written, pinpointing evaluation as the area to inspect in the minidump.
                _logger.LogInformation("Network change — re-evaluating adapters...");

                var result = AdapterMatcher.Evaluate(_config.Current);
                _logger.LogInformation("Evaluated: rule='{Rule}' ({RuleId}) switch='{Switch}' ({SwitchId})",
                    result.RuleName, result.RuleId, result.SwitchName, result.SwitchId);

                // A manual override stands until the network really changes; its own bind is not a change.
                if (HoldAbsorbs(result)) continue;

                // Skip the apply pass only when the last pass CONFIRMED this exact outcome — same rule,
                // switch, host adapter and target VMs, and it actually succeeded. See
                // MatchResult.ConfirmsSameOutcomeFor for why each clause is load-bearing; in short, the
                // old guard tested only switch+VMs, which both (a) skipped a required rebind when a
                // different rule pointed the same switch at a different adapter, and (b) made the
                // bind-failure retry unreachable by short-circuiting before the retry path could run.
                if (_lastApplied is { } confirmed && confirmed.ConfirmsSameOutcomeFor(result))
                {
                    // Same rule, switch, adapter and VMs, and the last pass confirmed it — skip the
                    // rebind, which would drop the VM's network for nothing. The IP/gateway/DNS may
                    // still have moved underneath it (a DHCP renew, or the same adapter roaming between
                    // two networks that resolve to the same rule), so still update _lastApplied and fire
                    // SwitchApplied for the dashboard.
                    _logger.LogDebug("No switch change needed");

                    // A no-op under the current guard — every bridge transition is a RULE change
                    // (to or from "Fallback"), and the guard admits only an identical rule, so both
                    // flags are necessarily false here. Kept deliberately: it is the one line that
                    // kept this fast path correct when the guard was looser, and a future guard that
                    // stops comparing RuleId would silently start missing bridge-lost actions
                    // without it. Cheap insurance against a re-loosening.
                    HandleBridgeTransition(confirmed.RuleId, result);

                    // Nothing was applied on this pass, so this result has no outcome of its own — carry
                    // the previous pass's outcome forward (issue #37). That is sound here and ONLY here:
                    // the guard above established that the previous pass confirmed Applied for this exact
                    // rule/switch/adapter/VM set, so the fact still holds. Publishing the default
                    // NotEvaluated would flick the icon to grey on every no-change network blip; an
                    // optimistic Applied would be the exact lie #37 exists to remove.
                    //
                    // A non-Applied status is deliberately NOT reachable here — it fails the guard and
                    // routes to ApplyAsync, which re-attempts and stamps a fresh outcome. Carrying a
                    // FAILURE forward would assert a stale snapshot as durable truth and strand the
                    // retry. Applied implies no failed VMs (NetworkStatusUi.Classify), so FailedVms is
                    // empty by construction rather than by copy.
                    result = result with
                    {
                        ApplyStatus = NetworkStatusUi.SwitchApplyStatus.Applied,
                        FailedVms   = [],
                    };

                    _lastApplied = result;
                    Publish(result);
                }
                else
                {
                    await ApplyAsync(result);
                }
            }
            while (_evaluatePending);
        }
        catch (OperationCanceledException) when (_disposing) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during network evaluation");
        }
        finally
        {
            // Dispose() can land between the acquire above and this release — a dock disconnect fires
            // NetworkChange while the app is shutting down. The work the lock guarded is over by then,
            // so releasing a semaphore that no longer exists has nothing left to protect. Unguarded, it
            // throws on a timer/thread-pool thread inside an async void, which AppDomain.UnhandledException
            // takes as fatal and kills the process. Every wait site already tolerates the same disposal.
            try { _evalLock.Release(); }
            catch (ObjectDisposedException) { }
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
    public async Task<MatchResult?> ForceEvaluateAsync(Func<MatchResult, bool>? confirmRebind = null)
    {
        bool acquired;
        try { acquired = await _evalLock.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (ObjectDisposedException) { return null; }
        if (!acquired) return null;

        try
        {
            ThrowIfDisposing();
            // AdapterMatcher.Evaluate enumerates all NICs (GetAllNetworkInterfaces + GetIPProperties),
            // which can block for hundreds of ms. ForceEvaluateAsync is invoked from the tray "Re-check
            // network now" command on the UI thread, so run the enumeration on the thread pool to keep
            // the UI responsive (issue #29, finding 3). The debounce path already runs on a timer thread.
            var result = await Task.Run(() => AdapterMatcher.Evaluate(_config.Current));
            ThrowIfDisposing();

            // A rebind drops the host's network, so a person asking for this pass is asked first. The
            // prediction is the apply pass's own skip-cache test; declining leaves everything as it was.
            if (confirmRebind is not null && WouldRebind(result) && !confirmRebind(result))
            {
                _logger.LogInformation("Re-check declined before rebinding '{Switch}'", result.SwitchName);
                return result with { ApplyStatus = NetworkStatusUi.SwitchApplyStatus.NotEvaluated, UserInitiated = true };
            }

            // Asking for the rules to be applied ends any manual override.
            if (_hold is not null) _logger.LogInformation("Override ends: re-check asked for the rules");
            _hold = null;

            // userInitiated: the tray's "Re-check network now" reports this result itself (including the
            // failure), so the automatic balloon must not also fire and contradict it.
            return await ApplyAsync(result, userInitiated: true);
        }
        catch (OperationCanceledException) when (_disposing)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during forced evaluation");
            return null;   // no answer — the caller must not report this as a successful re-check
        }
        finally
        {
            // Same disposal race as OnDebounceElapsed — see the note there.
            try { _evalLock.Release(); }
            catch (ObjectDisposedException) { }
            RunDeferredPass();
        }
    }

    /// <summary>A change that arrived while a command held the lock only flagged itself; run it now.</summary>
    private void RunDeferredPass()
    {
        if (!_evaluatePending) return;
        try { Schedule(1500); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Re-derives the DISPLAY strings of the currently-published result from the host and republishes it —
    /// no bind, no VM reconnect, no autostart, no bridge transition. For a caller that changed how the host
    /// DESCRIBES itself without changing anything the rules act on; today that is exactly one caller, the
    /// adapter rename.
    ///
    /// <para><b>Why this is needed at all (issue #49 regression).</b> A rename writes only
    /// <c>adapterNames</c>, which <see cref="ConfigManager.NonNetworkProperties"/> correctly excludes from
    /// <see cref="ConfigReloadedEventArgs.AffectsNetwork"/> — so <see cref="OnConfigReloaded"/> stands down,
    /// exactly as #49 intended. What #49 could not see is that the re-evaluation it suppressed was doing a
    /// SECOND job on the side: it re-derived <see cref="MatchResult.HostAdapterName"/> from a fresh
    /// FriendlyName read and republished it through <see cref="SwitchApplied"/>. With the fan-out gone, so
    /// was the refresh, and the dashboard's adapter row kept the old name until an unrelated
    /// <c>NetworkChange</c> happened by. A rename with the restart box ticked self-heals (the device cycle
    /// fires that event itself); the far more common deferred choice never did.</para>
    ///
    /// <para><b>Why this is not the fan-out coming back.</b> The fan-out #49 removed was this monitor
    /// reaching <see cref="HyperVManager.UpdateSwitchBindingAsync"/> — a cosmetic config write moving a
    /// real VM's switch. This path cannot: it evaluates (a pure, read-only host inspection) and then does
    /// nothing but hand the result to <see cref="MatchResult.WithRefreshedDisplayFrom"/>, which is a record
    /// <c>with</c>-expression over display strings and is structurally incapable of touching a switch. The
    /// apply path is not merely not called — it is not reachable from here. And a disagreement between the
    /// fresh evaluation and the confirmed outcome publishes NOTHING (see that method): a rename must never
    /// be the reason a real network change gets acted on, or reported, early.</para>
    /// </summary>
    public async Task RefreshDisplayAsync()
    {
        // Nothing has been applied yet, so there is no display to refresh — and no result to publish that
        // would not be an invention. The first evaluation will read the new name anyway.
        if (_lastApplied is null) return;

        bool acquired;
        // Shares _evalLock with the apply path deliberately: this republishes _lastApplied, and a
        // republish racing an apply pass could put a display-refreshed copy of the PREVIOUS outcome back
        // after the new one landed. The 5 s bound matches ForceEvaluateAsync; giving up is safe here in a
        // way it is not there, because an evaluation is in flight precisely when it is holding this lock —
        // and every path out of it publishes a freshly-derived HostAdapterName of its own.
        try { acquired = await _evalLock.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (ObjectDisposedException) { return; }
        if (!acquired) return;

        try
        {
            if (_disposing || _lastApplied is not { } confirmed) return;

            // Same off-thread enumeration as ForceEvaluateAsync, for the same reason: the caller is the
            // Settings UI thread and AdapterMatcher.Evaluate can block for hundreds of ms.
            var fresh = await Task.Run(() => AdapterMatcher.Evaluate(_config.Current));

            if (confirmed.WithRefreshedDisplayFrom(fresh) is not { } refreshed)
            {
                // The host disagrees with the outcome we are holding. That is a real change (or an
                // unretried failure), which the debounced evaluate/apply path owns — not a rename.
                _logger.LogDebug("Display refresh: the host no longer confirms the published outcome — leaving it to the apply pass");
                return;
            }

            _logger.LogDebug("Display refresh: adapter now displays as '{Adapter}'", refreshed.HostAdapterName);
            _lastApplied = refreshed;
            Publish(refreshed);
        }
        catch (Exception ex)
        {
            // A cosmetic refresh must never take the app down, and never turn a completed rename into a
            // failure report: the caller invokes this fire-and-forget after the write is already verified.
            _logger.LogError(ex, "Error refreshing the network display");
        }
        finally
        {
            // Same disposal race as OnDebounceElapsed — see the note there. Fire-and-forget from the
            // rename path, so an escaping throw here is unobserved rather than reported.
            try { _evalLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>What <see cref="ManualOverrideAsync"/> did, and the adapter a bridged switch was bound
    /// to (its display name, empty when no bind was involved).</summary>
    public sealed record OverrideReport(OverrideFlow.Outcome Outcome, string AdapterShown);

    /// <summary>
    /// Forces a specific VM onto a specific switch, ignoring rules, and reports what happened so the caller
    /// can confirm it (issue #37).
    ///
    /// <para><b>Bridged switch:</b> when a rule names the switch, it is first bound to the adapter the
    /// computer is connected through now — wired or Wi-Fi, by interface identifier — and the VM moves only
    /// after that bind succeeded. The binding is recorded like a rule's, so the next rule that wants the
    /// same adapter does not rebind. <paramref name="confirmDrop"/> is asked, with the adapter's display
    /// name, before the bind drops the host's network; null (a remote command) asks nothing.</para>
    ///
    /// <para><b>Transient:</b> config is not changed. The override holds until the network really
    /// changes — see <see cref="OverrideFlow.DecideHold"/> — and then the rules decide again.</para>
    /// </summary>
    public async Task<OverrideReport> ManualOverrideAsync(string vmId, SwitchRef sw, Func<string, bool>? confirmDrop = null)
    {
        _logger.LogInformation("Manual override: {Vm} → {Switch} ({SwitchId})", vmId, sw.Shown, sw.Id);
        // One shared lookup by VM ID — see VmConfigUi.FindManagedVm.
        if (VmConfigUi.FindManagedVm(_config.Current.VirtualMachines, vmId) is not { } vm)
        {
            _logger.LogWarning("Manual override: VM '{Vm}' not found in config — nothing done", vmId);
            // Nothing was attempted, so nothing is published: _lastApplied still describes the state the
            // app last confirmed, and claiming "Manual (…)" here would invent a state that never existed.
            return new OverrideReport(OverrideFlow.Outcome.NotConfigured, "");
        }

        bool needsBind = _config.Current.Rules.Any(r => !string.IsNullOrWhiteSpace(r.SwitchId) && HostIdentity.Same(r.SwitchId, sw.Id));
        var  current   = needsBind ? await Task.Run(AdapterMatcher.GetCurrentNetworkInfo) : null;
        var  adapterId = current?.InterfaceId;
        var  shown     = current?.AdapterDescription ?? "";
        bool acquired  = false;
        bool bound     = false;

        try
        {
            var outcome = await OverrideFlow.RunAsync(
                needsBind, adapterId,
                confirmDrop: () => confirmDrop?.Invoke(shown) ?? true,
                acquire: async () =>
                {
                    // An apply pass in flight could rebind or move the VM after this override; wait for it.
                    try { acquired = await _evalLock.WaitAsync(TimeSpan.FromSeconds(30)); }
                    catch (ObjectDisposedException) { acquired = false; }
                    // Held but refused once disposal has begun: nothing is bound or moved, and the
                    // finally below still releases it for Dispose() to take.
                    return acquired && !_disposing;
                },
                bind: async () =>
                {
                    var o = await _hyperV.UpdateSwitchBindingAsync(sw, adapterId!);
                    if (o == SwitchBindOutcome.Failed) _lastBoundAdapterBySwitch.TryRemove(sw.Id, out _);
                    else { _lastBoundAdapterBySwitch[sw.Id] = adapterId!; bound = true; }
                    return o;
                },
                move: () => _hyperV.ApplySwitchAsync(vm.Ref, vm.NicId, sw));

            _logger.LogInformation("Manual override {Outcome}: {Vm} → {Switch} (adapter '{Adapter}' {AdapterId})",
                outcome, vm.Ref.Shown, sw.Shown, shown, adapterId ?? "none");

            if (outcome is OverrideFlow.Outcome.Applied or OverrideFlow.Outcome.MoveFailed)
            {
                bool ok = outcome == OverrideFlow.Outcome.Applied;
                // The host section keeps showing the real connection rather than dashes.
                var host = await Task.Run(() => AdapterMatcher.Evaluate(_config.Current));
                var result = new MatchResult(ManualRuleId, $"Manual ({sw.Shown})", sw.Id, sw.Name, [vm.Ref])
                {
                    HostAdapterName        = host.HostAdapterName,
                    HostAdapterInterfaceId = host.HostAdapterInterfaceId,
                    HostAdapterAlias       = host.HostAdapterAlias,
                    HostIp                 = host.HostIp,
                    Gateway                = host.Gateway,
                    DnsServers             = host.DnsServers,
                    ApplyStatus = ok ? NetworkStatusUi.SwitchApplyStatus.Applied
                                     : NetworkStatusUi.SwitchApplyStatus.VmConnectFailed,
                    FailedVms   = ok ? [] : new[] { vm.Ref.Shown },
                    // The command reports this outcome itself, so the automatic balloon stands down.
                    UserInitiated = true,
                };

                // Only a completed override holds; after its own bind it waits out the connection's return.
                _endHoldRequested = false;
                _hold = ok
                    ? new OverrideHoldState(bound ? DateTime.UtcNow + OverrideFlow.SettleAfterBind : DateTime.MinValue,
                                            bound ? null : OverrideFlow.Fingerprint(host))
                    : null;
                if (bound) Schedule((int)OverrideFlow.SettleAfterBind.TotalMilliseconds + 500);

                _lastApplied = result;
                Publish(result);
            }
            return new OverrideReport(outcome, shown);
        }
        finally
        {
            // Same disposal race as OnDebounceElapsed — see the note there.
            if (acquired)
            {
                try { _evalLock.Release(); }
                catch (ObjectDisposedException) { }
                RunDeferredPass();
            }
        }
    }

    /// <summary>An override that holds against the rules: until when it ignores changes, and the network
    /// it belongs to once captured.</summary>
    private sealed class OverrideHoldState(DateTime settleUntilUtc, string? fingerprint)
    {
        public DateTime SettleUntilUtc { get; } = settleUntilUtc;
        public string?  Fingerprint    { get; set; } = fingerprint;
    }

    private volatile OverrideHoldState? _hold;
    // Set by a network-affecting settings change: the next pass lets the rules decide.
    private volatile bool _endHoldRequested;

    /// <summary>
    /// True when an override hold absorbs this evaluation. Publishes the override again with the host's
    /// current connection, so the dashboard follows the connection through the bind.
    /// </summary>
    private bool HoldAbsorbs(MatchResult evaluated)
    {
        if (_hold is not { } hold) return false;
        if (_endHoldRequested)
        {
            _logger.LogInformation("Override ends: a settings change asks the rules to decide");
            _hold = null;
            _endHoldRequested = false;
            return false;
        }

        var now     = DateTime.UtcNow;
        var print   = OverrideFlow.Fingerprint(evaluated);
        var verdict = OverrideFlow.DecideHold(now, hold.SettleUntilUtc, hold.Fingerprint, print);
        switch (verdict)
        {
            case OverrideFlow.HoldVerdict.Ends:
                _logger.LogInformation("Override ends: the network changed ({Before} → {After})", hold.Fingerprint, print);
                _hold = null;
                return false;
            case OverrideFlow.HoldVerdict.Settling:
                // Look again once the connection has settled, whether or not another change arrives.
                Schedule((int)Math.Max(500, (hold.SettleUntilUtc - now).TotalMilliseconds + 500));
                _logger.LogDebug("Override holds while its bind settles");
                break;
            case OverrideFlow.HoldVerdict.Capture:
                hold.Fingerprint = print;
                _logger.LogInformation("Override holds on this network ({Network})", print);
                break;
            default:
                _logger.LogDebug("Override holds: same network");
                break;
        }

        if (_lastApplied is { } manual)
        {
            var refreshed = manual with
            {
                HostAdapterName        = evaluated.HostAdapterName,
                HostAdapterInterfaceId = evaluated.HostAdapterInterfaceId,
                HostAdapterAlias       = evaluated.HostAdapterAlias,
                HostIp                 = evaluated.HostIp,
                Gateway                = evaluated.Gateway,
                DnsServers             = evaluated.DnsServers,
            };
            _lastApplied = refreshed;
            Publish(refreshed);
        }
        return true;
    }

    /// <summary>The one place <see cref="SwitchApplied"/> is raised: never once disposal has begun, when
    /// its subscribers are tearing down.</summary>
    private void Publish(MatchResult result)
    {
        if (_disposing) return;
        SwitchApplied?.Invoke(this, result);
    }

    /// <summary>Ends a pass at its next step once disposal has begun; each entry point catches this quietly.</summary>
    private void ThrowIfDisposing()
    {
        if (_disposing) throw new OperationCanceledException("The network monitor is being disposed.");
    }

    /// <summary>The rule ID a manual override publishes under: neither a rule nor the fallback.</summary>
    private const string ManualRuleId = "manual";

    /// <summary>True when applying <paramref name="result"/> would bind its switch to an adapter this
    /// session has not confirmed it on — the same test <see cref="ApplyAsync"/> uses to skip a bind.</summary>
    private bool WouldRebind(MatchResult result)
    {
        if (result.IsFallback || string.IsNullOrWhiteSpace(result.SwitchId)
            || string.IsNullOrWhiteSpace(result.HostAdapterInterfaceId)) return false;
        _lastBoundAdapterBySwitch.TryGetValue(result.SwitchId, out var lastAdapter);
        return !HostIdentity.Same(result.HostAdapterInterfaceId, lastAdapter);
    }

    /// <summary>
    /// Applies <paramref name="result"/> (bind the switch, reconnect the target VMs) and returns the
    /// SAME result stamped with what actually happened — see <see cref="MatchResult.ApplyStatus"/>.
    /// That stamped result is what <see cref="_lastApplied"/> holds and what
    /// <see cref="SwitchApplied"/> publishes, so every status surface renders the outcome rather than
    /// the intent (issue #37).
    ///
    /// <para><paramref name="userInitiated"/> marks a pass a user explicitly asked for. It rides on the
    /// published <see cref="MatchResult.UserInitiated"/> so the automatic failure balloon can stand down
    /// and leave the report to the command that ran the pass — one action, one report.</para>
    /// </summary>
    private async Task<MatchResult> ApplyAsync(MatchResult result, bool userInitiated = false)
    {
        // Capture the previously-active rule before any state changes so autostart and
        // bridge-transition detection both see a consistent before/after snapshot.
        ThrowIfDisposing();
        var previousRule = _lastApplied?.RuleId;

        // The rule that has just become active, if any: its service and autostart settings act once, on
        // the change, never on a re-apply of the same rule. Found by ID — two rules may share a name.
        var activeRule = result.RuleId != previousRule && !result.IsFallback
            ? _config.Current.Rules.FirstOrDefault(r => r.Id == result.RuleId)
            : null;

        // A stop the previous rule scheduled belongs to the network that has just gone.
        if (result.RuleId != previousRule) CancelServiceStops();

        // Services the rule starts come up before the bind below, which goes through vmms (issue #114).
        if (activeRule is not null) await StartRuleServicesAsync(activeRule);

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

        ThrowIfDisposing();
        var target = new SwitchRef(result.SwitchId, result.SwitchName);
        if (result.IsFallback)
        {
            _lastBoundAdapterBySwitch.Clear();
        }
        else if (string.IsNullOrWhiteSpace(result.SwitchId))
        {
            // The rule's switch is not identified yet (an older settings document whose switch name
            // matched none or several). There is nothing to bind, and that is a failure to say.
            _logger.LogWarning("Rule '{Rule}' has no identified virtual switch — nothing to bind", result.RuleName);
            bindStep = NetworkStatusUi.BindStep.Failed;
        }
        else if (!string.IsNullOrWhiteSpace(result.HostAdapterInterfaceId))
        {
            _lastBoundAdapterBySwitch.TryGetValue(result.SwitchId, out var lastAdapter);
            if (!HostIdentity.Same(result.HostAdapterInterfaceId, lastAdapter))
            {
                var outcome = await _hyperV.UpdateSwitchBindingAsync(target, result.HostAdapterInterfaceId);
                bindStep = NetworkStatusUi.FromBindOutcome(outcome);
                if (outcome == SwitchBindOutcome.Failed)
                    // Leave the cache clear for this switch so the next NetworkChange retries the bind.
                    _lastBoundAdapterBySwitch.TryRemove(result.SwitchId, out _);
                else
                    _lastBoundAdapterBySwitch[result.SwitchId] = result.HostAdapterInterfaceId;
            }
        }
        else
        {
            // A rule matched but no host interface was resolved, so there is nothing to bind the
            // switch to. Today AdapterMatcher can only produce an empty one for the Fallback branch (a matched
            // rule always carries the NIC it matched), so this arm is unreachable in practice — but if
            // that ever changes, a non-fallback rule with no adapter is a bind that cannot happen, and
            // it must read as a failure rather than fall through to Applied.
            _logger.LogWarning("Rule '{Rule}' matched but no host adapter was resolved — cannot bind '{Switch}'",
                result.RuleName, target.Shown);
            bindStep = NetworkStatusUi.BindStep.Failed;
        }

        // Collect the VMs whose NIC could not be attached, so the UI can name them (issue #37). Before
        // #37 both failure paths below were log-only and the pass reported success regardless.
        var failedVms = new List<string>();
        foreach (var targetVm in result.TargetVms)
        {
            ThrowIfDisposing();
            // Same shared lookup as ManualOverrideAsync (VmConfigUi.FindManagedVm), by VM ID.
            var vm = VmConfigUi.FindManagedVm(_config.Current.VirtualMachines, targetVm.Id);
            if (vm is null)
            {
                // A rule targets a VM that isn't managed: which adapter to reconnect is unknown, so it can
                // never be reconnected. That is a real failure to put this VM on the intended network,
                // not something to skip quietly.
                _logger.LogWarning("VM '{Vm}' not found in config", targetVm.Id);
                failedVms.Add(targetVm.Shown);
                continue;
            }
            if (string.IsNullOrWhiteSpace(result.SwitchId)
                || !await _hyperV.ApplySwitchAsync(vm.Ref, vm.NicId, target))
                failedVms.Add(vm.Ref.Shown);
        }

        var status = NetworkStatusUi.Classify(bindStep, failedVms.Count);
        if (NetworkStatusUi.IsFailure(status))
            _logger.LogWarning("Apply INCOMPLETE: rule='{Rule}' switch='{Switch}' status={Status} failedVms=[{Vms}]",
                result.RuleName, target.Shown, status, string.Join(", ", failedVms));

        // Stamp the outcome onto the result BEFORE it is published/remembered — from here on this is
        // what the icon, tooltip and dashboard render.
        result = result with { ApplyStatus = status, FailedVms = failedVms, UserInitiated = userInitiated };

        // Autostart, service stops and bridge-lost actions all schedule work that outlives this pass.
        ThrowIfDisposing();

        // Per-network autostart: when this rule has just become active and opts in, start (or
        // resume) its target VMs.  Never auto-stop on leaving — by design.
        if (activeRule is { AutoStart: true } rule && rule.TargetVmIds.Count > 0)
        {
            // A stopped service is started before any VM start, whatever the rule says about it.
            string? servicesError = _services.AnyServiceDown
                ? await _services.EnsureRunningForVmStartAsync(VmOpOrigin.Auto, $"rule '{rule.Name}' autostart")
                : null;

            if (servicesError is not null)
            {
                _logger.LogWarning("Autostart for rule '{Rule}' skipped: {Error}", rule.Name, servicesError);
                _powerLog.LogWarning("AUTO Start skipped for rule '{Rule}': {Error}", rule.Name, servicesError);
            }
            else
            {
                foreach (var vmRef in _config.Current.VmRefs(rule.TargetVmIds))
                {
                    _logger.LogInformation("Autostart: starting/resuming {Vm} ({Id}) for rule '{Rule}'", vmRef.Shown, vmRef.Id, rule.Name);
                    _powerLog.LogInformation("AUTO Start '{Vm}' ({Id}): rule '{Rule}' autostart (network became active)", vmRef.Shown, vmRef.Id, rule.Name);
                    _vm.BeginPowerAction(vmRef, VmOpKind.Start, VmOpOrigin.Auto);
                }
            }
        }

        if (activeRule is not null) ScheduleServiceStops(activeRule);

        HandleBridgeTransition(previousRule, result);

        _lastApplied = result;
        Publish(result);
        return result;
    }

    // ── Hyper-V services per rule (issue #114) ──────────────────────────────────

    /// <summary>
    /// Starts the services <paramref name="rule"/> asks for, vmms first, and waits for the VMs to be
    /// readable when vmms had to start — the bind and the reconnects after this go through it. A failure
    /// is logged and the pass carries on: the bind then fails and says so on the icon.
    /// </summary>
    private async Task StartRuleServicesAsync(NetworkRule rule)
    {
        bool startedVmms = false;
        foreach (var kind in rule.ServicesToStart())
        {
            ThrowIfDisposing();
            if (_services.Monitor.State(kind) == HyperVServiceState.Running) continue;

            var name = HyperVServiceNames.DisplayName(kind);
            _logger.LogInformation("Rule '{Rule}': starting service '{Service}'", rule.Name, name);
            _powerLog.LogInformation("AUTO Start service '{Service}': rule '{Rule}' (network became active)", name, rule.Name);

            if (await _services.StartAsync(kind, VmOpOrigin.Auto) is { } error)
                _logger.LogWarning("Rule '{Rule}': service '{Service}' could not be started: {Error}", rule.Name, name, error);
            else if (kind == HyperVServiceKind.VirtualMachineManagement)
                startedVmms = true;
        }

        ThrowIfDisposing();
        if (startedVmms && !await _vm.WaitForStatesAsync(ServiceStatesTimeout))
            _logger.LogWarning("Rule '{Rule}': vmms is running, but the VMs could not be read yet", rule.Name);
    }

    /// <summary>
    /// Schedules the service stops <paramref name="rule"/> asks for, after its delay. Cancelled by
    /// <see cref="CancelServiceStops"/> when another rule becomes active first. Each stop saves every
    /// running VM on the host before stopping, through <see cref="HyperVServiceControl.StopAsync"/>.
    /// </summary>
    private void ScheduleServiceStops(NetworkRule rule)
    {
        var kinds = rule.ServicesToStop();
        if (kinds.Count == 0)
        {
            if (HyperVServiceNames.All.Any(k => rule.ServiceAction(k) == RuleServiceAction.Stop))
                _logger.LogInformation("Rule '{Rule}': service stop ignored while it auto-starts VMs", rule.Name);
            return;
        }

        var ruleName = rule.Name;
        var delaySec = SettingsOptions.NormalizeDelaySeconds(rule.ServiceStopDelaySeconds);
        var cts      = new CancellationTokenSource();
        lock (_serviceStopLock)
        {
            // Cancelled, not disposed: its task still reads the token, and disposes it on the way out.
            _pendingServiceStop?.Cancel();
            _pendingServiceStop = cts;
        }

        _logger.LogInformation("Rule '{Rule}': stopping {Services} in {Delay}s",
            ruleName, string.Join(", ", kinds.Select(HyperVServiceNames.DisplayName)), delaySec);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySec), cts.Token);

                foreach (var kind in kinds)
                {
                    // The stop itself runs to its end once begun; a rule change only prevents the next one.
                    cts.Token.ThrowIfCancellationRequested();
                    _powerLog.LogInformation("AUTO Stop service '{Service}': rule '{Rule}' (active for {Delay}s)",
                        HyperVServiceNames.DisplayName(kind), ruleName, delaySec);
                    var outcome = await _services.StopAsync(kind, VmOpOrigin.Auto, $"rule '{ruleName}'", confirm: null);
                    if (outcome.Outcome != Helpers.ServiceStopFlow.Outcome.Stopped)
                        _logger.LogWarning("Rule '{Rule}': service '{Service}' not stopped — {Outcome}: {Message}",
                            ruleName, HyperVServiceNames.DisplayName(kind), outcome.Outcome, outcome.Message);
                }
            }
            catch (OperationCanceledException) { /* another rule became active — expected */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Service stop for rule '{Rule}' failed", ruleName);
            }
            finally
            {
                lock (_serviceStopLock)
                {
                    if (ReferenceEquals(_pendingServiceStop, cts)) _pendingServiceStop = null;
                }
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    private void CancelServiceStops()
    {
        lock (_serviceStopLock)
        {
            if (_pendingServiceStop is null) return;
            _logger.LogInformation("Network changed — cancelling the pending service stop");
            _pendingServiceStop.Cancel();
            _pendingServiceStop = null;
        }
    }

    // ── Bridge-lost / bridge-restored transition ────────────────────────────────

    // Called from both the switchUnchanged fast path and ApplyAsync (full path) so that
    // disconnect actions are scheduled or cancelled regardless of whether the Hyper-V
    // switch binding itself needed to change.
    private void HandleBridgeTransition(string? previousRuleId, MatchResult result)
    {
        // previousRuleId == null means first evaluation at startup — never trigger on startup
        // even if the initial result is Fallback. Compared by rule ID: a rule NAMED "Fallback" is a rule.
        bool bridgeJustLost     = previousRuleId != null
                               && previousRuleId != MatchResult.FallbackRuleId
                               && result.IsFallback;
        bool bridgeJustRestored = previousRuleId == MatchResult.FallbackRuleId
                               && !result.IsFallback;

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
                // An entry not identified yet names no VM this app could act on.
                if (string.IsNullOrWhiteSpace(vm.Id)) continue;

                // Cancel any existing timer for this VM (bridge may have flapped).
                if (_pendingDisconnect.TryGetValue(vm.Id, out var existing))
                {
                    existing.Cancel();
                    existing.Dispose();
                }

                var cts      = new CancellationTokenSource();
                var vmRef    = vm.Ref;
                var vmId     = vm.Id;
                var vmName   = $"{vmRef.Shown} ({vmId})";
                // One source of truth for "how long?", shared with the Settings picker — see
                // SettingsOptions.EffectiveBridgeLostDelaySeconds for why 0 ("Immediate") is a real
                // value and must not be read as "unset". This was `delay > 0 ? delay : 30` inline.
                var delaySec = SettingsOptions.EffectiveBridgeLostDelaySeconds(vm);
                _pendingDisconnect[vmId] = cts;

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
                            case "pause":    _vm.BeginPowerAction(vmRef, VmOpKind.Pause,    VmOpOrigin.Auto); break;
                            case "save":     _vm.BeginPowerAction(vmRef, VmOpKind.Save,     VmOpOrigin.Auto); break;
                            case "shutdown": _vm.BeginPowerAction(vmRef, VmOpKind.Shutdown, VmOpOrigin.Auto); break;
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
                            if (_pendingDisconnect.TryGetValue(vmId, out var current) &&
                                ReferenceEquals(current, cts))
                                _pendingDisconnect.Remove(vmId);
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
        // First, so a pass in flight stops at its next step and publishes nothing from here on.
        _disposing = true;

        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        // Unsubscribing keeps a disposed monitor from staying alive through ConfigManager's event, and from
        // arming a disposed timer when a config write lands during shutdown.
        _config.ConfigReloaded -= OnConfigReloaded;
        _debounceTimer.Dispose();

        // Take the lock the pass in flight holds, so it has finished or stopped before anything it uses goes.
        bool drained;
        try { drained = _evalLock.Wait(DisposeWaitBudget); }
        catch (ObjectDisposedException) { return; }   // already disposed

        // After the wait: a pass that got past its last check before the flag was set may still have
        // scheduled a delayed action.
        CancelDisconnectActions();
        CancelServiceStops();

        if (drained)
        {
            // Held, never released: no pass can take it again, and none is left to release it.
            _evalLock.Dispose();
        }
        else
        {
            // The pass is abandoned rather than awaited further. Its lock is left undisposed so its own
            // release stays valid; the flag above stops it at its next step.
            _logger.LogWarning("Network evaluation still running after {Seconds} s at shutdown — abandoning it",
                DisposeWaitBudget.TotalSeconds);
        }
    }
}
