using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Tests;

/// <summary>
/// What an inbound MQTT command is allowed to do (issue #75). Two things are asserted throughout: a
/// remote write reaches nothing <see cref="VmStateUi.AllowedVerbs"/> would not offer the dashboard's own
/// buttons, and a refusal carries the APPLICATION's wording — the module composes none, so an empty
/// <see cref="MqttCommandVerdict.Detail"/> reaches the broker as a refusal with no reason in it.
/// </summary>
public class MqttCommandGateTests
{
    /// <summary>Records whether the verdict's work ran, and with which verb. A refusal must leave it
    /// untouched: nothing is attempted, so nothing can be half-done.</summary>
    private sealed class Runner
    {
        public int Calls;
        public VmOpKind? Kind;
        public string? SwitchName;

        public Task Power(CancellationToken _) { Calls++; return Task.CompletedTask; }
        public Task Power(VmOpKind kind, CancellationToken _) { Calls++; Kind = kind; return Task.CompletedTask; }
        public Task Override(string name, CancellationToken _) { Calls++; SwitchName = name; return Task.CompletedTask; }
    }

    /// <summary>Runs an accepted verdict's work, so "accepted" is asserted by what it DOES rather than
    /// by the enum alone.</summary>
    private static void Run(MqttCommandVerdict verdict) => verdict.Run!(CancellationToken.None).Wait();

    // ── The state → verb contract (the same gate the dashboard's buttons use) ────

    /// <summary>
    /// The whole table, hard-coded rather than read back out of <see cref="VmStateUi.AllowedVerbs"/> —
    /// deriving the expectation from the thing under test would pass against any table at all. Every one
    /// of the five verbs is tried against every state, so this fails both ways: a verb the state must
    /// refuse being accepted, and one it must offer being refused.
    /// </summary>
    public static TheoryData<string?, VmOpKind[]> StateVerbTable() => new()
    {
        { "Running",      [VmOpKind.Shutdown, VmOpKind.Pause, VmOpKind.Save] },
        { "Paused",       [VmOpKind.Resume, VmOpKind.Save] },
        { "Saved",        [VmOpKind.Start] },
        { "Off",          [VmOpKind.Start] },
        // Transitional: a RequestStateChange here returns 0x8007, so nothing is offered.
        { "Starting",     [] },
        { "Stopping",     [] },
        { "Saving",       [] },
        { "Pausing",      [] },
        { "Resuming",     [] },
        { "Snapshotting", [] },
        // Not read yet, or a value WmiVmMapper does not map.
        { "Unknown",      [] },
        { "",             [] },
        { null,           [] },
    };

    [Theory]
    [MemberData(nameof(StateVerbTable))]
    public void Power_AcceptsExactlyTheVerbsTheStateAllows(string? state, VmOpKind[] allowed)
    {
        foreach (var kind in MqttCommandGate.PowerVerbs)
        {
            var runner = new Runner();
            var verdict = MqttCommandGate.Power(state, kind, runner.Power);

            if (allowed.Contains(kind))
            {
                Assert.True(verdict.IsAccepted, $"'{kind}' must be accepted while the VM is '{state}'");
                Run(verdict);
                Assert.Equal(1, runner.Calls);
            }
            else
            {
                Assert.Equal(MqttCommandOutcome.Refused, verdict.Outcome);
                Assert.Null(verdict.Run);
                Assert.Equal(0, runner.Calls);   // refused means not attempted
            }
        }
    }

