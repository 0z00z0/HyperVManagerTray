using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;
using Xunit;
using static HyperVManagerTray.Helpers.NetworkStatusUi;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Pure outcome → UI decisions for the switch-apply path (issue #37).
///
/// <para>The app's status surfaces used to report the rules' INTENT: the tray icon was coloured from
/// the matched rule's switch, published unconditionally, so a failed bind showed a confident green
/// "bridged" while the VM was still on the old switch. These tests pin the rule that replaced it — <b>a
/// status surface must never claim a state the app hasn't confirmed</b> — and the load-bearing one is
/// <see cref="IconFor_OnlyAppliedEverRendersAsSuccess"/>, which enumerates the status enum rather than
/// listing cases, so a status added later cannot quietly default to reading as success.</para>
/// </summary>
public class NetworkStatusUiTests
{
    // ── FromBindOutcome: the bind result reaches the UI intact ───────────────────

    [Theory]
    [InlineData(SwitchBindOutcome.Bound,        BindStep.Succeeded)]  // a real rebind happened
    [InlineData(SwitchBindOutcome.AlreadyBound, BindStep.Succeeded)]  // already there — still a success
    [InlineData(SwitchBindOutcome.Failed,       BindStep.Failed)]
    public void FromBindOutcome_MapsEachOutcome(SwitchBindOutcome outcome, BindStep expected) =>
        Assert.Equal(expected, FromBindOutcome(outcome));

    // The whole defect in one assertion: a Failed bind must never become a successful step.
    [Fact]
    public void FromBindOutcome_FailedIsNeverASuccessfulStep() =>
        Assert.Equal(BindStep.Failed, FromBindOutcome(SwitchBindOutcome.Failed));

    // ── Classify: the two halves of an apply pass → one status ───────────────────

    [Theory]
    // Bind succeeded (or wasn't needed) and every VM reconnected → the only success.
    [InlineData(BindStep.Succeeded, 0, SwitchApplyStatus.Applied)]
    [InlineData(BindStep.NotNeeded, 0, SwitchApplyStatus.Applied)]
    // Bind failed → BindFailed, regardless of how the VM reconnects went (bind failure dominates:
    // if the switch isn't on the right adapter, the VMs are not on the intended network).
    [InlineData(BindStep.Failed,    0, SwitchApplyStatus.BindFailed)]
    [InlineData(BindStep.Failed,    1, SwitchApplyStatus.BindFailed)]
    [InlineData(BindStep.Failed,    5, SwitchApplyStatus.BindFailed)]
    // Bind fine, but a VM NIC didn't attach → VmConnectFailed.
    [InlineData(BindStep.Succeeded, 1, SwitchApplyStatus.VmConnectFailed)]
    [InlineData(BindStep.NotNeeded, 2, SwitchApplyStatus.VmConnectFailed)]
    public void Classify_CombinesBindAndVmOutcomes(BindStep bind, int failedVms, SwitchApplyStatus expected) =>
        Assert.Equal(expected, Classify(bind, failedVms));

    // No combination involving a failed bind or a failed VM may ever classify as Applied.
    [Fact]
    public void Classify_AnyFailureNeverClassifiesAsApplied()
    {
        foreach (var bind in Enum.GetValues<BindStep>())
            for (int failedVms = 0; failedVms <= 3; failedVms++)
            {
                bool anythingFailed = bind == BindStep.Failed || failedVms > 0;
                if (!anythingFailed) continue;

                Assert.NotEqual(SwitchApplyStatus.Applied, Classify(bind, failedVms));
            }
    }

    // ── IconFor: the invariant this whole issue exists to enforce ────────────────

    [Theory]
    // A CONFIRMED apply is the only thing that may show a success colour; the rules' intent
    // (bridgedTarget) then picks which one.
    [InlineData(SwitchApplyStatus.Applied,         true,  TrayIconState.Bridged)]
    [InlineData(SwitchApplyStatus.Applied,         false, TrayIconState.Fallback)]
    // A failed apply is red — never the optimistic target colour, whatever the rule intended.
    [InlineData(SwitchApplyStatus.BindFailed,      true,  TrayIconState.Failed)]
    [InlineData(SwitchApplyStatus.BindFailed,      false, TrayIconState.Failed)]
    [InlineData(SwitchApplyStatus.VmConnectFailed, true,  TrayIconState.Failed)]
    [InlineData(SwitchApplyStatus.VmConnectFailed, false, TrayIconState.Failed)]
    // Nothing applied yet → grey. Not a guess in either direction.
    [InlineData(SwitchApplyStatus.NotEvaluated,    true,  TrayIconState.Unknown)]
    [InlineData(SwitchApplyStatus.NotEvaluated,    false, TrayIconState.Unknown)]
    public void IconFor_RendersEachStatus(SwitchApplyStatus status, bool bridgedTarget, TrayIconState expected) =>
        Assert.Equal(expected, IconFor(status, bridgedTarget));

    /// <summary>
    /// THE regression test for issue #37. Enumerates every <see cref="SwitchApplyStatus"/> — including
    /// any added in future — and asserts that the two "all is well" colours are reachable ONLY from
    /// <see cref="SwitchApplyStatus.Applied"/>. A failed bind rendering as green/blue fails here, and so
    /// does a new status that someone slots into the success arm without thinking about it.
    /// </summary>
    [Fact]
    public void IconFor_OnlyAppliedEverRendersAsSuccess()
    {
        foreach (var status in Enum.GetValues<SwitchApplyStatus>())
            foreach (var bridgedTarget in new[] { true, false })
            {
                var icon = IconFor(status, bridgedTarget);
                bool looksLikeSuccess = icon is TrayIconState.Bridged or TrayIconState.Fallback;

                Assert.True(!looksLikeSuccess || status == SwitchApplyStatus.Applied,
                    $"{status} rendered as {icon}, which reads to the user as a working connection. " +
                    "Only a CONFIRMED Applied may show a success colour (issue #37).");
            }
    }

    // A failed bind specifically must not reach the green "bridged" icon — the exact symptom reported.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IconFor_FailedBindIsNeverBridged(bool bridgedTarget) =>
        Assert.NotEqual(TrayIconState.Bridged, IconFor(SwitchApplyStatus.BindFailed, bridgedTarget));

    // ── IsFailure ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SwitchApplyStatus.Applied,         false)]
    [InlineData(SwitchApplyStatus.NotEvaluated,    false)]  // unknown is not a failure — nothing to report
    [InlineData(SwitchApplyStatus.BindFailed,      true)]
    [InlineData(SwitchApplyStatus.VmConnectFailed, true)]
    public void IsFailure_IdentifiesReportableFailures(SwitchApplyStatus status, bool expected) =>
        Assert.Equal(expected, IsFailure(status));

    // ── Dashboard host card ──────────────────────────────────────────────────────

    // A confirmed apply shows the plain rule name; anything else says why it can't be trusted.
    [Fact]
    public void RuleRowText_AppliedShowsPlainRuleName() =>
        Assert.Equal("Office LAN", RuleRowText("Office LAN", SwitchApplyStatus.Applied));

    [Theory]
    [InlineData(SwitchApplyStatus.BindFailed,      "Office LAN — bind failed")]
    [InlineData(SwitchApplyStatus.VmConnectFailed, "Office LAN — VM connect failed")]
    [InlineData(SwitchApplyStatus.NotEvaluated,    "Office LAN — not applied yet")]
    public void RuleRowText_NonAppliedIsQualified(SwitchApplyStatus status, string expected) =>
        Assert.Equal(expected, RuleRowText("Office LAN", status));

    // The card must never render a bare rule name for a state that isn't confirmed — a bare name reads
    // as "this rule is in force".
    [Fact]
    public void RuleRowText_OnlyAppliedIsABareRuleName()
    {
        foreach (var status in Enum.GetValues<SwitchApplyStatus>())
        {
            var text = RuleRowText("Office LAN", status);
            if (status == SwitchApplyStatus.Applied) continue;

            Assert.NotEqual("Office LAN", text);
            Assert.Contains("Office LAN", text);   // still names the rule
        }
    }

    // ── Tray tooltip ─────────────────────────────────────────────────────────────

    [Fact]
    public void TooltipSwitchSuffix_AppliedAddsNothing() =>
        Assert.Equal("", TooltipSwitchSuffix(SwitchApplyStatus.Applied));

    [Theory]
    [InlineData(SwitchApplyStatus.BindFailed)]
    [InlineData(SwitchApplyStatus.VmConnectFailed)]
    [InlineData(SwitchApplyStatus.NotEvaluated)]
    public void TooltipSwitchSuffix_NonAppliedQualifiesTheSwitchRow(SwitchApplyStatus status) =>
        Assert.NotEqual("", TooltipSwitchSuffix(status));

    // ── Failure balloon ──────────────────────────────────────────────────────────

    /// <summary>
    /// An applied result, with the description and the interface alias deliberately DIFFERENT so a
    /// message that names the wrong one is caught. On a real host these two are never interchangeable:
    /// "Realtek USB GbE Family Controller" is the driver description (which two docked adapters can
    /// share), "Ethernet 5" is the OS alias the bind actually targets.
    /// </summary>
    private static MatchResult Result(
        SwitchApplyStatus status, IReadOnlyList<string>? failedVms = null, string rule = "Office LAN") =>
        new(rule, "Bridged", ["vDev"])
        {
            HostAdapterName          = "Realtek USB GbE Family Controller",
            HostAdapterInterfaceName = "Ethernet 5",
            ApplyStatus              = status,
            FailedVms                = failedVms ?? [],
        };

    [Fact]
    public void FailureMessage_BindFailedNamesSwitchAdapterAndLog()
    {
        var msg = FailureMessage(Result(SwitchApplyStatus.BindFailed));

        Assert.NotNull(msg);
        Assert.Contains("Bridged", msg);
        Assert.Contains("Ethernet 5", msg);
        Assert.Contains("switcher.log", msg);
    }

    /// <summary>
    /// The bind-failure balloon must name the adapter the bind TARGETED — the OS interface alias — not
    /// the driver description. Two adapters can share a description, so naming it sends the user to
    /// check a string that need not identify the one that failed; and this is the single message read
    /// when the network is down. The description-vs-alias split is what docs/STYLE.md pins.
    /// </summary>
    [Fact]
    public void FailureMessage_BindFailedNamesTheInterfaceAliasNotTheDescription()
    {
        var msg = FailureMessage(Result(SwitchApplyStatus.BindFailed))!;

        Assert.Contains("Ethernet 5", msg);
        Assert.DoesNotContain("Realtek USB GbE Family Controller", msg);
    }

    [Fact]
    public void FailureMessage_VmConnectFailedNamesTheFailedVms()
    {
        var msg = FailureMessage(Result(SwitchApplyStatus.VmConnectFailed, ["vDev", "vBuild"]));

        Assert.NotNull(msg);
        Assert.Contains("vDev", msg);
        Assert.Contains("vBuild", msg);
        Assert.Contains("switcher.log", msg);
    }

    // A VmConnectFailed with an empty list (shouldn't happen, but must not produce broken text).
    [Fact]
    public void FailureMessage_VmConnectFailedWithNoNamesStillReads()
    {
        var msg = FailureMessage(Result(SwitchApplyStatus.VmConnectFailed));

        Assert.NotNull(msg);
        Assert.DoesNotContain("''", msg);
        Assert.Contains("VMs", msg);
    }

    // Nothing to report when the apply landed or hasn't run — no balloon.
    [Theory]
    [InlineData(SwitchApplyStatus.Applied)]
    [InlineData(SwitchApplyStatus.NotEvaluated)]
    public void FailureMessage_NonFailureHasNoMessage(SwitchApplyStatus status) =>
        Assert.Null(FailureMessage(Result(status)));

    // Every status IsFailure reports must have a message, and no other status may have one — the balloon
    // and the red rendering must never disagree about whether something failed.
    [Fact]
    public void FailureMessage_ExistsForExactlyTheFailureStatuses()
    {
        foreach (var status in Enum.GetValues<SwitchApplyStatus>())
        {
            var msg = FailureMessage(Result(status, ["vDev"]));
            Assert.Equal(IsFailure(status), msg is not null);
        }
    }

    // ── "Re-check network now" — always answers (recommendation 5) ────────────────

    [Fact]
    public void ReCheckMessage_SuccessReportsRuleAndSwitch()
    {
        var msg = ReCheckMessage(Result(SwitchApplyStatus.Applied));

        Assert.Contains("Office LAN", msg);
        Assert.Contains("Bridged", msg);
    }

    [Fact]
    public void ReCheckMessage_FailureReportsBothTheResultAndTheFailure()
    {
        var msg = ReCheckMessage(Result(SwitchApplyStatus.BindFailed));

        Assert.Contains("Office LAN", msg);
        Assert.Contains("bind", msg);
        Assert.Contains("switcher.log", msg);
    }

    // The command must never return an empty/blank answer for ANY status — silence is the bug.
    [Fact]
    public void ReCheckMessage_NeverAnswersWithNothing()
    {
        foreach (var status in Enum.GetValues<SwitchApplyStatus>())
        {
            var msg = ReCheckMessage(Result(status, ["vDev"]));
            Assert.False(string.IsNullOrWhiteSpace(msg), $"Re-check said nothing for {status}.");
        }
    }

    // A non-confirmed re-check must not read as a clean success.
    [Fact]
    public void ReCheckMessage_UnconfirmedResultSaysSo()
    {
        var msg = ReCheckMessage(Result(SwitchApplyStatus.NotEvaluated));

        Assert.Contains("could not be confirmed", msg);
    }

    // ── "Override VM switch" — confirms, and states its transience ────────────────

    [Fact]
    public void OverrideAppliedMessage_ConfirmsTheAction()
    {
        var msg = OverrideAppliedMessage("vDev", "Default Switch");

        Assert.Contains("vDev", msg);
        Assert.Contains("Default Switch", msg);
    }

    /// <summary>
    /// The override is undone by the next network change. That lifespan was documented NOWHERE in the UI
    /// before issue #37 — the user had no way to know the override was temporary. This asserts the
    /// confirmation says so.
    /// </summary>
    [Fact]
    public void OverrideAppliedMessage_StatesThatTheNextNetworkChangeRevertsIt()
    {
        var msg = OverrideAppliedMessage("vDev", "Default Switch");

        Assert.Contains("temporary", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("next network change", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverrideFailedMessage_SaysNothingChanged()
    {
        var msg = OverrideFailedMessage("vDev", "Default Switch");

        Assert.Contains("vDev", msg);
        Assert.Contains("Nothing was changed", msg);
    }

    // The silent no-op path: a VM this app does not manage now explains itself.
    //
    // Asserts "managed", not "config.json" (issue #42): the pinned vocabulary makes "managed VM" the
    // user-facing phrase for a VM in config.json, and names the FILE only when the user is being
    // pointed at the file itself — which here they are not, since the fix is to tick the VM in the
    // tray's Manage VMs list. The message must still name the VM and disclaim any change; that is what
    // this test was always really guarding.
    [Fact]
    public void OverrideNotConfiguredMessage_ExplainsTheNoOp()
    {
        var msg = OverrideNotConfiguredMessage("vGhost");

        Assert.Contains("vGhost", msg);
        Assert.Contains("managed", msg);
        Assert.Contains("Nothing was changed", msg);
    }

    // ── MatchResult carries the outcome, and defaults to claiming nothing ─────────

    /// <summary>
    /// A MatchResult straight out of rule evaluation describes INTENT only — nothing has been applied
    /// yet. Its default must therefore be <see cref="SwitchApplyStatus.NotEvaluated"/>: if the default
    /// were Applied, every not-yet-applied result would render as a working connection, which is the
    /// original bug with extra steps. Only NetworkMonitor's apply path may set anything else.
    /// </summary>
    [Fact]
    public void MatchResult_DefaultsToNotEvaluatedAndNoFailedVms()
    {
        var result = new MatchResult("Office LAN", "Bridged", ["vDev"]);

        Assert.Equal(SwitchApplyStatus.NotEvaluated, result.ApplyStatus);
        Assert.Empty(result.FailedVms);
        // …and that default must render as grey, never as a success colour.
        Assert.Equal(TrayIconState.Unknown, IconFor(result.ApplyStatus, bridgedTarget: true));
    }

    /// <summary>
    /// The outcome must survive the <c>with</c>-expression stamping that <c>NetworkMonitor.ApplyAsync</c>
    /// uses to attach it to the evaluated result — the whole plumbing hinges on this.
    /// </summary>
    [Fact]
    public void MatchResult_CarriesTheStampedOutcome()
    {
        var evaluated = new MatchResult("Office LAN", "Bridged", ["vDev"]) { HostAdapterName = "Realtek USB GbE" };

        var applied = evaluated with { ApplyStatus = SwitchApplyStatus.BindFailed, FailedVms = ["vDev"] };

        Assert.Equal(SwitchApplyStatus.BindFailed, applied.ApplyStatus);
        Assert.Equal(["vDev"], applied.FailedVms);
        Assert.Equal("Realtek USB GbE", applied.HostAdapterName);   // the evaluated fields survive
        Assert.Equal(TrayIconState.Failed, IconFor(applied.ApplyStatus, bridgedTarget: true));
    }

    // ── The skip guard: when may a confirmed outcome be carried forward? ──────────
    //
    // NetworkMonitor skips the apply pass — and carries the previous ApplyStatus forward — only when
    // MatchResult.ConfirmsSameOutcomeFor holds. The predicate it replaced compared switch + target VMs
    // ONLY, which was unsound in two opposite directions; both are pinned below.

    /// <summary>An applied outcome for a rule on a given host adapter — the "last confirmed" state.</summary>
    private static MatchResult Applied(string rule, string adapter, string sw = "Bridged", string vm = "vDev") =>
        new(rule, sw, [vm])
        {
            HostAdapterInterfaceName = adapter,
            ApplyStatus              = SwitchApplyStatus.Applied,
        };

    /// <summary>A freshly evaluated result (no outcome of its own yet) — what a NetworkChange produces.</summary>
    private static MatchResult Evaluated(string rule, string adapter, string sw = "Bridged", string vm = "vDev") =>
        new(rule, sw, [vm]) { HostAdapterInterfaceName = adapter };

    // The baseline: genuinely nothing changed, so the pass is skippable.
    [Fact]
    public void ConfirmsSameOutcomeFor_TrueWhenTheConfirmedApplyStillDescribesTheNewResult() =>
        Assert.True(Applied("Office LAN", "Ethernet 5")
            .ConfirmsSameOutcomeFor(Evaluated("Office LAN", "Ethernet 5")));

    /// <summary>
    /// FALSE SUCCESS. Two rules — an office dock and a home dock — name the SAME switch and the SAME
    /// VMs but different host adapters. Moving between docks must re-apply: the switch has to be
    /// re-bound to the adapter that is actually present. The old switch+VMs guard passed here, skipped
    /// the rebind, left the switch on the absent office adapter, and carried "Applied" forward — a green
    /// icon over a VM with no network. The outcome depends on the adapter, so the guard must test it.
    /// </summary>
    [Fact]
    public void ConfirmsSameOutcomeFor_FalseWhenTheHostAdapterChanged() =>
        Assert.False(Applied("Office dock", "Ethernet 5")
            .ConfirmsSameOutcomeFor(Evaluated("Home dock", "Ethernet 7")));

    // The adapter alone is enough to break it, even for the very same rule (dock re-enumerated to a new
    // alias): this must not depend on the rule name also having changed.
    [Fact]
    public void ConfirmsSameOutcomeFor_FalseWhenOnlyTheHostAdapterChanged() =>
        Assert.False(Applied("Office LAN", "Ethernet 5")
            .ConfirmsSameOutcomeFor(Evaluated("Office LAN", "Ethernet 7")));

    // A different rule is a different intent even when it resolves identically — and the skip path runs
    // none of the per-rule side effects (autoStart most concretely), so it must not be taken.
    [Fact]
    public void ConfirmsSameOutcomeFor_FalseWhenOnlyTheRuleChanged() =>
        Assert.False(Applied("Office dock", "Ethernet 5")
            .ConfirmsSameOutcomeFor(Evaluated("Home dock", "Ethernet 5")));

    /// <summary>
    /// STUCK FAILURE. A failed bind clears the skip-cache specifically so the next NetworkChange retries
    /// it — but if a failed status satisfies the guard, the apply pass is never re-entered, the cleared
    /// cache is never read, and the red icon is pinned for the session even after the host heals. A
    /// failure is a snapshot of ONE attempt, never a durable truth: it can never authorise a skip.
    /// </summary>
    [Theory]
    [InlineData(SwitchApplyStatus.BindFailed)]
    [InlineData(SwitchApplyStatus.VmConnectFailed)]
    [InlineData(SwitchApplyStatus.NotEvaluated)]
    public void ConfirmsSameOutcomeFor_FalseForAnyUnconfirmedStatus_SoTheRetryStaysReachable(
        SwitchApplyStatus status)
    {
        var last = Applied("Office LAN", "Ethernet 5") with { ApplyStatus = status };

        Assert.False(last.ConfirmsSameOutcomeFor(Evaluated("Office LAN", "Ethernet 5")),
            $"{status} authorised a skip — the apply pass would never be re-entered and the retry is unreachable.");
    }

    /// <summary>
    /// Enumerates the enum rather than listing cases, mirroring
    /// <see cref="IconFor_OnlyAppliedEverRendersAsSuccess"/>: a status added later must not quietly
    /// become skippable. Only a confirmed Applied may ever be carried forward.
    /// </summary>
    [Fact]
    public void ConfirmsSameOutcomeFor_OnlyAppliedIsEverCarriedForward()
    {
        foreach (var status in Enum.GetValues<SwitchApplyStatus>())
        {
            var last = Applied("Office LAN", "Ethernet 5") with { ApplyStatus = status };
            Assert.Equal(status == SwitchApplyStatus.Applied,
                         last.ConfirmsSameOutcomeFor(Evaluated("Office LAN", "Ethernet 5")));
        }
    }

    [Fact]
    public void ConfirmsSameOutcomeFor_FalseWhenTheSwitchChanged() =>
        Assert.False(Applied("Office LAN", "Ethernet 5", sw: "Bridged")
            .ConfirmsSameOutcomeFor(Evaluated("Office LAN", "Ethernet 5", sw: "Default Switch")));

    [Fact]
    public void ConfirmsSameOutcomeFor_FalseWhenTheTargetVmsChanged() =>
        Assert.False(Applied("Office LAN", "Ethernet 5", vm: "vDev")
            .ConfirmsSameOutcomeFor(Evaluated("Office LAN", "Ethernet 5", vm: "vBuild")));

    /// <summary>
    /// Casing must NOT force a re-apply. Hyper-V switch names, Windows interface aliases and VM names
    /// are all case-insensitive, so an ordinal guard would rebind — a real VM network drop — over
    /// nothing but a casing difference between the config and the host.
    /// </summary>
    [Fact]
    public void ConfirmsSameOutcomeFor_IgnoresCasingOnEveryField() =>
        Assert.True(Applied("Office LAN", "Ethernet 5", sw: "Bridged", vm: "DevBox")
            .ConfirmsSameOutcomeFor(Evaluated("OFFICE lan", "ETHERNET 5", sw: "BRIDGED", vm: "devbox")));
}
