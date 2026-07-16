using HyperVManagerTray.Services;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure outcome → UI decisions for the network/switch-apply path (issue #37), the network-side
/// counterpart to <see cref="VmStateUi"/>. WMI-free and side-effect-free, so every decision below is
/// unit-testable without a live Hyper-V host.
///
/// <para><b>Why this exists.</b> Before issue #37 the tray icon, the tray tooltip and the dashboard
/// HOST NETWORK card were all derived from the rule-match <i>intent</i> — "which switch did the rules
/// pick?" — and <c>SwitchApplied</c> was raised unconditionally at the end of the apply pass. A failed
/// switch bind or a failed VM-NIC reconnect was log-only, so the icon showed a confident green
/// "bridged" while the VM was in fact still on the old switch. The apply pipeline now carries a
/// <see cref="SwitchApplyStatus"/> and every status surface renders THAT, not the intent.</para>
///
/// <para><b>The invariant this file exists to hold.</b> A status surface must never claim a state the
/// app has not confirmed. Concretely: <see cref="IconFor"/> returns
/// <see cref="TrayIconState.Bridged"/> or <see cref="TrayIconState.Fallback"/> — the two "all is
/// well, the VM is on this network" colours — for <see cref="SwitchApplyStatus.Applied"/> and for
/// nothing else. Every other status renders as <see cref="TrayIconState.Failed"/> (red: we tried and
/// it did not work) or <see cref="TrayIconState.Unknown"/> (grey: we have not established the state).
/// There is deliberately no "probably fine" state. If a future status is added, it lands in the
/// <c>_ =&gt; Unknown</c> arm rather than silently reading as success — and
/// <c>NetworkStatusUiTests.IconFor_OnlyAppliedEverRendersAsSuccess</c> enumerates the enum to enforce
/// exactly that.</para>
/// </summary>
public static class NetworkStatusUi
{
    /// <summary>
    /// What actually happened on the most recent apply pass — as opposed to what the rules intended.
    /// Carried on <see cref="MatchResult.ApplyStatus"/> from <c>NetworkMonitor.ApplyAsync</c> to every
    /// status surface.
    /// </summary>
    public enum SwitchApplyStatus
    {
        /// <summary>No apply pass has completed yet (startup, before the first evaluation). We do not
        /// know where the VMs are — the icon is grey, not an optimistic guess.</summary>
        NotEvaluated,

        /// <summary>The switch is on the intended host adapter (bound now, already bound, or no bind
        /// was required) AND every target VM's NIC is attached to it. The only status that may render
        /// as a success colour.</summary>
        Applied,

        /// <summary>The virtual switch could not be bound to the host adapter — the adapter is absent,
        /// the switch or its external port was not found, or the WMI modify failed. The VMs are NOT on
        /// the intended network regardless of what the rule said.</summary>
        BindFailed,

        /// <summary>The switch binding is fine (or was not needed), but at least one target VM's NIC
        /// could not be attached to it — a bad <c>nicName</c>, a VM missing from Hyper-V, a target VM
        /// absent from config, or a failed WMI modify. See <see cref="MatchResult.FailedVms"/>.</summary>
        VmConnectFailed,
    }

    /// <summary>The switch-binding half of an apply pass, reduced to what the status decision needs.</summary>
    public enum BindStep
    {
        /// <summary>No bind was attempted this pass, and none was needed: the fallback switch (Default
        /// Switch / NAT) needs no external binding, or this session already bound this switch to this
        /// exact adapter and nothing has changed since (the skip-cache hit). The skip-cache only ever
        /// records a confirmed <see cref="SwitchBindOutcome.Bound"/> / <see cref="SwitchBindOutcome.AlreadyBound"/>,
        /// and is cleared on a failure or a fallback, so a hit is a genuine earlier confirmation rather
        /// than an assumption.</summary>
        NotNeeded,

        /// <summary>The switch is confirmed to be on the requested adapter (a real rebind, or it was
        /// already there).</summary>
        Succeeded,

        /// <summary>The bind was attempted and failed.</summary>
        Failed,
    }

    /// <summary>
    /// Maps a <see cref="SwitchBindOutcome"/> to its <see cref="BindStep"/>.
    /// <see cref="SwitchBindOutcome.AlreadyBound"/> is a success — the switch IS on the
    /// requested adapter, which is the thing the UI claims — the fact that no WMI write was needed to
    /// get there is irrelevant to the user. <see cref="SwitchBindOutcome.Failed"/> maps
    /// to <see cref="BindStep.Failed"/> and can never reach <see cref="SwitchApplyStatus.Applied"/>.
    /// </summary>
    public static BindStep FromBindOutcome(SwitchBindOutcome outcome) => outcome switch
    {
        SwitchBindOutcome.Bound        => BindStep.Succeeded,
        SwitchBindOutcome.AlreadyBound => BindStep.Succeeded,
        _                              => BindStep.Failed,   // Failed, and any future member
    };

    /// <summary>
    /// The overall status of an apply pass from its two halves. A bind failure DOMINATES: if the switch
    /// isn't on the right adapter then no amount of VM-NIC success puts the VMs on the intended
    /// network, and "bind failed" is the actionable root cause to report.
    /// </summary>
    public static SwitchApplyStatus Classify(BindStep bind, int failedVmCount) => bind switch
    {
        BindStep.Failed                     => SwitchApplyStatus.BindFailed,
        _ when failedVmCount > 0            => SwitchApplyStatus.VmConnectFailed,
        BindStep.Succeeded or BindStep.NotNeeded => SwitchApplyStatus.Applied,
        _                                   => SwitchApplyStatus.NotEvaluated,
    };

    /// <summary>True for the statuses that represent a real, user-actionable failure.</summary>
    public static bool IsFailure(SwitchApplyStatus status) =>
        status is SwitchApplyStatus.BindFailed or SwitchApplyStatus.VmConnectFailed;

    /// <summary>
    /// The tray icon colour for an apply status. <paramref name="bridgedTarget"/> is the INTENT (the
    /// rules picked a non-fallback switch) and is consulted ONLY once the apply is confirmed
    /// <see cref="SwitchApplyStatus.Applied"/> — that is the whole point of issue #37. A failure
    /// renders red and an unestablished state renders grey, never the optimistic target colour.
    /// </summary>
    public static TrayIconState IconFor(SwitchApplyStatus status, bool bridgedTarget) => status switch
    {
        SwitchApplyStatus.Applied         => bridgedTarget ? TrayIconState.Bridged : TrayIconState.Fallback,
        SwitchApplyStatus.BindFailed      => TrayIconState.Failed,
        SwitchApplyStatus.VmConnectFailed => TrayIconState.Failed,
        _                                 => TrayIconState.Unknown,   // NotEvaluated, and any future member
    };

    /// <summary>
    /// The dashboard HOST NETWORK card's "Rule" row: the rule name, suffixed with the failure when the
    /// apply didn't land (e.g. "Office LAN — bind failed"), mirroring how the VM cards overlay
    /// "Failed: …". <see cref="IsFailure"/> decides whether the row is also coloured red.
    /// </summary>
    public static string RuleRowText(string ruleName, SwitchApplyStatus status) => status switch
    {
        SwitchApplyStatus.Applied         => ruleName,
        SwitchApplyStatus.BindFailed      => $"{ruleName} — bind failed",
        SwitchApplyStatus.VmConnectFailed => $"{ruleName} — VM connect failed",
        _                                 => $"{ruleName} — not applied yet",
    };

    /// <summary>
    /// The short status suffix for the tray tooltip's switch row (e.g. "Bridged — bind failed"), so a
    /// hover reports the outcome rather than the intent. Empty for a confirmed apply — the switch name
    /// alone already says it. The caller keeps the Win32 tooltip length clamp.
    /// </summary>
    public static string TooltipSwitchSuffix(SwitchApplyStatus status) => status switch
    {
        SwitchApplyStatus.Applied         => "",
        SwitchApplyStatus.BindFailed      => " — bind failed",
        SwitchApplyStatus.VmConnectFailed => " — VM connect failed",
        _                                 => " — not applied yet",
    };

    /// <summary>
    /// The balloon text for a failed apply, mirroring <c>App.OnVmOperationFailed</c>'s treatment of a
    /// failed power action. Names the switch and the adapter the bind targeted (the two things the user
    /// needs to check) and points at the log for the underlying WMI error. Returns null when the
    /// result is not a failure — there is nothing to report.
    ///
    /// <para><b>Takes the whole <see cref="MatchResult"/> on purpose.</b> It previously took a loose
    /// <c>adapterName</c> string, and both call sites passed <see cref="MatchResult.HostAdapterName"/> —
    /// the DisplayNameResolver's <i>description</i> ("Realtek USB GbE Family Controller") — while this
    /// message is about the bind, which targets <see cref="MatchResult.HostAdapterInterfaceName"/> (the
    /// OS alias, "Ethernet 5"). Two adapters can share a description, so the one message the user reads
    /// when the network is broken named a string that need not identify the adapter that actually
    /// failed. That is the exact description-vs-alias conflation <c>docs/STYLE.md</c> exists to
    /// eliminate. Selecting the field HERE, once, means no call site can reintroduce it.</para>
    /// </summary>
    public static string? FailureMessage(MatchResult result) => result.ApplyStatus switch
    {
        // "virtual switch", not a bare "switch" (issue #42): each of these is read alone in a balloon,
        // so each is a first mention, and "switch" next to "network" in the same sentence is precisely
        // the pair the pinned vocabulary exists to keep apart.
        SwitchApplyStatus.BindFailed =>
            $"Could not bind virtual switch '{result.VirtualSwitch}' to '{result.HostAdapterInterfaceName}'. " +
            "The VM is not on this network — see switcher.log.",
        SwitchApplyStatus.VmConnectFailed =>
            $"Could not connect {DescribeVms(result.FailedVms)} to virtual switch '{result.VirtualSwitch}' — see switcher.log.",
        _ => null,
    };

    /// <summary>
    /// The "Re-check network now" confirmation (issue #37, recommendation 5). The command previously
    /// completed with zero feedback of any kind, so the user could not tell whether it had run, matched,
    /// or failed. Always answers, success or failure.
    ///
    /// <para>It deliberately reports the evaluated rule → switch and the apply outcome rather than
    /// claiming "no change": <see cref="HyperVManager.ApplySwitchAsync"/> and
    /// <see cref="HyperVManager.UpdateSwitchBindingAsync"/> hold their own internal skip guards, so
    /// whether any WMI write actually occurred is not known at this level — and asserting "no change"
    /// without knowing would be the exact overclaim this issue is about.</para>
    /// </summary>
    public static string ReCheckMessage(MatchResult result)
    {
        var headline = $"Re-checked: {result.RuleName} → {result.VirtualSwitch}";
        var failure  = FailureMessage(result);
        if (failure is not null) return $"{headline}\n\n{failure}";
        return result.ApplyStatus == SwitchApplyStatus.Applied
            ? headline
            : $"{headline}\n\nThe result could not be confirmed — see switcher.log.";
    }

    /// <summary>
    /// The "Override VM switch" confirmation (issue #37, recommendation 5). Beyond confirming the
    /// action, this is the ONLY place the override's transience is stated: the override is undone by
    /// the next <c>NetworkChange</c> re-evaluation, which was previously documented nowhere in the UI —
    /// the user had no way to know the override had a lifespan. The tray submenu label carries a short
    /// form of the same warning up front.
    /// </summary>
    public static string OverrideAppliedMessage(string vmName, string switchName) =>
        $"Override applied: {vmName} → {switchName}.\n\n" +
        "This is temporary — the next network change re-evaluates the rules and reverts it.";

    /// <summary>The "Override VM switch" failure text — the action silently no-opped before issue #37.</summary>
    public static string OverrideFailedMessage(string vmName, string switchName) =>
        $"Could not move {vmName} to virtual switch '{switchName}' — see switcher.log. Nothing was changed.";

    /// <summary>The "Override VM switch" text for a VM this app does not manage (a silent no-op before
    /// issue #37 — <c>ManualOverrideAsync</c> simply returned).
    ///
    /// <para>Says "managed VM", not "in config.json", and "network adapter", not "NIC" (issue #42): the
    /// user's mental model is the tray's Manage VMs list, not the file behind it, and the fix is to tick
    /// the VM there — so the message names the thing they can act on.</para></summary>
    public static string OverrideNotConfiguredMessage(string vmName) =>
        $"{vmName} is not a managed VM, so its network adapter is unknown. Nothing was changed.\n\n" +
        "Start managing it from the tray's Manage VMs list, then try again.";

    /// <summary>Renders a failed-VM list for a one-line message: "'a'", "'a' and 'b'", "'a', 'b' and 'c'".
    /// Falls back to a neutral phrase for an empty list so a message never reads "Could not connect  to …".</summary>
    private static string DescribeVms(IReadOnlyList<string> vms) => vms.Count switch
    {
        0 => "one or more VMs",
        1 => $"'{vms[0]}'",
        _ => $"{string.Join(", ", vms.Take(vms.Count - 1).Select(v => $"'{v}'"))} and '{vms[^1]}'",
    };
}
