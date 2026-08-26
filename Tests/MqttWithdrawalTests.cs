using HyperVManagerTray.Helpers;
using Xunit;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Taking this host's published device off the broker (issue #75). Two things are being guarded, and the
/// second is the dangerous one: that the device goes when publishing is switched off, and that it goes at
/// no other time — the retained document is the receiving end's whole registry entry, and re-announcing
/// it never restores the names, entity ids and areas a user chose there.
/// </summary>
public class MqttWithdrawalTests
{
    private static MqttSettings Settings(bool enabled, string host = "broker.lan") =>
        new() { Enabled = enabled, Host = host };

    // ── When the device is withdrawn by itself ──────────────────────────────────

    /// <summary>The whole point of the automatic half: publishing switched off used to leave the retained
    /// document standing, and the device read offline at the receiving end for ever.</summary>
    [Fact]
    public void OnDisable_IsTrueWhenPublishingIsSwitchedOff() =>
        Assert.True(MqttWithdrawal.OnDisable(Settings(enabled: true), Settings(enabled: false)));

    [Theory]
    [InlineData(true,  true)]    // an unrelated write while publishing stays on
    [InlineData(false, false)]   // an unrelated write while publishing stays off
    [InlineData(false, true)]    // switched ON — the opposite transition
    public void OnDisable_IsFalseWhenPublishingDidNotGoOff(bool before, bool after) =>
        Assert.False(MqttWithdrawal.OnDisable(Settings(before), Settings(after)));

    /// <summary>
    /// The trap this rule exists for. Blanking the broker host also stops the connection, so a rule
    /// written over "should the connection still run" would fire here — and deleting the receiving end's
    /// registry entry over one keystroke in a field being edited is unrecoverable.
    /// </summary>
    [Fact]
    public void OnDisable_IsFalseWhenOnlyTheHostWasBlanked() =>
        Assert.False(MqttWithdrawal.OnDisable(
            Settings(enabled: true, host: "broker.lan"),
            Settings(enabled: true, host: "")));

    /// <summary>The same rule from the other side: switching off is the transition, whether or not the
    /// configuration was ever complete enough to connect with.</summary>
    [Fact]
    public void OnDisable_IsTrueWhenPublishingGoesOffWithNoHostSet() =>
        Assert.True(MqttWithdrawal.OnDisable(
            Settings(enabled: true, host: ""),
            Settings(enabled: false, host: "")));

    [Fact]
    public void OnDisable_RefusesNulls()
    {
        Assert.Throws<ArgumentNullException>(() => MqttWithdrawal.OnDisable(null!, Settings(false)));
        Assert.Throws<ArgumentNullException>(() => MqttWithdrawal.OnDisable(Settings(true), null!));
    }

    // ── What the user is told ───────────────────────────────────────────────────

    [Fact]
    public void Report_TreatsOnlyARemovalAsSuccess()
    {
        Assert.False(MqttWithdrawal.Report(MqttWithdrawal.Outcome.Removed).IsFailure);
        Assert.True(MqttWithdrawal.Report(MqttWithdrawal.Outcome.NoConnection).IsFailure);
        Assert.True(MqttWithdrawal.Report(MqttWithdrawal.Outcome.Failed).IsFailure);
    }

