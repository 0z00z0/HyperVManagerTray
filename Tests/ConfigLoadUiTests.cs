using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Tests for the pure load-outcome → UI contract (issue #39).
///
/// <para>The load-bearing test here is <see cref="FailedLoadCanNeverBeReportedAsSuccess"/>. Everything
/// else is wording and arithmetic; that one is the invariant the issue exists to establish — a config
/// load that did not happen must not be presentable to the user as one that did. It is the config-side
/// twin of <c>NetworkStatusUiTests.IconFor_OnlyAppliedEverRendersAsSuccess</c>.</para>
/// </summary>
public class ConfigLoadUiTests
{
    // ── The invariant ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every way an outcome can fail — a parse error, an IO error, a blank error string, and the
    /// default-constructed record — must produce no success sentence and must NOT authorise a caller to
    /// re-render from ConfigManager.Current (which, after a failure, still holds the OLD config).
    /// If this test ever fails, "Reload config from disk" has regained the ability to tell someone their
    /// broken edit was applied.
    /// </summary>
    [Fact]
    public void FailedLoadCanNeverBeReportedAsSuccess()
    {
        ConfigLoadOutcome[] failures =
        [
            ConfigLoadOutcome.Failure("'}' is invalid after a value. LineNumber: 13 | BytePositionInLine: 4."),
            ConfigLoadOutcome.Failure("Could not find file 'C:\\nope\\config.json'."),
            ConfigLoadOutcome.Failure(""),
            ConfigLoadOutcome.Failure("   "),
            new(),   // default — Succeeded must default to false, not to a hopeful true
        ];

        foreach (var failure in failures)
        {
            Assert.False(failure.Succeeded);
            Assert.Null(ConfigLoadUi.SuccessMessage(failure));
            Assert.False(ConfigLoadUi.ShouldRebuildFromConfig(failure));
            Assert.NotNull(ConfigLoadUi.FailureMessage(failure));
            Assert.NotNull(ConfigLoadUi.BalloonMessage(failure));
        }
    }

    /// <summary>A null outcome must fail closed too, not throw and not read as success.</summary>
    [Fact]
    public void NullOutcomeIsNotSuccess()
    {
        Assert.Null(ConfigLoadUi.SuccessMessage(null!));
        Assert.Null(ConfigLoadUi.FailureMessage(null!));
        Assert.Null(ConfigLoadUi.BalloonMessage(null!));
        Assert.False(ConfigLoadUi.ShouldRebuildFromConfig(null!));
    }

    /// <summary>The mirror image: a success has no failure sentence, and does authorise a rebuild.</summary>
    [Fact]
    public void SuccessHasNoFailureMessage()
    {
        var ok = ConfigLoadOutcome.Success(3, 2);

        Assert.True(ConfigLoadUi.ShouldRebuildFromConfig(ok));
        Assert.Null(ConfigLoadUi.FailureMessage(ok));
        Assert.Null(ConfigLoadUi.BalloonMessage(ok));
        Assert.NotNull(ConfigLoadUi.SuccessMessage(ok));
    }

    // ── Wording ───────────────────────────────────────────────────────────────

    [Fact]
    public void SuccessMessageNamesWhatWasLoaded()
        => Assert.Equal("Reloaded — 3 rules, 2 VMs", ConfigLoadUi.SuccessMessage(ConfigLoadOutcome.Success(3, 2)));

    [Theory]
    [InlineData(0, 0, "Reloaded — 0 rules, 0 VMs")]
    [InlineData(1, 1, "Reloaded — 1 rule, 1 VM")]
    [InlineData(2, 1, "Reloaded — 2 rules, 1 VM")]
    public void SuccessMessagePluralisesProperly(int rules, int vms, string expected)
        => Assert.Equal(expected, ConfigLoadUi.SuccessMessage(ConfigLoadOutcome.Success(rules, vms)));

    /// <summary>
    /// The failure dialog must carry BOTH halves of the truth: what went wrong (the parse message,
    /// which is where the line number lives), and that the app is still running on the old settings.
    /// Dropping the second half leaves the user unsure whether the app now has no config at all.
    /// </summary>
    [Fact]
    public void FailureMessageCarriesTheParseErrorAndSaysOldSettingsAreStillActive()
    {
        var message = ConfigLoadUi.FailureMessage(
            ConfigLoadOutcome.Failure("'}' is invalid after a value. LineNumber: 13 | BytePositionInLine: 4."))!;

        Assert.Contains("LineNumber: 13", message);
        Assert.Contains("still active", message);
        Assert.Contains("nothing was reloaded", message);
    }

    /// <summary>A blank error must not leave a hole where the explanation should be.</summary>
    [Fact]
    public void FailureMessageFallsBackWhenTheErrorIsBlank()
    {
        var message = ConfigLoadUi.FailureMessage(ConfigLoadOutcome.Failure("  "))!;

        Assert.Contains("could not be parsed", message);
        Assert.DoesNotContain("\n\n\n", message);   // no empty slot where the detail belongs
    }

    /// <summary>
    /// The balloon leads with the consequence ("keeping the previous settings"), and takes only the
    /// first line of the error — a multi-line IO exception would otherwise blow the Win32 balloon limit
    /// and push the consequence off the end.
    /// </summary>
    [Fact]
    public void BalloonMessageIsShortAndLeadsWithTheConsequence()
    {
        var message = ConfigLoadUi.BalloonMessage(
            ConfigLoadOutcome.Failure("Line one of the error.\r\nLine two.\r\nLine three."))!;

        Assert.Contains("keeping the previous settings", message);
        Assert.Contains("Line one of the error.", message);
        Assert.DoesNotContain("Line two.", message);
        Assert.DoesNotContain("Line three.", message);
    }
}
