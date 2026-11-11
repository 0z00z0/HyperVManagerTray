using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

public class TrayProgressTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    private static VmOperationProgress Op(string id, VmOpKind kind, VmOpPhase phase, int? percent = null) =>
        new(id, kind, phase, percent, null);

    [Theory]
    [InlineData(-5, 0f)]
    [InlineData(0, 0f)]
    [InlineData(25, 90f)]
    [InlineData(50, 180f)]
    [InlineData(100, 360f)]
    [InlineData(140, 360f)]
    public void SweepFor_MapsPercentToDegrees(int percent, float sweep) =>
        Assert.Equal(sweep, IconGenerator.SweepFor(percent), 3);

    [Fact]
    public void Percent_AveragesOnlyOperationsThatReportOne_InWholeSteps()
    {
        var p = new TrayProgress();
        p.Update(Op("a", VmOpKind.Save, VmOpPhase.Running, 40), Now);
        p.Update(Op("b", VmOpKind.Start, VmOpPhase.Running, 64), Now);
        p.Update(Op("c", VmOpKind.Shutdown, VmOpPhase.Running), Now);

        Assert.True(p.Active);
        Assert.Equal(50, p.Percent);   // (40 + 64) / 2 = 52, shown as the step below
    }

    [Fact]
    public void Percent_IsNullWhileNoOperationReportsOne()
    {
        var p = new TrayProgress();
        p.Update(Op("a", VmOpKind.Shutdown, VmOpPhase.Requested), Now);

        Assert.True(p.Active);
        Assert.Null(p.Percent);
    }

    [Fact]
    public void Ring_EndsWhenTheLastOperationEnds_WhicheverWay()
    {
        var p = new TrayProgress();
        p.Update(Op("a", VmOpKind.Save, VmOpPhase.Running, 30), Now);
        p.Update(Op("b", VmOpKind.Start, VmOpPhase.Running, 10), Now);
        p.Update(Op("c", VmOpKind.Shutdown, VmOpPhase.Running), Now);

        p.Update(Op("a", VmOpKind.Save, VmOpPhase.Failed), Now);
        Assert.Equal(10, p.Percent);
        p.Update(Op("B", VmOpKind.Start, VmOpPhase.Succeeded), Now);   // identifiers compare without case
        Assert.True(p.Active);

        // A guest shutdown sends no outcome: the machine reaching Off ends it.
        p.Settle([new VmStatus { Id = "c", State = "Running" }]);
        Assert.True(p.Active);
        p.Settle([new VmStatus { Id = "c", State = "Off" }]);
        Assert.False(p.Active);
    }

    [Fact]
    public void Expire_RetiresAShutdownThatNeverReachesOff()
    {
        var p = new TrayProgress();
        p.Update(Op("c", VmOpKind.Shutdown, VmOpPhase.Running), Now);

        p.Expire(Now + VmStateUi.ShutdownDeadline - TimeSpan.FromSeconds(1));
        Assert.True(p.Active);
        p.Expire(Now + VmStateUi.ShutdownDeadline);
        Assert.False(p.Active);
    }
}
