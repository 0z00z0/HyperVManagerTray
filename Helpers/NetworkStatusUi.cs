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
        SwitchApplyStatus.VmConnectFailed => ConnectFailedSentence(result.FailedVms, result.VirtualSwitch),
        _ => null,
    };

    /// <summary>
    /// The one sentence that says "these VMs are not on that virtual switch" — shared verbatim by
    /// <see cref="FailureMessage"/>'s VmConnectFailed arm and <see cref="ConnectBindFailedMessage"/>.
    ///
    /// <para><b>Why it is extracted (code review, 2026-07-16).</b> The two call sites already committed,
    /// in prose, to being "deliberately the same sentence … because it is the same fact" — but said it as
    /// two string literals, which is a comment asserting an invariant the compiler was not holding. The
    /// only real difference between the callers is where the VM list comes from: an apply pass over N
    /// target VMs (<see cref="MatchResult.FailedVms"/>) versus a single VM the user just clicked. That is
    /// one argument, not one sentence. As two literals, fixing the bare-"switch" vocabulary here or
    /// repointing "switcher.log" after the #20/#21 log split would have changed one and silently left the
    /// other — the exact drift <c>docs/DISPLAY-VOCABULARY.md</c> corollary 4 exists to prevent.</para>
    /// </summary>
    private static string ConnectFailedSentence(IReadOnlyList<string> vms, string switchName) =>
        $"Could not connect {DescribeVms(vms)} to virtual switch '{switchName}' — see switcher.log.";

    /// <summary>
    /// The Connect / Start &amp; Connect failure text (issue #45): the VM's adapter could not be confirmed
    /// on the applied virtual switch, but the console is being opened anyway — see the connect-anyway
    /// rule on <see cref="VmConnectFlow"/>.
    ///
    /// <para><b>Deliberately the same sentence as <see cref="FailureMessage"/>'s VmConnectFailed arm</b>
    /// — because it is the same fact. Both now build it from <see cref="ConnectFailedSentence"/>, so that
    /// is enforced by the compiler rather than asserted by this paragraph. This is a separate method only
    /// because that arm reads its VM list off a <see cref="MatchResult"/> (an automatic apply pass over
    /// N target VMs), whereas this one is a single VM the user just clicked, with no apply pass behind
    /// it. Living here rather than in the flow keeps every word the user reads about the network in one
    /// file, which is the rule <c>ManagedVmActions.cs:33</c> and <c>SettingsWindow.cs:112</c> both state
    /// and the one issue #37 spent its effort restoring.</para>
    ///
    /// <para>The second sentence exists because the first one alone would be a lie by omission: the
    /// console opens regardless, and a message that reports only the failure would leave the user
    /// wondering whether the window that just appeared meant it had recovered.</para>
    /// </summary>
    public static string ConnectBindFailedMessage(string vmName, string switchName) =>
        ConnectFailedSentence([vmName], switchName) + "\n\n" +
        "Opening the VM console anyway — the VM is not on the intended network.";

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

    /// <summary>The re-check could not run at all — <c>ForceEvaluateAsync</c> returned null (busy, disposed,
    /// or it threw). We genuinely do not know the outcome, so this claims nothing about the network.</summary>
    public static string ReCheckUnavailableMessage() =>
        "Could not re-check the network right now — see switcher.log.";

    /// <summary>The unexpected-exception arm of the re-check.</summary>
    public static string ReCheckUnexpectedErrorMessage(string error) =>
        $"Could not re-check the network: {error}. See switcher.log.";

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

    /// <summary>The unexpected-exception arm of the override. Unlike <see cref="OverrideFailedMessage"/> it
    /// does NOT claim "nothing was changed": that message answers a <c>ManualOverrideAsync</c> that
    /// returned a clean failure, whereas this one answers a throw from somewhere unknown in the flow.</summary>
    public static string OverrideUnexpectedErrorMessage(string vmName, string error) =>
        $"Could not move {vmName} to the virtual switch: {error}. See switcher.log.";

    /// <summary>The "Override VM switch" text for a VM this app does not manage (a silent no-op before
    /// issue #37 — <c>ManualOverrideAsync</c> simply returned).
    ///
    /// <para>Says "managed VM", not "in config.json", and "network adapter", not "NIC" (issue #42): the
    /// user's mental model is the tray's Manage VMs list, not the file behind it, and the fix is to tick
    /// the VM there — so the message names the thing they can act on.</para></summary>
    public static string OverrideNotConfiguredMessage(string vmName) =>
        $"{vmName} is not a managed VM, so its network adapter is unknown. Nothing was changed.\n\n" +
        "Start managing it from the tray's Manage VMs list, then try again.";

    // ── "Repair host networking" (issue #51) ────────────────────────────────────
    //
    // These four were composed inline in NetworkActions and shown as blocking modals, beside a Re-check
    // button that answered the same kind of question with a balloon. They are reports of a command's
    // outcome, so they are balloons now, and their text lives here for the same reason every sibling's
    // does: the claim a message makes is the thing worth unit-testing, and it must not be re-derived at
    // whichever surface happens to show it. See docs/DISPLAY-VOCABULARY.md.

    /// <summary>
    /// What the repair did on ONE configured switch — the pure mirror of <c>HyperVManager.HostVNicState</c>,
    /// which lives on the live-host WMI mutator and is deliberately not linked into the test project. The
    /// caller maps; every decision about what to SAY happens here, where a test can reach it.
    ///
    /// <para>The names say what was observed, not what the enum's author hoped: <see cref="Collapsed"/>
    /// and <see cref="ShareRestored"/> are opposite repairs and are no longer allowed to share a
    /// sentence (see <see cref="RepairReportFor"/>).</para>
    /// </summary>
    public enum RepairStep
    {
        /// <summary>The switch was found and inspected, and had exactly one host vNIC already. Healthy —
        /// nothing was done and nothing needed doing.</summary>
        Inspected,

        /// <summary>A duplicate host vNIC was found and the extras were removed, leaving exactly one.
        /// <c>HostVNicState.Repaired</c>.</summary>
        Collapsed,

        /// <summary>The switch was External with host sharing off (zero host vNICs), so one was ADDED
        /// back. <c>HostVNicState.Reshared</c> — the opposite of <see cref="Collapsed"/>.</summary>
        ShareRestored,

        /// <summary>The configured switch does not exist on the host, so NOTHING about it was inspected.
        /// <c>HostVNicState.NoSwitch</c> — a rule names a switch that was renamed, deleted, or mistyped.</summary>
        SwitchNotFound,

        /// <summary>The repair threw part-way. <c>HostVNicState.Error</c> — see <see cref="RepairReportFor"/>
        /// on why this may NOT be reported as "nothing was changed".</summary>
        Failed,
    }

    /// <summary>One configured switch and what the repair did to it.</summary>
    /// <param name="SwitchName">The switch named by a rule.</param>
    /// <param name="Step">What was observed or done.</param>
    public readonly record struct RepairStepOn(string SwitchName, RepairStep Step);

    /// <summary>A composed repair report: the balloon text, and whether it is an error.</summary>
    /// <param name="Message">What to tell the user.</param>
    /// <param name="IsError">True when something failed or could not be checked.</param>
    public readonly record struct RepairReport(string Message, bool IsError);

    /// <summary>
    /// Composes the "Repair host networking" report from the per-switch outcomes — the whole decision, in
    /// one pure place, because the three bugs this replaced were all decisions made in a call-site
    /// if/else chain that no test could reach (code review, 2026-07-16).
    ///
    /// <para><b>The rule it enforces</b> is <c>docs/DISPLAY-VOCABULARY.md</c> corollary 3 — <i>a report
    /// states only what was verified</i> — which the code it replaces broke three ways, each in the one
    /// message a user reads while their host networking is down:</para>
    /// <list type="number">
    /// <item><b>A clean bill of health for a switch that was never found.</b> <c>NoSwitch</c> counted as
    /// neither repaired nor error, so it fell through to "no duplicate was found on any configured
    /// switch" — a positive claim that every configured switch had been inspected, issued precisely when
    /// the configured switch does not exist on the host. That is the misconfiguration Repair is run to
    /// diagnose, and the user was told their host was fine. It is now <see cref="RepairStep.SwitchNotFound"/>
    /// and is reported as the actionable fault it is.</item>
    /// <item><b>"Nothing was changed" after the host was changed.</b> <c>Failed</c> is returned from a
    /// catch wrapping a loop that has already mutated the host — <c>RemoveExtraInternalPorts</c> removes
    /// extras one at a time, so a throw on the second of three leaves one vNIC already gone. The report
    /// says the host MAY have been partly changed, because that is the honest state of knowledge.</item>
    /// <item><b>A collapse that never happened.</b> <c>Repaired</c> and <c>Reshared</c> were both fed to
    /// one "a duplicate was collapsed back to one" sentence, but <c>Reshared</c> ADDED a missing host
    /// vNIC — the opposite repair. Each now gets its own clause.</item>
    /// </list>
    ///
    /// <para><b>Composition, not priority.</b> Each bucket contributes a clause naming only its own
    /// switches, so a pass that repairs one switch and cannot find another reports both facts rather than
    /// letting the happier one speak for the whole host. A repair that fixed something still reports the
    /// fix even alongside an error: the collapse is a confirmed fact and the user's link is coming back.</para>
    /// </summary>
    public static RepairReport RepairReportFor(IReadOnlyList<RepairStepOn> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count == 0) return new RepairReport(RepairNoSwitchesMessage(), IsError: false);

        var collapsed = Named(outcomes, RepairStep.Collapsed);
        var restored  = Named(outcomes, RepairStep.ShareRestored);
        var failed    = Named(outcomes, RepairStep.Failed);
        var notFound  = Named(outcomes, RepairStep.SwitchNotFound);
        var inspected = Named(outcomes, RepairStep.Inspected);

        var parts = new List<string>();

        // Verified repairs. "virtual switch" on first mention (issue #42) — each report is read alone in
        // a balloon whose title is already "… — network", so a bare "switch" under a "network" heading is
        // exactly the pair the pinned vocabulary keeps apart. See docs/STYLE.md.
        if (collapsed.Count > 0)
            parts.Add($"Repaired host networking on {SwitchNoun(collapsed.Count)} {DescribeSwitches(collapsed)}: " +
                      "a duplicate host network adapter was collapsed back to one — your wired connection " +
                      "should return within a few seconds.");

        // Drops the noun when a collapse clause already introduced it, so one report never says
        // "virtual switch" twice — that is the "(then 'switch')" half of the pinned rule.
        if (restored.Count > 0)
            parts.Add($"Repaired host networking on {(collapsed.Count > 0 ? "" : SwitchNoun(restored.Count) + " ")}" +
                      $"{DescribeSwitches(restored)}: a missing host network adapter was added back — your " +
                      "wired connection should return within a few seconds.");

        // Attempted and threw mid-flight: the host may already be partly changed. Never "nothing changed".
        if (failed.Count > 0)
            parts.Add($"Could not repair {SwitchNoun(failed.Count)} {DescribeSwitches(failed)} — see " +
                      "switcher.log. The repair had already started, so the host may have been partly changed.");

        // Never inspected: the switch is not on the host at all.
        if (notFound.Count > 0)
            parts.Add($"{char.ToUpperInvariant(SwitchNoun(notFound.Count)[0])}{SwitchNoun(notFound.Count)[1..]} " +
                      $"{DescribeSwitches(notFound)} " +
                      $"{(notFound.Count == 1 ? "was" : "were")} not found on the host, so " +
                      $"{(notFound.Count == 1 ? "it was" : "they were")} not checked — a rule names a switch " +
                      "that does not exist here.");

        // Only when there is nothing else to say. Names the switches actually inspected rather than
        // claiming "any configured switch" — that claim is only true when every switch was found.
        if (parts.Count == 0)
            parts.Add($"Nothing needed repairing — no duplicate host network adapter was found on " +
                      $"{SwitchNoun(inspected.Count)} {DescribeSwitches(inspected)}.");

        return new RepairReport(string.Join(" ", parts), IsError: failed.Count > 0 || notFound.Count > 0);
    }

    private static List<string> Named(IReadOnlyList<RepairStepOn> outcomes, RepairStep step) =>
        outcomes.Where(o => o.Step == step).Select(o => o.SwitchName).ToList();

    /// <summary>"virtual switch" / "virtual switches" — the pinned first-mention noun (docs/STYLE.md),
    /// pluralised. Each clause of a repair report is its own first mention of the object, because the
    /// report may consist of that clause alone.</summary>
    private static string SwitchNoun(int count) => count == 1 ? "virtual switch" : "virtual switches";

    /// <summary>Nothing to repair because no bridged switch is configured — a statement about the CONFIG,
    /// not about the host, which was never inspected. Says so rather than implying a clean bill of health
    /// the app has not earned (issue #37).</summary>
    public static string RepairNoSwitchesMessage() =>
        "No bridged virtual switches are configured, so there was nothing to repair. No rule names a switch to check.";

    /// <summary>The unexpected-exception arm of the repair command — the throw escaped the per-switch loop
    /// itself, so switches earlier in the loop may already have been repaired.
    ///
    /// <para>Routed through this class rather than composed at the call site (corollary 4), and pointedly
    /// does NOT say "nothing was changed" like its siblings: at this point the app does not know how far
    /// the loop got, and the unexpected-exception path is exactly where a confident claim is least
    /// earned.</para></summary>
    public static string RepairUnexpectedErrorMessage(string error) =>
        $"Could not finish repairing host networking: {error}. See switcher.log — some virtual switches may " +
        "already have been changed.";

    // ── "Add current network" (issue #51) ───────────────────────────────────────

    /// <summary>No adapter to capture — the command cannot proceed and says why.</summary>
    public static string AddRuleNoAdapterMessage() =>
        "No active network adapter with an IPv4 address was found, so there is no current network to add.";

    /// <summary>
    /// The Wi-Fi rejection (issue #29, finding 5). Shortened from its modal wording for the balloon —
    /// which truncates — but the two facts that make it actionable both survive: bridging cannot target
    /// a wireless adapter at all, and NOTHING was saved. A rejection that lost the second half would read
    /// as a rule the user still has.
    /// </summary>
    public static string AddRuleWirelessMessage(string adapterDescription) =>
        $"\"{adapterDescription}\" is a Wi-Fi adapter. A Hyper-V virtual switch can only bridge onto a wired " +
        "(Ethernet) adapter, such as a USB-Ethernet dock — so no rule was added.";

    /// <summary>This adapter's MAC already has a rule. Points at the rule, which is on screen in the
    /// editor this command lives beside.</summary>
    public static string AddRuleDuplicateMessage(string ruleName) =>
        $"This adapter is already covered by rule \"{ruleName}\", so no rule was added. Edit that rule to update it.";

    /// <summary>The rule could not be written. Names the error and states that nothing was saved — true
    /// here, and only here, because this arm wraps the config write alone.</summary>
    public static string AddRuleSaveFailedMessage(string error) =>
        $"Could not save the rule: {error}. Nothing was changed.";

    /// <summary>The unexpected-exception arm of "Add current network" — routed through this class rather
    /// than composed at the call site (corollary 4).
    ///
    /// <para>Its siblings all state whether a rule was written; this one cannot honestly. The throw could
    /// come from the adapter scan, the name prompt, or anywhere else in the flow, on either side of the
    /// config write — so it says where to look instead of guessing. Pointing at the rules list is the
    /// answer the user needs, and it is one they can act on immediately: the list is on screen, since
    /// this command lives in the rules editor.</para></summary>
    public static string AddRuleUnexpectedErrorMessage(string error) =>
        $"Could not add the current network: {error}. See switcher.log — check the rules list before " +
        "trying again, in case the rule was already saved.";

    /// <summary>Renders a switch-name list the way <see cref="DescribeVms"/> renders VMs, so the two
    /// report channels read alike.</summary>
    private static string DescribeSwitches(IReadOnlyList<string> switches) => switches.Count switch
    {
        0 => "the configured switches",
        1 => $"'{switches[0]}'",
        _ => $"{string.Join(", ", switches.Take(switches.Count - 1).Select(s => $"'{s}'"))} and '{switches[^1]}'",
    };

    /// <summary>Renders a failed-VM list for a one-line message: "'a'", "'a' and 'b'", "'a', 'b' and 'c'".
    /// Falls back to a neutral phrase for an empty list so a message never reads "Could not connect  to …".</summary>
    private static string DescribeVms(IReadOnlyList<string> vms) => vms.Count switch
    {
        0 => "one or more VMs",
        1 => $"'{vms[0]}'",
        _ => $"{string.Join(", ", vms.Take(vms.Count - 1).Select(v => $"'{v}'"))} and '{vms[^1]}'",
    };
}