    /// <summary>A removal that never started must not read as one that did — the device is still on the
    /// broker, and a message implying otherwise is the stranding this issue is about, silently.</summary>
    [Fact]
    public void Report_SaysTheDeviceIsStillPublishedWhenThereWasNoLink()
    {
        string message = MqttWithdrawal.Report(MqttWithdrawal.Outcome.NoConnection).Message;

        Assert.Contains("nothing was removed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every outcome says something. A destructive command answering with silence cannot be told
    /// from one that worked (docs\DISPLAY-VOCABULARY.md, corollary 2).</summary>
    [Theory]
    [InlineData(MqttWithdrawal.Outcome.Removed)]
    [InlineData(MqttWithdrawal.Outcome.NoConnection)]
    [InlineData(MqttWithdrawal.Outcome.Failed)]
    public void Report_HasAMessageForEveryOutcome(MqttWithdrawal.Outcome outcome) =>
        Assert.False(string.IsNullOrWhiteSpace(MqttWithdrawal.Report(outcome).Message));

    /// <summary>The MQTT surface names no particular consumer — the standing rule this app's
    /// <c>MqttPanelStrings</c> exists to keep, and a removal message is exactly where a receiver's name
    /// would otherwise arrive.</summary>
    [Fact]
    public void TheVocabularyNamesNoConsumer()
    {
        var texts = new List<string> { MqttWithdrawal.ConfirmPrompt };
        foreach (var outcome in Enum.GetValues<MqttWithdrawal.Outcome>())
            texts.Add(MqttWithdrawal.Report(outcome).Message);

        foreach (string text in texts)
            Assert.DoesNotContain("home assistant", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The consent has to carry what is actually lost, or it is not consent: the receiving end's
    /// chosen ids do not come back, and the module's own docs are explicit that a taken entity id is gone
    /// permanently.</summary>
    [Fact]
    public void ConfirmPrompt_SaysWhatIsLostAndThatItIsFinal()
    {
        Assert.Contains("entity id", MqttWithdrawal.ConfirmPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be undone", MqttWithdrawal.ConfirmPrompt, StringComparison.OrdinalIgnoreCase);
    }

    // ── The sequence ────────────────────────────────────────────────────────────

    private sealed class Flow(MqttWithdrawal.Outcome outcome, bool agree)
    {
        public int Prompts;
        public int Withdrawals;
        public string? Reported;
        public bool? ReportedAsFailure;
        public string? Prompted;

        public Task<MqttWithdrawal.Outcome?> RunAsync() => MqttWithdrawal.RunAsync(
            confirm: prompt => { Prompts++; Prompted = prompt; return agree; },
            withdraw: () => { Withdrawals++; return Task.FromResult(outcome); },
            report: (message, isFailure) => { Reported = message; ReportedAsFailure = isFailure; });
    }

    /// <summary>Declining removes nothing and says nothing. A "cancelled" report for a destructive command
    /// the user cancelled is noise; what must never happen is the removal running anyway.</summary>
    [Fact]
    public async Task RunAsync_RemovesNothingWhenTheUserDeclines()
    {
        var flow = new Flow(MqttWithdrawal.Outcome.Removed, agree: false);

        Assert.Null(await flow.RunAsync());
        Assert.Equal(1, flow.Prompts);
        Assert.Equal(0, flow.Withdrawals);
        Assert.Null(flow.Reported);
    }

    [Fact]
    public async Task RunAsync_AsksBeforeRemovingAnything()
    {
        var flow = new Flow(MqttWithdrawal.Outcome.Removed, agree: true);

        Assert.Equal(MqttWithdrawal.Outcome.Removed, await flow.RunAsync());
        Assert.Equal(1, flow.Withdrawals);
        Assert.Equal(MqttWithdrawal.ConfirmPrompt, flow.Prompted);
    }

    /// <summary>Every outcome is reported, and each carries its own verdict — reporting only the happy
    /// path leaves a user who pressed the button with no idea the device is still there.</summary>
    [Theory]
    [InlineData(MqttWithdrawal.Outcome.Removed,      false)]
    [InlineData(MqttWithdrawal.Outcome.NoConnection, true)]
    [InlineData(MqttWithdrawal.Outcome.Failed,       true)]
    public async Task RunAsync_ReportsEveryOutcome(MqttWithdrawal.Outcome outcome, bool isFailure)
    {
        var flow = new Flow(outcome, agree: true);

        Assert.Equal(outcome, await flow.RunAsync());
        Assert.Equal(MqttWithdrawal.Report(outcome).Message, flow.Reported);
        Assert.Equal(isFailure, flow.ReportedAsFailure);
    }

    [Fact]
    public async Task RunAsync_RefusesNulls()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => MqttWithdrawal.RunAsync(
            null!, () => Task.FromResult(MqttWithdrawal.Outcome.Removed), (_, _) => { }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => MqttWithdrawal.RunAsync(
            _ => true, null!, (_, _) => { }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => MqttWithdrawal.RunAsync(
            _ => true, () => Task.FromResult(MqttWithdrawal.Outcome.Removed), null!));
    }
}
