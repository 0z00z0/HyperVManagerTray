using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Issue #51: <c>NetworkActions</c>' four commands must answer in ONE vocabulary — ask with a modal,
/// tell with a balloon (see <c>docs/DISPLAY-VOCABULARY.md</c>).
///
/// <para><b>Why part of this is a source test.</b> The rule is about which CHANNEL a report goes down,
/// and <c>NetworkActions</c> is a UI class the test project deliberately cannot instantiate (it links
/// pure files individually and has no Windows App SDK runtime). The message tests below prove the text
/// is right, but they link <see cref="NetworkStatusUi"/> directly and would stay green if someone put
/// <c>NativeMethods.Warn(...)</c> back into a command — which is the whole defect this issue is about,
/// and exactly how it arrived in the first place (issue #34 lifted the bodies verbatim, carrying their
/// channels in unexamined). Same instrument, same caveat, as <see cref="VmConnectFlowSourceTests"/>: it
/// reads text, it is aimed at the realistic regression, not at an adversary.</para>
/// </summary>
public class NetworkActionsVocabularyTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    private static string Source(params string[] parts)
    {
        var path = Path.Combine([RepoRoot(), .. parts]);
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    // ── The channel rule ─────────────────────────────────────────────────────────

    /// <summary>
    /// No command in this class may REPORT through a modal. <c>Info</c>/<c>Warn</c>/<c>Error</c> are the
    /// three telling verbs on <c>NativeMethods</c>; all three are banned here. <c>Confirm</c> is not —
    /// it asks, and asking is what a modal is for.
    /// </summary>
    [Fact]
    public void NoCommand_ReportsThroughAModal()
    {
        var src = Source("UI", "NetworkActions.cs");

        var modalReport = new Regex(@"NativeMethods\.(Info|Warn|Error)\s*\(");
        var found       = modalReport.Matches(src).Select(m => m.Value).Distinct();

        Assert.False(modalReport.IsMatch(src),
            "NetworkActions reports an outcome through a blocking modal (" + string.Join(", ", found) + "), "
          + "while its sibling commands report through the tray balloon — so two adjacent buttons in one "
          + "Settings row answer in two vocabularies (issue #51). Reports go through _notify; only a "
          + "question that gates a side effect may be a modal. See docs/DISPLAY-VOCABULARY.md.");
    }

    /// <summary>
    /// The positive half. Without it, the test above passes trivially if the reports were simply DELETED
    /// rather than moved — a "fix" that makes every failure silent, which is a worse bug than the one
    /// being fixed and regresses issues #37/#40 outright. Each of the four commands' report messages must
    /// be reachable from here, and they must come from the pure class that holds them.
    /// </summary>
    [Theory]
    [InlineData("NetworkStatusUi.RepairNoSwitchesMessage")]
    [InlineData("NetworkStatusUi.RepairReportFor")]
    [InlineData("NetworkStatusUi.RepairUnexpectedErrorMessage")]
    [InlineData("NetworkStatusUi.AddRuleNoAdapterMessage")]
    [InlineData("NetworkStatusUi.AddRuleWirelessMessage")]
    [InlineData("NetworkStatusUi.AddRuleDuplicateMessage")]
    [InlineData("NetworkStatusUi.AddRuleSaveFailedMessage")]
    [InlineData("NetworkStatusUi.AddRuleUnexpectedErrorMessage")]
    [InlineData("NetworkStatusUi.ReCheckUnavailableMessage")]
    [InlineData("NetworkStatusUi.ReCheckUnexpectedErrorMessage")]
    [InlineData("NetworkStatusUi.OverrideUnexpectedErrorMessage")]
    public void EveryOutcome_StillReportsSomething(string message)
    {
        Assert.Contains(message, Source("UI", "NetworkActions.cs"));
    }

    /// <summary>
    /// Corollary 4 of <c>docs/DISPLAY-VOCABULARY.md</c>: <i>message text lives in a pure class, not at the
    /// call site</i>. Every <c>_notify</c> in this class must hand over a value that came from
    /// <see cref="NetworkStatusUi"/> (or a local holding one) — never an interpolated literal composed
    /// on the spot.
    ///
    /// <para>Four of the ten reports were doing exactly that, and they were the four unexpected-exception
    /// arms — the paths where the user most needs to know whether anything was written, and the only ones
    /// whose wording no test could reach. Every sibling failure states whether the host or the config was
    /// changed and is asserted on for it; these said nothing and could not be asserted on at all.</para>
    /// </summary>
    [Fact]
    public void NoReport_ComposesItsTextAtTheCallSite()
    {
        var src = Source("UI", "NetworkActions.cs");

        // A _notify(...) whose message argument is an interpolated string literal.
        var inlineText = new Regex(@"_notify\([^;]*?,\s*\$""", RegexOptions.Singleline);
        var found      = inlineText.Matches(src).Select(m => m.Value.Trim()).ToList();

        Assert.False(found.Count > 0,
            "NetworkActions composes report text at the call site (" + found.Count + " occurrence(s)), "
          + "instead of routing it through NetworkStatusUi. The claim a message makes is the testable "
          + "part, and re-deriving it per surface is how two surfaces drift into two vocabularies — "
          + "docs/DISPLAY-VOCABULARY.md corollary 4. First: " + (found.FirstOrDefault() ?? ""));
    }

    /// <summary>The ask stays an ask: the "add this rule?" confirmation must remain a blocking modal,
    /// because the user's answer decides whether a rule is written at all.</summary>
    [Fact]
    public void TheOneQuestion_StaysAModal()
    {
        Assert.Matches(new Regex(@"NativeMethods\.Confirm\s*\("), Source("UI", "NetworkActions.cs"));
    }

    /// <summary>The rule is only written down if it is actually written down. This is the file the class
    /// docs point the next author at.</summary>
    [Fact]
    public void TheRule_IsWrittenDown()
    {
        var doc = Source("docs", "DISPLAY-VOCABULARY.md");

        Assert.Contains("ask with a modal, tell with a balloon", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DISPLAY-VOCABULARY.md", Source("UI", "NetworkActions.cs"));
    }

    // ── The messages: only what was verified (issues #37/#40) ────────────────────

    private static NetworkStatusUi.RepairReport Report(params (string Sw, NetworkStatusUi.RepairStep Step)[] outcomes) =>
        NetworkStatusUi.RepairReportFor([.. outcomes.Select(o => new NetworkStatusUi.RepairStepOn(o.Sw, o.Step))]);

    /// <summary>
    /// "Nothing needed repairing" must not become "your network is fine". This command looks for exactly
    /// one fault — a duplicated host vNIC — so a clean result says only that. Overclaiming from a narrow
    /// check is the defect issue #37 exists to prevent.
    /// </summary>
    [Fact]
    public void RepairNothingToDo_ClaimsOnlyWhatWasChecked()
    {
        var report = Report(("Bridged", NetworkStatusUi.RepairStep.Inspected));

        Assert.False(report.IsError);
        Assert.Contains("duplicate host network adapter", report.Message);
        Assert.DoesNotContain("healthy", report.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fine", report.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>With no switch configured, the host was never inspected — so the message is about the
    /// config, and must not imply the host was checked and found clean.</summary>
    [Fact]
    public void RepairNoSwitches_DoesNotClaimTheHostWasChecked()
    {
        var msg = NetworkStatusUi.RepairNoSwitchesMessage();

        Assert.Contains("No bridged virtual switches are configured", msg);
        Assert.DoesNotContain("healthy", msg, StringComparison.OrdinalIgnoreCase);

        // The empty outcome list routes here rather than to a "nothing found on any switch" claim.
        Assert.Equal(msg, NetworkStatusUi.RepairReportFor([]).Message);
        Assert.False(NetworkStatusUi.RepairReportFor([]).IsError);
    }

    [Fact]
    public void Repaired_NamesEverySwitchItRepaired()
    {
        const NetworkStatusUi.RepairStep C = NetworkStatusUi.RepairStep.Collapsed;

        Assert.Contains("'Bridged'", Report(("Bridged", C)).Message);
        Assert.Contains("'Bridged' and 'Office'", Report(("Bridged", C), ("Office", C)).Message);
        Assert.Contains("'Bridged', 'Office' and 'Home'",
                        Report(("Bridged", C), ("Office", C), ("Home", C)).Message);
    }

    // ── Finding 1: a switch that was never found is never a clean bill of health ──

    /// <summary>
    /// THE regression test for the worst of the three. <c>HostVNicState.NoSwitch</c> — the configured
    /// switch does not exist on the host, because it was renamed, deleted, or mistyped in the rule — was
    /// counted as neither repaired nor error, so it fell through the call site's if/else into
    /// "no duplicate host network adapter was found on any configured switch": a positive assertion that
    /// every configured switch had been inspected, issued in the one case where none had been.
    ///
    /// <para>That is precisely the misconfiguration Repair exists to diagnose — the app's own docs call a
    /// misspelt switch the primary failure mode of the rules editor — and the user was told their host was
    /// fine. It logged nothing either, so switcher.log did not even contradict it.</para>
    /// </summary>
    [Fact]
    public void RepairSwitchNotFound_IsNeverReportedAsNothingToRepair()
    {
        var report = Report(("Bridged", NetworkStatusUi.RepairStep.SwitchNotFound));

        Assert.True(report.IsError,
            "A configured switch that does not exist on the host is the misconfiguration this command is "
          + "run to find. It must be reported as a fault, not passed over.");
        Assert.Contains("'Bridged'", report.Message);
        Assert.Contains("not found on the host", report.Message);
        Assert.Contains("not checked", report.Message);

        // The specific lie: it must not claim an inspection that never happened.
        Assert.DoesNotContain("Nothing needed repairing", report.Message);
        Assert.DoesNotContain("any configured switch", report.Message);
    }

    /// <summary>
    /// The mixed pass, which is where a priority-ordered report would quietly go wrong: one switch really
    /// was repaired and another does not exist. Both facts are the user's, and neither may speak for the
    /// other — the repair is reported (their link is coming back) AND the missing switch is still a fault.
    /// </summary>
    [Fact]
    public void RepairMixed_ReportsBothTheRepairAndTheMissingSwitch()
    {
        var report = Report(("Bridged", NetworkStatusUi.RepairStep.Collapsed),
                            ("Typo",    NetworkStatusUi.RepairStep.SwitchNotFound));

        Assert.True(report.IsError);
        Assert.Contains("'Bridged'", report.Message);
        Assert.Contains("collapsed back to one", report.Message);
        Assert.Contains("'Typo'", report.Message);
        Assert.Contains("not found on the host", report.Message);
    }

    /// <summary>An inspected switch alongside a missing one must not let the inspected one report for
    /// both — the "on any configured switch" phrasing is only true when every switch was found.</summary>
    [Fact]
    public void RepairInspectedPlusNotFound_DoesNotClaimEverySwitchWasChecked()
    {
        var report = Report(("Bridged", NetworkStatusUi.RepairStep.Inspected),
                            ("Typo",    NetworkStatusUi.RepairStep.SwitchNotFound));

        Assert.True(report.IsError);
        Assert.DoesNotContain("Nothing needed repairing", report.Message);
        Assert.Contains("'Typo'", report.Message);
    }

    // ── Finding 2: "nothing was changed" only where that is true ─────────────────

    /// <summary>
    /// A repair failure is announced as an error — but it must NOT claim nothing changed.
    /// <c>HostVNicState.Error</c> comes from a catch wrapping a loop that has already mutated the host:
    /// <c>RemoveExtraInternalPorts</c> removes the extras one at a time, so a throw on the second of three
    /// leaves one host vNIC already gone. The old wording told the user nothing had changed while their
    /// host vNIC set had been altered mid-flight.
    /// </summary>
    [Fact]
    public void RepairFailed_DoesNotClaimNothingWasChanged()
    {
        var report = Report(("Bridged", NetworkStatusUi.RepairStep.Failed));

        Assert.True(report.IsError);
        Assert.Contains("Could not repair", report.Message);
        Assert.Contains("switcher.log", report.Message);

        Assert.DoesNotContain("Nothing was changed", report.Message);
        Assert.Contains("may have been partly changed", report.Message);
    }

    /// <summary>The unexpected-exception arm of the loop itself: switches earlier in the loop may already
    /// have been repaired, so it too must not claim nothing changed.</summary>
    [Fact]
    public void RepairUnexpectedError_DoesNotClaimNothingWasChanged()
    {
        var msg = NetworkStatusUi.RepairUnexpectedErrorMessage("RPC server unavailable");

        Assert.Contains("RPC server unavailable", msg);
        Assert.DoesNotContain("Nothing was changed", msg);
        Assert.Contains("may", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ── Finding 3: a collapse is only reported when a collapse happened ──────────

    /// <summary>
    /// <c>Reshared</c> ADDS a missing host vNIC; <c>Repaired</c> REMOVES duplicate ones. They are opposite
    /// repairs, and both were fed to one "a duplicate host network adapter was collapsed back to one"
    /// sentence — so for the Reshared half the app described an action it had not taken, in the one
    /// message the user gets about what happened to their host networking.
    /// </summary>
    [Fact]
    public void RepairShareRestored_IsNotDescribedAsACollapse()
    {
        var report = Report(("Bridged", NetworkStatusUi.RepairStep.ShareRestored));

        Assert.False(report.IsError);
        Assert.Contains("'Bridged'", report.Message);
        Assert.Contains("added back", report.Message);

        Assert.DoesNotContain("collapsed", report.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("duplicate", report.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A collapse is described as a collapse, and never as an addition — the same guard from the
    /// other side, so the two clauses cannot be swapped without a test noticing.</summary>
    [Fact]
    public void RepairCollapsed_IsNotDescribedAsAnAddition()
    {
        var report = Report(("Bridged", NetworkStatusUi.RepairStep.Collapsed));

        Assert.False(report.IsError);
        Assert.Contains("duplicate host network adapter", report.Message);
        Assert.Contains("collapsed back to one", report.Message);
        Assert.DoesNotContain("added back", report.Message);
    }

    /// <summary>Both repairs in one pass: each switch is named in its own clause, so neither is described
    /// as the other.</summary>
    [Fact]
    public void RepairBothKinds_DescribesEachSwitchWithItsOwnRepair()
    {
        var report = Report(("Dup",     NetworkStatusUi.RepairStep.Collapsed),
                            ("Unshared", NetworkStatusUi.RepairStep.ShareRestored));

        Assert.False(report.IsError);

        var collapseAt = report.Message.IndexOf("collapsed back to one", StringComparison.Ordinal);
        var addAt      = report.Message.IndexOf("added back", StringComparison.Ordinal);
        Assert.True(collapseAt >= 0 && addAt >= 0, "Both repairs must be reported: " + report.Message);

        // 'Dup' belongs to the collapse clause, 'Unshared' to the add-back clause.
        Assert.True(report.Message.IndexOf("'Dup'", StringComparison.Ordinal) < collapseAt);
        Assert.True(report.Message.IndexOf("'Unshared'", StringComparison.Ordinal) is var u && u > collapseAt && u < addAt);
    }

    /// <summary>
    /// The enum is enumerated, so a future <c>HostVNicState</c> cannot be added and quietly render as a
    /// success — the same technique as <c>NetworkStatusUiTests.IconFor_OnlyAppliedEverRendersAsSuccess</c>
    /// and <c>VmConnectFlowTests.RunAsync_OnlyAnUnclaimedOrConfirmedBindIsSilent</c>. Only the two states
    /// that VERIFIED a repair, and the one that verified there was nothing to do, may be non-errors.
    /// </summary>
    [Theory]
    [InlineData(NetworkStatusUi.RepairStep.Inspected,      false)]
    [InlineData(NetworkStatusUi.RepairStep.Collapsed,      false)]
    [InlineData(NetworkStatusUi.RepairStep.ShareRestored,  false)]
    [InlineData(NetworkStatusUi.RepairStep.SwitchNotFound, true)]
    [InlineData(NetworkStatusUi.RepairStep.Failed,         true)]
    public void RepairReport_OnlyVerifiedOutcomesAreNonErrors(NetworkStatusUi.RepairStep step, bool expectError)
    {
        var report = Report(("Bridged", step));

        Assert.Equal(expectError, report.IsError);
        Assert.False(string.IsNullOrWhiteSpace(report.Message));
    }

    /// <summary>
    /// The pinned noun is pluralised, not concatenated: "virtual switches 'A' and 'B'", never "virtual
    /// switch 'A' and 'B'". Multi-switch reports are the ones a mixed pass produces, so they are exactly
    /// the ones read while something is actually wrong.
    /// </summary>
    [Theory]
    [InlineData(NetworkStatusUi.RepairStep.Inspected)]
    [InlineData(NetworkStatusUi.RepairStep.Collapsed)]
    [InlineData(NetworkStatusUi.RepairStep.SwitchNotFound)]
    [InlineData(NetworkStatusUi.RepairStep.Failed)]
    public void RepairReport_PluralisesThePinnedNoun(NetworkStatusUi.RepairStep step)
    {
        // Case-insensitive: the not-found clause opens a sentence, so its noun is capitalised.
        var msg = Report(("Bridged", step), ("Office", step)).Message;

        Assert.Contains("virtual switches 'Bridged' and 'Office'", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("virtual switch 'Bridged' and", msg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every step must produce a message that actually names the switch it is about. A clause that
    /// forgot its switch name would leave the user with a fault they cannot locate.</summary>
    [Theory]
    [InlineData(NetworkStatusUi.RepairStep.Inspected)]
    [InlineData(NetworkStatusUi.RepairStep.Collapsed)]
    [InlineData(NetworkStatusUi.RepairStep.ShareRestored)]
    [InlineData(NetworkStatusUi.RepairStep.SwitchNotFound)]
    [InlineData(NetworkStatusUi.RepairStep.Failed)]
    public void RepairReport_AlwaysNamesTheSwitch(NetworkStatusUi.RepairStep step)
    {
        Assert.Contains("'Bridged'", Report(("Bridged", step)).Message);
    }

    /// <summary>
    /// The Wi-Fi rejection was shortened for the balloon (which truncates), so this pins the half most
    /// easily lost in an edit: that NO RULE WAS ADDED. A rejection that dropped it would read as a rule
    /// the user still has — and they would go looking for why it never bridges (issue #29, finding 5).
    /// </summary>
    [Fact]
    public void AddRuleWireless_StillSaysNoRuleWasAdded()
    {
        var msg = NetworkStatusUi.AddRuleWirelessMessage("Intel(R) Wi-Fi 6 AX201 160MHz");

        Assert.Contains("Intel(R) Wi-Fi 6 AX201 160MHz", msg);
        Assert.Contains("no rule was added", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wired", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddRuleDuplicate_NamesTheRuleToEdit()
    {
        var msg = NetworkStatusUi.AddRuleDuplicateMessage("Office LAN");

        Assert.Contains("\"Office LAN\"", msg);
        Assert.Contains("no rule was added", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddRuleSaveFailed_CarriesTheErrorAndSaysNothingChanged()
    {
        var msg = NetworkStatusUi.AddRuleSaveFailedMessage("access denied");

        Assert.Contains("access denied", msg);
        Assert.Contains("Nothing was changed", msg);
    }

    [Fact]
    public void AddRuleNoAdapter_SaysWhyItCouldNotProceed()
    {
        Assert.Contains("IPv4", NetworkStatusUi.AddRuleNoAdapterMessage());
    }

    // ── The pinned vocabulary (issue #42 / docs/STYLE.md) ────────────────────────

    /// <summary>
    /// Every balloon this class can raise, each as it is actually rendered. The single list the two
    /// invariants below both walk — a message added to one and not the other is the drift they exist to
    /// catch.
    /// </summary>
    public static TheoryData<string, string> EveryBalloon()
    {
        var data = new TheoryData<string, string>();
        void Add(string name, string message) => data.Add(name, message);

        Add(nameof(NetworkStatusUi.RepairNoSwitchesMessage),  NetworkStatusUi.RepairNoSwitchesMessage());
        Add("RepairReportFor(Inspected)",     Report(("Bridged", NetworkStatusUi.RepairStep.Inspected)).Message);
        Add("RepairReportFor(Collapsed)",     Report(("Bridged", NetworkStatusUi.RepairStep.Collapsed)).Message);
        Add("RepairReportFor(ShareRestored)", Report(("Bridged", NetworkStatusUi.RepairStep.ShareRestored)).Message);
        Add("RepairReportFor(SwitchNotFound)",Report(("Bridged", NetworkStatusUi.RepairStep.SwitchNotFound)).Message);
        Add("RepairReportFor(Failed)",        Report(("Bridged", NetworkStatusUi.RepairStep.Failed)).Message);
        Add(nameof(NetworkStatusUi.RepairUnexpectedErrorMessage),
            NetworkStatusUi.RepairUnexpectedErrorMessage("RPC server unavailable"));
        Add(nameof(NetworkStatusUi.AddRuleNoAdapterMessage),   NetworkStatusUi.AddRuleNoAdapterMessage());
        Add(nameof(NetworkStatusUi.AddRuleWirelessMessage),
            NetworkStatusUi.AddRuleWirelessMessage("Intel(R) Wi-Fi 6 AX201 160MHz"));
        Add(nameof(NetworkStatusUi.AddRuleDuplicateMessage),   NetworkStatusUi.AddRuleDuplicateMessage("Office LAN"));
        Add(nameof(NetworkStatusUi.AddRuleSaveFailedMessage),  NetworkStatusUi.AddRuleSaveFailedMessage("access denied"));
        Add(nameof(NetworkStatusUi.AddRuleUnexpectedErrorMessage),
            NetworkStatusUi.AddRuleUnexpectedErrorMessage("the file is locked"));
        Add(nameof(NetworkStatusUi.ReCheckUnavailableMessage), NetworkStatusUi.ReCheckUnavailableMessage());
        Add(nameof(NetworkStatusUi.ReCheckUnexpectedErrorMessage),
            NetworkStatusUi.ReCheckUnexpectedErrorMessage("WMI went away"));
        Add(nameof(NetworkStatusUi.OverrideUnexpectedErrorMessage),
            NetworkStatusUi.OverrideUnexpectedErrorMessage("vDev", "WMI went away"));
        return data;
    }

    /// <summary>
    /// <c>docs/STYLE.md:19</c> pins <b>"virtual switch"</b> (then "switch") on first mention, and this
    /// file's own <c>FailureMessage</c> carries the reasoning verbatim: each of these is read alone in a
    /// balloon, so each is a first mention — and the balloon's title is already "… — network", so a bare
    /// "switch" under a "network" heading is exactly the switch/network pair the pinned vocabulary keeps
    /// apart.
    ///
    /// <para><b>The gap this closes.</b> Issue #51's new balloons drifted straight off that rule — a bare
    /// "switch" in two of them and a third variant in another — while the tests asserted only the messages'
    /// CLAIMS and never their vocabulary. So the batch that wrote the vocabulary down broke it, and nothing
    /// failed. The claim tests above cannot see this; only this can.</para>
    ///
    /// <para>The regex is word-bounded, so "switcher.log" is not a match ("switcher" has no boundary after
    /// "switch") and neither is a quoted switch NAME, which the fixtures keep clear of the word.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryBalloon))]
    public void EveryBalloonMessage_SaysVirtualSwitchOnFirstMention(string name, string message)
    {
        var first = Regex.Match(message, @"\bswitch(es)?\b", RegexOptions.IgnoreCase);
        if (!first.Success) return;   // the message never mentions a switch — nothing to pin

        var preceding = message[..first.Index];
        Assert.True(preceding.EndsWith("virtual ", StringComparison.OrdinalIgnoreCase),
            $"{name} says a bare \"{first.Value}\" on first mention: \"{message}\"\n"
          + "docs/STYLE.md pins \"virtual switch\" (then \"switch\") on first mention, and every balloon is "
          + "read alone under a \"… — network\" title, so every balloon is a first mention. A bare \"switch\" "
          + "beside \"network\" is the exact pair issue #42's vocabulary exists to keep apart.");
    }

    /// <summary>
    /// A balloon truncates, so every one of these has to survive being read at a glance. This is a
    /// sanity bound, not a spec — the modal wording these replaced ran to ~230 characters across three
    /// paragraphs, which a balloon would have cut mid-sentence.
    ///
    /// <para>The bound is per-CLAUSE, not per-report: a repair pass over several switches composes one
    /// clause per outcome, and a genuinely mixed pass has more to say. Each fixture below is a single
    /// clause, which is the overwhelmingly common case (one bridged switch).</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryBalloon))]
    public void EveryBalloonMessage_FitsInABalloon(string name, string message)
    {
        Assert.False(string.IsNullOrWhiteSpace(message), $"{name} is empty.");
        Assert.True(message.Length <= 200, $"{name} is {message.Length} chars and will be truncated: \"{message}\"");
    }
}