    /// <summary>The refusal is the app's own sentence, naming the verb and the state. The module carries
    /// <see cref="MqttCommandVerdict.Detail"/> verbatim to the refusal sink, so an empty one is a
    /// refusal the operator cannot act on.</summary>
    [Theory]
    [InlineData("Off",      VmOpKind.Shutdown, "'Shutdown' is not available while the VM is Off.")]
    [InlineData("Running",  VmOpKind.Start,    "'Start' is not available while the VM is Running.")]
    [InlineData("Saved",    VmOpKind.Pause,    "'Pause' is not available while the VM is Saved.")]
    [InlineData("Starting", VmOpKind.Start,    "'Start' is not available while the VM is Starting.")]
    public void Power_RefusalCarriesTheApplicationsOwnWording(string state, VmOpKind kind, string expected)
        => Assert.Equal(expected, MqttCommandGate.Power(state, kind, _ => Task.CompletedTask).Detail);

    /// <summary>A state that was never read has no name to quote, so the sentence says so rather than
    /// reading as "not available while the VM is ".</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Power_RefusalNamesAnUnreadStateAsUnknown(string? state)
        => Assert.Equal("'Start' is not available while the VM is in an unknown state.",
                        MqttCommandGate.Power(state, VmOpKind.Start, _ => Task.CompletedTask).Detail);

    [Fact]
    public void Power_AcceptanceCarriesNoRefusalText()
    {
        var verdict = MqttCommandGate.Power("Off", VmOpKind.Start, _ => Task.CompletedTask);
        Assert.Equal(MqttCommandOutcome.Accepted, verdict.Outcome);
        Assert.Equal("", verdict.Detail);
    }

    // ── The on/off switch ───────────────────────────────────────────────────────

    /// <summary>On starts a stopped or saved VM and RESUMES a paused one — Start is not among a paused
    /// VM's allowed verbs, so mapping on→Start unconditionally would refuse the commonest request.</summary>
    [Theory]
    [InlineData("Off",     true,  VmOpKind.Start)]
    [InlineData("Saved",   true,  VmOpKind.Start)]
    [InlineData("Paused",  true,  VmOpKind.Resume)]
    [InlineData("Running", false, VmOpKind.Shutdown)]
    public void Running_MapsOnOffToTheVerbTheStateAllows(string state, bool on, VmOpKind expected)
    {
        var runner = new Runner();
        var verdict = MqttCommandGate.Running(state, on, runner.Power);

        Assert.True(verdict.IsAccepted, $"turning {(on ? "on" : "off")} a '{state}' VM must be accepted");
        Run(verdict);
        Assert.Equal(expected, runner.Kind);
    }

    /// <summary>A state that allows neither verb refuses, in the app's own words — the switch is not a
    /// second way in past <see cref="VmStateUi.AllowedVerbs"/>.</summary>
    [Theory]
    [InlineData("Off",      false, "'Shutdown' is not available while the VM is Off.")]
    [InlineData("Saved",    false, "'Shutdown' is not available while the VM is Saved.")]
    [InlineData("Paused",   false, "'Shutdown' is not available while the VM is Paused.")]
    [InlineData("Running",  true,  "'Start' is not available while the VM is Running.")]
    [InlineData("Starting", true,  "'Start' is not available while the VM is Starting.")]
    [InlineData("Stopping", false, "'Shutdown' is not available while the VM is Stopping.")]
    public void Running_RefusesWhenTheStateAllowsNeitherVerb(string state, bool on, string expected)
    {
        var runner = new Runner();
        var verdict = MqttCommandGate.Running(state, on, runner.Power);

        Assert.Equal(MqttCommandOutcome.Refused, verdict.Outcome);
        Assert.Equal(expected, verdict.Detail);
        Assert.Equal(0, runner.Calls);
    }

    // ── The announced power verbs ───────────────────────────────────────────────

    /// <summary>The options are the verb names, in the declared order: the receiver renders them in it,
    /// and <see cref="MqttCommandGate.ParseVerb"/> has to read back exactly what was announced.</summary>
    [Fact]
    public void PowerOptions_AreTheVerbNamesInTheDeclaredOrder()
        => Assert.Equal(["Start", "Shutdown", "Pause", "Save", "Resume"], MqttCommandGate.PowerOptions);

