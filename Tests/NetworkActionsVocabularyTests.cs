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
    [InlineData("NetworkStatusUi.RepairedMessage")]
    [InlineData("NetworkStatusUi.RepairFailedMessage")]
    [InlineData("NetworkStatusUi.RepairNothingToDoMessage")]
    [InlineData("NetworkStatusUi.AddRuleNoAdapterMessage")]
    [InlineData("NetworkStatusUi.AddRuleWirelessMessage")]
    [InlineData("NetworkStatusUi.AddRuleDuplicateMessage")]
    [InlineData("NetworkStatusUi.AddRuleSaveFailedMessage")]
    public void EveryOutcome_StillReportsSomething(string message)
    {
        Assert.Contains(message, Source("UI", "NetworkActions.cs"));
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

    /// <summary>
    /// "Nothing needed repairing" must not become "your network is fine". This command looks for exactly
    /// one fault — a duplicated host vNIC — so a clean result says only that. Overclaiming from a narrow
    /// check is the defect issue #37 exists to prevent.
    /// </summary>
    [Fact]
    public void RepairNothingToDo_ClaimsOnlyWhatWasChecked()
    {
        var msg = NetworkStatusUi.RepairNothingToDoMessage();

        Assert.Contains("duplicate host network adapter", msg);
        Assert.DoesNotContain("healthy", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fine", msg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>With no switch configured, the host was never inspected — so the message is about the
    /// config, and must not imply the host was checked and found clean.</summary>
    [Fact]
    public void RepairNoSwitches_DoesNotClaimTheHostWasChecked()
    {
        var msg = NetworkStatusUi.RepairNoSwitchesMessage();

        Assert.Contains("No bridged switches are configured", msg);
        Assert.DoesNotContain("healthy", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repaired_NamesEverySwitchItRepaired()
    {
        Assert.Contains("'Bridged'", NetworkStatusUi.RepairedMessage(["Bridged"]));

        var two = NetworkStatusUi.RepairedMessage(["Bridged", "Office"]);
        Assert.Contains("'Bridged' and 'Office'", two);

        var three = NetworkStatusUi.RepairedMessage(["Bridged", "Office", "Home"]);
        Assert.Contains("'Bridged', 'Office' and 'Home'", three);
    }

    /// <summary>A repair failure is announced as an error and states that nothing changed — the user must
    /// not be left assuming a silent success.</summary>
    [Fact]
    public void RepairFailed_SaysNothingWasChanged()
    {
        var msg = NetworkStatusUi.RepairFailedMessage();

        Assert.Contains("Could not repair", msg);
        Assert.Contains("Nothing was changed", msg);
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

    /// <summary>
    /// A balloon truncates, so every one of these has to survive being read at a glance. This is a
    /// sanity bound, not a spec — the modal wording these replaced ran to ~230 characters across three
    /// paragraphs, which a balloon would have cut mid-sentence.
    /// </summary>
    [Fact]
    public void EveryBalloonMessage_FitsInABalloon()
    {
        string[] messages =
        [
            NetworkStatusUi.RepairNoSwitchesMessage(),
            NetworkStatusUi.RepairedMessage(["Bridged"]),
            NetworkStatusUi.RepairFailedMessage(),
            NetworkStatusUi.RepairNothingToDoMessage(),
            NetworkStatusUi.AddRuleNoAdapterMessage(),
            NetworkStatusUi.AddRuleWirelessMessage("Intel(R) Wi-Fi 6 AX201 160MHz"),
            NetworkStatusUi.AddRuleDuplicateMessage("Office LAN"),
            NetworkStatusUi.AddRuleSaveFailedMessage("access denied"),
        ];

        foreach (var m in messages)
        {
            Assert.False(string.IsNullOrWhiteSpace(m));
            Assert.True(m.Length <= 200, $"Balloon text is {m.Length} chars and will be truncated: \"{m}\"");
        }
    }
}
