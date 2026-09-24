using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>Issue #69: a failed VM power action reaches the person who asked for it, with the machine,
/// what did not happen to it, and why.</summary>
public class VmPowerUiTests
{
    /// <summary>The sentence VmFailureText builds for the measured failure: a host that could not set
    /// aside the memory the machine needed.</summary>
    private static readonly string OutOfMemory = VmFailureText.ForRequestRefusal(VmOpKind.Start, 32778).Full;

    /// <summary>A failed start is announced even while the dashboard is open: an automatic start may
    /// concern a machine with no card, and the card's own report is one short line.</summary>
    [Fact]
    public void AFailedStart_IsShownEvenWhileTheDashboardIsOpen()
    {
        Assert.False(VmPowerUi.SuppressWhenDashboardVisible(VmOpKind.Start));
        Assert.True(VmPowerUi.SuppressWhenDashboardVisible(VmOpKind.Pause));
    }

    /// <summary>The balloon carries the same sentence the card's tooltip and the broker do.</summary>
    [Fact]
    public void TheStartBalloon_NamesTheMachineAndTheReason()
    {
        var message = VmPowerUi.FailedMessage("Lab", VmOpKind.Start, OutOfMemory);

        Assert.StartsWith("'Lab' did not start.", message, StringComparison.Ordinal);
        Assert.Contains(OutOfMemory, message, StringComparison.Ordinal);
    }

    /// <summary>Every operation, not only a start, says what did not happen — the balloon used to say
    /// "Power action failed" for the other four.</summary>
    [Theory]
    [InlineData(VmOpKind.Resume,   "was not resumed")]
    [InlineData(VmOpKind.Pause,    "was not paused")]
    [InlineData(VmOpKind.Save,     "was not saved")]
    [InlineData(VmOpKind.Shutdown, "was not shut down")]
    public void EveryOtherOperation_SaysWhatDidNotHappen(VmOpKind kind, string expected)
        => Assert.StartsWith($"'Lab' {expected}.", VmPowerUi.FailedMessage("Lab", kind, OutOfMemory),
            StringComparison.Ordinal);

    /// <summary>Both halves, and the reason: this report can replace the start balloon on screen.</summary>
    [Fact]
    public void StartAndConnect_GivingUp_SaysTheConsoleWasNotOpenedAndWhy()
    {
        var message = VmPowerUi.StartAndConnectAbandonedMessage("Lab", OutOfMemory);

        Assert.Contains("'Lab' did not start", message, StringComparison.Ordinal);
        Assert.Contains("console was not opened", message, StringComparison.Ordinal);
        Assert.Contains(OutOfMemory, message, StringComparison.Ordinal);
    }

    /// <summary>With no sentence to carry, the balloon says where to look rather than trailing off.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNoReason_TheMessageSaysWhereToLook(string? detail)
        => Assert.Contains("vm-power.log", VmPowerUi.FailedMessage("Lab", VmOpKind.Start, detail),
            StringComparison.Ordinal);
}
