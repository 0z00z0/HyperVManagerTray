using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The gate every inbound Home Assistant command passes (issue #75). It asks
/// <see cref="VmStateUi.AllowedVerbs"/> — the same rule the dashboard's buttons use — so a remote
/// write can reach nothing the dashboard cannot, and a verb the current state does not allow is
/// refused rather than attempted.
/// </summary>
public class MqttCommandGateTests
{
    // ── The announced options ──────────────────────────────────────────────────

    [Fact]
    public void PowerOptions_AreTheFiveVerbsIssue75Names()
    {
        Assert.Equal(["Start", "Shutdown", "Pause", "Save", "Resume"], MqttCommandGate.PowerOptions);
    }

    [Theory]
    [InlineData("Start",    VmOpKind.Start)]
    [InlineData("shutdown", VmOpKind.Shutdown)]
    [InlineData("  Pause ", VmOpKind.Pause)]
    [InlineData("SAVE",     VmOpKind.Save)]
    [InlineData("Resume",   VmOpKind.Resume)]
    public void ParseVerb_ReadsTheAnnouncedOptions(string payload, VmOpKind expected) =>
        Assert.Equal(expected, MqttCommandGate.ParseVerb(payload));

    [Theory]
    [InlineData("")]
    [InlineData("Reboot")]
    [InlineData("ON")]
    [InlineData(null)]
    public void ParseVerb_RefusesAnythingElse(string? payload) =>
        Assert.Null(MqttCommandGate.ParseVerb(payload));

    // ── The gate itself ────────────────────────────────────────────────────────

    /// <summary>Every verb the dashboard offers in a state is allowed here too, and nothing else is.
    /// Enumerated rather than spot-checked so the two can never drift apart.</summary>
    [Theory]
    [InlineData("Running")]
    [InlineData("Paused")]
    [InlineData("Saved")]
    [InlineData("Off")]
    [InlineData("Starting")]
    [InlineData("Stopping")]
    [InlineData("Unknown")]
    [InlineData("")]
    public void Power_AllowsExactlyTheVerbsTheDashboardOffers(string state)
    {
        var allowed = VmStateUi.AllowedVerbs(state);

        foreach (var kind in Enum.GetValues<VmOpKind>())
            Assert.Equal(allowed.Contains(kind), MqttCommandGate.Power(state, kind).Allowed);
    }

    [Fact]
    public void Power_RefusesAVerbTheStateDoesNotAllow()
    {
        var verdict = MqttCommandGate.Power("Off", VmOpKind.Shutdown);

        Assert.False(verdict.Allowed);
        Assert.Contains("Shutdown", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("Off", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>A transitional state offers nothing at all: Hyper-V answers a request made then with
    /// 0x8007, so the refusal happens here rather than as a failure in the log.</summary>
    [Theory]
    [InlineData("Starting")]
    [InlineData("Stopping")]
    [InlineData("Saving")]
    [InlineData("Pausing")]
    [InlineData("Resuming")]
    [InlineData("Snapshotting")]
    public void Power_RefusesEveryVerbMidTransition(string state)
    {
        foreach (var kind in Enum.GetValues<VmOpKind>())
            Assert.False(MqttCommandGate.Power(state, kind).Allowed);
    }

    [Fact]
    public void Power_RefusesEveryVerbForAnUnknownState()
    {
        foreach (var kind in Enum.GetValues<VmOpKind>())
        {
            Assert.False(MqttCommandGate.Power(null, kind).Allowed);
            Assert.False(MqttCommandGate.Power("Unknown", kind).Allowed);
        }
    }

    // ── The on/off switch ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Off",    true,  VmOpKind.Start)]
    [InlineData("Saved",  true,  VmOpKind.Start)]
    [InlineData("Paused", true,  VmOpKind.Resume)]
    [InlineData("Running", false, VmOpKind.Shutdown)]
    public void Running_MapsOnAndOffToTheVerbTheStateAllows(string state, bool on, VmOpKind expected)
    {
        var verdict = MqttCommandGate.Running(state, on, out var kind);

        Assert.True(verdict.Allowed);
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("Off",     false)]   // nothing to shut down
    [InlineData("Saved",   false)]
    [InlineData("Running", true)]    // already running
    [InlineData("Starting", true)]
    [InlineData("Starting", false)]
    public void Running_RefusesWhenTheStateAllowsNeitherVerb(string state, bool on) =>
        Assert.False(MqttCommandGate.Running(state, on, out _).Allowed);

    // ── The switch override ────────────────────────────────────────────────────

    [Fact]
    public void Override_AllowsAConfiguredSwitch()
    {
        Assert.True(MqttCommandGate.Override(["Bridged", "Isolated"], "Bridged").Allowed);
        // Hyper-V switch names are case-insensitive everywhere else in this app.
        Assert.True(MqttCommandGate.Override(["Bridged"], " bridged ").Allowed);
    }

    [Fact]
    public void Override_RefusesASwitchNoRuleNames()
    {
        var verdict = MqttCommandGate.Override(["Bridged"], "Default Switch");

        Assert.False(verdict.Allowed);
        Assert.Contains("Default Switch", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Override_RefusesWhenNothingIsConfigured() =>
        Assert.False(MqttCommandGate.Override([], "Bridged").Allowed);
}
