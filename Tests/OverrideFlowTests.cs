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
}