    [Fact]
    public void PowerVerbs_CoverEveryVerbTheStateTableCanOffer()
    {
        var offered = StateVerbTable().SelectMany(row => (VmOpKind[])row[1]).Distinct();
        Assert.All(offered, kind => Assert.Contains(kind, MqttCommandGate.PowerVerbs));
    }

    [Theory]
    [InlineData("Start",    VmOpKind.Start)]
    [InlineData("shutdown", VmOpKind.Shutdown)]
    [InlineData("  Pause ", VmOpKind.Pause)]
    [InlineData("SAVE",     VmOpKind.Save)]
    [InlineData("Resume",   VmOpKind.Resume)]
    public void ParseVerb_ReadsAnAnnouncedOption(string payload, VmOpKind expected)
        => Assert.Equal(expected, MqttCommandGate.ParseVerb(payload));

    [Theory]
    [InlineData("Reboot")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("None")]
    public void ParseVerb_ReturnsNullForAPayloadThatNamesNoVerb(string? payload)
        => Assert.Null(MqttCommandGate.ParseVerb(payload));

    // ── The switch override ─────────────────────────────────────────────────────

    /// <summary>Only a switch this host announced may be bound: a receiver holding a stale option list
    /// must not be able to move a VM onto a switch no rule names.</summary>
    [Fact]
    public void Override_AcceptsAConfiguredSwitch()
    {
        var runner = new Runner();
        var verdict = MqttCommandGate.Override(["Bridged", "Default Switch"], "Bridged", runner.Override);

        Assert.True(verdict.IsAccepted);
        Run(verdict);
        Assert.Equal("Bridged", runner.SwitchName);
    }

    /// <summary>Matched case-insensitively and trimmed, and the TRIMMED name is what reaches the host —
    /// a switch name with a stray space binds nothing.</summary>
    [Fact]
    public void Override_TrimsAndMatchesCaseInsensitively()
    {
        var runner = new Runner();
        var verdict = MqttCommandGate.Override(["Bridged"], "  bridged  ", runner.Override);

        Assert.True(verdict.IsAccepted);
        Run(verdict);
        Assert.Equal("bridged", runner.SwitchName);
    }

    [Theory]
    [InlineData("Guest Only", "'Guest Only' is not one of the configured rule switches.")]
    [InlineData("",           "'' is not one of the configured rule switches.")]
    [InlineData(null,         "'' is not one of the configured rule switches.")]
    public void Override_RefusesASwitchNoRuleNames(string? name, string expected)
    {
        var runner = new Runner();
        var verdict = MqttCommandGate.Override(["Bridged"], name, runner.Override);

        Assert.Equal(MqttCommandOutcome.NotAnOption, verdict.Outcome);
        Assert.Equal(expected, verdict.Detail);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Override_RefusesEverythingWhenNoRuleNamesASwitch()
    {
        var runner = new Runner();
        var verdict = MqttCommandGate.Override([], "Bridged", runner.Override);

        Assert.Equal(MqttCommandOutcome.NotAnOption, verdict.Outcome);
        Assert.Equal(0, runner.Calls);
    }

    /// <summary>Every refusal this gate can produce says why. The module carries the sentence and
    /// composes nothing, so a blank here reaches the operator as a bare outcome name.</summary>
    [Fact]
    public void EveryRefusalCarriesAReason()
    {
        MqttCommandVerdict[] refusals =
        [
            MqttCommandGate.Power("Off", VmOpKind.Shutdown, _ => Task.CompletedTask),
            MqttCommandGate.Power(null, VmOpKind.Start, _ => Task.CompletedTask),
            MqttCommandGate.Running("Starting", true, (_, _) => Task.CompletedTask),
            MqttCommandGate.Override(["Bridged"], "Guest Only", (_, _) => Task.CompletedTask),
        ];

        Assert.All(refusals, v =>
        {
            Assert.NotEqual(MqttCommandOutcome.Accepted, v.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(v.Detail), $"{v.Outcome} carried no reason");
        });
    }
}
