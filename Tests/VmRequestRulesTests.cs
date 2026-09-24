using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The guard that decides whether a power request is issued at all.
///
/// <para>Recognising only the finished state let a rule's autostart ask a machine that was already
/// <c>Starting</c> to start again — twice inside 90 seconds while a dock settled — and each extra
/// request came back refused, replacing the running start's progress on the card with a code. These
/// are the decisions that stop that, and they are only testable here: the states they turn on are
/// otherwise reached by docking a laptop and catching a machine mid-transition.</para>
/// </summary>
public class VmRequestRulesTests
{
    [Theory]
    // Start and Resume both drive the machine to Running, and both transient states on the way there
    // count — a cold boot reads Starting, a resume-from-Saved reads Resuming.
    [InlineData(VmOpKind.Start,  "Running",  VmRequestStanding.AlreadyThere)]
    [InlineData(VmOpKind.Start,  "Starting", VmRequestStanding.AlreadyUnderWay)]
    [InlineData(VmOpKind.Start,  "Resuming", VmRequestStanding.AlreadyUnderWay)]
    [InlineData(VmOpKind.Start,  "Off",      VmRequestStanding.Needed)]
    [InlineData(VmOpKind.Start,  "Saved",    VmRequestStanding.Needed)]
    [InlineData(VmOpKind.Start,  "Paused",   VmRequestStanding.Needed)]
    [InlineData(VmOpKind.Resume, "Running",  VmRequestStanding.AlreadyThere)]
    [InlineData(VmOpKind.Resume, "Starting", VmRequestStanding.AlreadyUnderWay)]
    [InlineData(VmOpKind.Resume, "Paused",   VmRequestStanding.Needed)]
    // The stop direction, which had no guard at all: a graceful shutdown skipped past it entirely.
    [InlineData(VmOpKind.Shutdown, "Off",      VmRequestStanding.AlreadyThere)]
    [InlineData(VmOpKind.Shutdown, "Stopping", VmRequestStanding.AlreadyUnderWay)]
    [InlineData(VmOpKind.Shutdown, "Running",  VmRequestStanding.Needed)]
    [InlineData(VmOpKind.Save,     "Saved",    VmRequestStanding.AlreadyThere)]
    [InlineData(VmOpKind.Save,     "Saving",   VmRequestStanding.AlreadyUnderWay)]
    [InlineData(VmOpKind.Save,     "Running",  VmRequestStanding.Needed)]
    [InlineData(VmOpKind.Pause,    "Paused",   VmRequestStanding.AlreadyThere)]
    [InlineData(VmOpKind.Pause,    "Pausing",  VmRequestStanding.AlreadyUnderWay)]
    [InlineData(VmOpKind.Pause,    "Running",  VmRequestStanding.Needed)]
    // A machine moving somewhere else is no reason to skip: the request is what redirects it.
    [InlineData(VmOpKind.Start,    "Saving",       VmRequestStanding.Needed)]
    [InlineData(VmOpKind.Start,    "Stopping",     VmRequestStanding.Needed)]
    [InlineData(VmOpKind.Start,    "Snapshotting", VmRequestStanding.Needed)]
    [InlineData(VmOpKind.Save,     "Starting",     VmRequestStanding.Needed)]
    [InlineData(VmOpKind.Shutdown, "Saving",       VmRequestStanding.Needed)]
    // Nothing established: a state nobody could read is never a reason to skip a request.
    [InlineData(VmOpKind.Start, "Unknown", VmRequestStanding.Needed)]
    [InlineData(VmOpKind.Start, "",        VmRequestStanding.Needed)]
    [InlineData(VmOpKind.Start, null,      VmRequestStanding.Needed)]
    public void Standing_IsDecidedByTheStateTheMachineIsInOrHeadingFor(
        VmOpKind kind, string? state, VmRequestStanding expected)
        => Assert.Equal(expected, VmRequestRules.Standing(kind, state));

    /// <summary>The state arrives as whatever <c>MapState</c> wrote, so comparison cannot be
    /// case-sensitive and must survive the padding a WMI read can leave behind.</summary>
    [Theory]
    [InlineData("starting")]
    [InlineData("STARTING")]
    [InlineData("  Starting  ")]
    public void Standing_IgnoresCaseAndSurroundingSpace(string state)
        => Assert.Equal(VmRequestStanding.AlreadyUnderWay, VmRequestRules.Standing(VmOpKind.Start, state));

    /// <summary>
    /// The rule matches the names the mapper really produces. Spelling a transient state here that
    /// <see cref="WmiVmMapper.MapState"/> never emits would leave the guard permanently open while
    /// every assertion above still passed.
    /// </summary>
    [Theory]
    [InlineData(VmOpKind.Start,    32770u)]   // Starting
    [InlineData(VmOpKind.Resume,   32777u)]   // Resuming
    [InlineData(VmOpKind.Shutdown, 32774u)]   // Stopping
    [InlineData(VmOpKind.Save,     32773u)]   // Saving
    [InlineData(VmOpKind.Pause,    32776u)]   // Pausing
    public void Standing_RecognisesTheNamesTheMapperActuallyEmits(VmOpKind kind, uint enabledState)
        => Assert.Equal(VmRequestStanding.AlreadyUnderWay,
            VmRequestRules.Standing(kind, WmiVmMapper.MapState((ushort)enabledState)));

    /// <summary>The convenience the call site reads: both non-Needed answers mean "do not ask again".</summary>
    [Fact]
    public void AlreadySatisfied_CoversBothWaysARequestCanBeRedundant()
    {
        Assert.True(VmRequestRules.AlreadySatisfied(VmOpKind.Start, "Running"));
        Assert.True(VmRequestRules.AlreadySatisfied(VmOpKind.Start, "Starting"));
        Assert.False(VmRequestRules.AlreadySatisfied(VmOpKind.Start, "Off"));
    }
}
