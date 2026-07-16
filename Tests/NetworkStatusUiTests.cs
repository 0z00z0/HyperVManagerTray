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

    [Fact]
    public void FailureMessage_BindFailedNamesSwitchAdapterAndLog()
    {
        var msg = FailureMessage(SwitchApplyStatus.BindFailed, "Bridged", "Realtek USB GbE", []);

        Assert.NotNull(msg);
        Assert.Contains("Bridged", msg);
        Assert.Contains("Realtek USB GbE", msg);
        Assert.Contains("switcher.log", msg);
    }

    [Fact]
    public void FailureMessage_VmConnectFailedNamesTheFailedVms()
    {
        var msg = FailureMessage(SwitchApplyStatus.VmConnectFailed, "Bridged", "Realtek USB GbE", ["vDev", "vBuild"]);

        Assert.NotNull(msg);
        Assert.Contains("vDev", msg);
        Assert.Contains("vBuild", msg);
        Assert.Contains("switcher.log", msg);
    }

    // A VmConnectFailed with an empty list (shouldn't happen, but must not produce broken text).
    [Fact]
    public void FailureMessage_VmConnectFailedWithNoNamesStillReads()
    {
        var msg = FailureMessage(SwitchApplyStatus.VmConnectFailed, "Bridged", "Realtek USB GbE", []);

        Assert.NotNull(msg);
        Assert.DoesNotContain("''", msg);
        Assert.Contains("VMs", msg);
    }

    // Nothing to report when the apply landed or hasn't run — no balloon.
    [Theory]
    [InlineData(SwitchApplyStatus.Applied)]
    [InlineData(SwitchApplyStatus.NotEvaluated)]
    public void FailureMessage_NonFailureHasNoMessage(SwitchApplyStatus status) =>
        Assert.Null(FailureMessage(status, "Bridged", "Realtek USB GbE", []));

    // Every status IsFailure reports must have a message, and no other status may have one — the balloon
    // and the red rendering must never disagree about whether something failed.
    [Fact]
    public void FailureMessage_ExistsForExactlyTheFailureStatuses()
    {
        foreach (var status in Enum.GetValues<SwitchApplyStatus>())
        {
            var msg = FailureMessage(status, "Bridged", "Realtek USB GbE", ["vDev"]);
            Assert.Equal(IsFailure(status), msg is not null);
        }
    }

    // ── "Re-check network now" — always answers (recommendation 5) ────────────────

    [Fact]
    public void ReCheckMessage_SuccessReportsRuleAndSwitch()
    {
        var msg = ReCheckMessage("Office LAN", "Bridged", SwitchApplyStatus.Applied, "Realtek USB GbE", []);

        Assert.Contains("Office LAN", msg);
        Assert.Contains("Bridged", msg);
    }

    [Fact]
    public void ReCheckMessage_FailureReportsBothTheResultAndTheFailure()
    {
        var msg = ReCheckMessage("Office LAN", "Bridged", SwitchApplyStatus.BindFailed, "Realtek USB GbE", []);

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
            var msg = ReCheckMessage("Office LAN", "Bridged", status, "Realtek USB GbE", ["vDev"]);
            Assert.False(string.IsNullOrWhiteSpace(msg), $"Re-check said nothing for {status}.");
        }
    }

    // A non-confirmed re-check must not read as a clean success.
    [Fact]
    public void ReCheckMessage_UnconfirmedResultSaysSo()
    {
        var msg = ReCheckMessage("Office LAN", "Bridged", SwitchApplyStatus.NotEvaluated, "Realtek USB GbE", []);

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
}
