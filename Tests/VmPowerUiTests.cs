using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>Issue #69: a failed VM start reaches the person who asked for it, with the VM and the reason.</summary>
public class VmPowerUiTests
{
    private const string HyperVReason = "'Lab' failed to start. Not enough memory in the system to start the virtual machine.";

    /// <summary>The card's inline report is cut short and fades, and an automatic start may have no card,
    /// so the dashboard being open must never hold back a failed start.</summary>
    [Fact]
    public void AFailedStart_IsShownEvenWhileTheDashboardIsOpen()
    {
        Assert.False(VmPowerUi.SuppressWhenDashboardVisible(VmOpKind.Start));
        Assert.True(VmPowerUi.SuppressWhenDashboardVisible(VmOpKind.Pause));
    }

    /// <summary>Composes on the progress message the mapper already builds, rather than re-deriving it.</summary>
    [Fact]
    public void TheStartBalloon_NamesTheVmAndHyperVsReason()
    {
        var progress = WmiVmMapper.ProgressMessage(VmOpKind.Start, VmOpPhase.Failed, null, HyperVReason);

        var message = VmPowerUi.StartFailedMessage("Lab", progress);

        Assert.StartsWith("'Lab' did not start.", message, StringComparison.Ordinal);
        Assert.Contains(HyperVReason, message, StringComparison.Ordinal);
    }

    /// <summary>Both halves, and the reason: this report can replace the start balloon on screen.</summary>
    [Fact]
    public void StartAndConnect_GivingUp_SaysTheConsoleWasNotOpenedAndWhy()
    {
        var progress = WmiVmMapper.ProgressMessage(VmOpKind.Start, VmOpPhase.Failed, null, HyperVReason);

        var message = VmPowerUi.StartAndConnectAbandonedMessage("Lab", progress);

        Assert.Contains("'Lab' did not start", message, StringComparison.Ordinal);
        Assert.Contains("console was not opened", message, StringComparison.Ordinal);
        Assert.Contains(HyperVReason, message, StringComparison.Ordinal);
    }

    /// <summary>With no reason from Hyper-V the balloon says so rather than showing an empty sentence.</summary>
    [Fact]
    public void WithNoReason_TheMessageSaysWhereToLook()
    {
        var progress = WmiVmMapper.ProgressMessage(VmOpKind.Start, VmOpPhase.Failed, null, null);

        Assert.Null(VmPowerUi.Reason(progress));
        Assert.Contains("vm-power.log", VmPowerUi.StartFailedMessage("Lab", progress), StringComparison.Ordinal);
    }
}
