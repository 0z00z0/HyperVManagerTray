using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The manual override onto a bridged switch. A VM moved onto a switch that could not be bound loses its
/// network with nothing to show for it, so the move happens only after a bind that succeeded.
/// </summary>
public class OverrideFlowTests
{
    private const string Adapter = "{00000000-0000-0000-0000-000000000001}";

    [Theory]
    [InlineData(SwitchBindOutcome.Failed, OverrideFlow.Outcome.BindFailed, "bind")]
    [InlineData(SwitchBindOutcome.Bound, OverrideFlow.Outcome.Applied, "bind,move")]
    [InlineData(SwitchBindOutcome.AlreadyBound, OverrideFlow.Outcome.Applied, "bind,move")]
    public async Task BridgedOverride_MovesTheVmOnlyAfterASuccessfulBind(
        SwitchBindOutcome bind, OverrideFlow.Outcome expected, string expectedSteps)
    {
        var steps = new List<string>();

        var outcome = await OverrideFlow.RunAsync(
            needsBind: true, Adapter,
            confirmDrop: () => true,
            acquire: () => Task.FromResult(true),
            bind: () => { steps.Add("bind"); return Task.FromResult(bind); },
            move: () => { steps.Add("move"); return Task.FromResult(true); });

        Assert.Equal(expected, outcome);
        Assert.Equal(expectedSteps, string.Join(",", steps));
    }

    [Fact]
    public void OverrideOntoTheFallbackSwitch_EndingOnTheFallback_ArmsNoBridgeLostAction()
    {
        var previous = OverrideFlow.RuleBefore(OverrideFlow.ManualRuleId, MatchResult.FallbackRuleId);
        var fallback = new MatchResult(MatchResult.FallbackRuleId, "Fallback", "fallback-switch", "Default Switch", []);

        Assert.False(fallback.LosesBridgeAfter(previous));
    }

    [Fact]
    public void Override_EndingOnTheRuleItReplaced_RunsNoSecondAutostart()
    {
        const string home = "6f1c2b9e-0d4a-4e57-9a3b-2c8e1f7d5a10";
        var previous = OverrideFlow.RuleBefore(OverrideFlow.ManualRuleId, home);
        var result   = new MatchResult(home, "Home", "bridge-switch", "Bridge", []);

        Assert.False(result.ActivatesRuleAfter(previous));
    }
}
