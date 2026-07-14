using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

public class WmiVmMapperTests
{
    [Theory]
    // Msvm vendor codes (Msvm_ComputerSystem).
    [InlineData((ushort)2, "Running")]
    [InlineData((ushort)3, "Off")]
    [InlineData((ushort)9, "Paused")]       // Quiesce — a user-initiated pause reports EnabledState 9
    [InlineData((ushort)32768, "Paused")]   // host critical-pause
    [InlineData((ushort)32769, "Saved")]
    [InlineData((ushort)32773, "Saving")]
    [InlineData((ushort)32770, "Starting")]
    // CIM-standard codes (Msvm_SummaryInformation / CIM-reporting hosts) — issue #13 hardening.
    [InlineData((ushort)4, "Stopping")]     // CIM Shutting Down
    [InlineData((ushort)6, "Saved")]        // CIM Enabled but Offline
    [InlineData((ushort)10, "Starting")]    // CIM Starting (captured live on a resume-from-Saved)
    [InlineData((ushort)11, "Stopping")]    // CIM Stopping
    [InlineData((ushort)9999, "Unknown")]
    // Remaining transient vendor codes not covered above.
    [InlineData((ushort)32771, "Snapshotting")]
    [InlineData((ushort)32774, "Stopping")]
    [InlineData((ushort)32776, "Pausing")]
    [InlineData((ushort)32777, "Resuming")]
    [InlineData((ushort)0, "Unknown")]        // 0 is not a defined EnabledState
    [InlineData(ushort.MaxValue, "Unknown")]  // extreme out-of-range value
    public void MapState_MapsKnownEnabledStates(ushort code, string expected)
        => Assert.Equal(expected, WmiVmMapper.MapState(code));

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(-5L, 0L)]
    [InlineData(2048L, 2048L * 1048576L)]
    public void BytesFromMb_Converts(long mb, long expectedBytes)
        => Assert.Equal(expectedBytes, WmiVmMapper.BytesFromMb(mb));

    [Fact]
    public void UptimeString_ZeroIsEmpty()
        => Assert.Equal("", WmiVmMapper.UptimeString(0));

    // The uptime string must be parseable by the existing UptimeFormatter (TimeSpan round-trip).
    [Fact]
    public void BuildStatus_UptimeFeedsUptimeFormatter()
    {
        // 1h 5m = 3_900_000 ms, Running.
        var s = WmiVmMapper.BuildStatus("vm1", 2, processorLoad: 12, memoryUsageMb: 2048,
            uptimeMs: 3_900_000, memMaxBytes: 4L * 1024 * 1024 * 1024, switchName: "Bridged");

        Assert.Equal("Running", s.State);
        Assert.True(s.IsRunning);
        Assert.Equal(12, s.Cpu);
        Assert.Equal(2048L * 1048576L, s.MemAssigned);
        Assert.Equal("", s.JobStatus);   // no active job → empty transient status
        Assert.Equal("1h 5m", UptimeFormatter.Format(s));   // proves Uptime is a parseable TimeSpan string
    }

    [Fact]
    public void BuildStatus_CarriesJobStatus()
    {
        var s = WmiVmMapper.BuildStatus("vm1", 32770, processorLoad: 0, memoryUsageMb: 0,
            uptimeMs: 0, memMaxBytes: 0, switchName: "", jobStatus: "Restoring (10%)");

        Assert.Equal("Starting", s.State);           // coarse EnabledState for colour
        Assert.Equal("Restoring (10%)", s.JobStatus); // fine-grained Status-column verb for the label
    }

    // ── BuildStatus: Cpu is clamped to 0-100 (WMI/COM can occasionally report out-of-range) ──

    [Theory]
    [InlineData(150, 100)]   // over 100% clamps down
    [InlineData(-20, 0)]     // negative clamps up to 0
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    public void BuildStatus_ClampsCpuToPercentRange(int rawLoad, int expectedCpu)
    {
        var s = WmiVmMapper.BuildStatus("vm1", 2, processorLoad: rawLoad, memoryUsageMb: 0,
            uptimeMs: 0, memMaxBytes: 0, switchName: "");
        Assert.Equal(expectedCpu, s.Cpu);
    }

    [Fact]
    public void BuildStatus_NullJobStatus_BecomesEmptyString()
    {
        var s = WmiVmMapper.BuildStatus("vm1", 2, processorLoad: 0, memoryUsageMb: 0,
            uptimeMs: 0, memMaxBytes: 0, switchName: "", jobStatus: null);
        Assert.Equal("", s.JobStatus);
    }

    [Fact]
    public void BuildStatus_NullSwitchName_BecomesEmptyString()
    {
        var s = WmiVmMapper.BuildStatus("vm1", 2, processorLoad: 0, memoryUsageMb: 0,
            uptimeMs: 0, memMaxBytes: 0, switchName: null!);
        Assert.Equal("", s.Switch);
    }

    [Fact]
    public void ProgressMessage_RequestedAndRunningAndFailed()
    {
        Assert.Equal("Requesting start…", WmiVmMapper.ProgressMessage(VmOpKind.Start, VmOpPhase.Requested, null, null));
        Assert.Equal("Saving (47%)…",     WmiVmMapper.ProgressMessage(VmOpKind.Save,  VmOpPhase.Running, 47, null));
        Assert.Equal("Saving…",           WmiVmMapper.ProgressMessage(VmOpKind.Save,  VmOpPhase.Running, null, null));
        Assert.Equal("Failed: not enough memory", WmiVmMapper.ProgressMessage(VmOpKind.Start, VmOpPhase.Failed, null, "not enough memory"));
        Assert.Equal("", WmiVmMapper.ProgressMessage(VmOpKind.Start, VmOpPhase.Succeeded, null, null));
    }

    // ── ProgressMessage: every VmOpKind has a distinct verb/gerund (Requested + Running phases) ──

    [Theory]
    [InlineData(VmOpKind.Start,    "Requesting start…",     "Starting…")]
    [InlineData(VmOpKind.Resume,   "Requesting resume…",    "Resuming…")]
    [InlineData(VmOpKind.Pause,    "Requesting pause…",     "Pausing…")]
    [InlineData(VmOpKind.Save,     "Requesting save…",      "Saving…")]
    [InlineData(VmOpKind.Shutdown, "Requesting shut down…", "Shutting down…")]
    public void ProgressMessage_EveryKind_HasDistinctVerbAndGerund(VmOpKind kind, string requestedText, string runningText)
    {
        Assert.Equal(requestedText, WmiVmMapper.ProgressMessage(kind, VmOpPhase.Requested, null, null));
        Assert.Equal(runningText,   WmiVmMapper.ProgressMessage(kind, VmOpPhase.Running, null, null));
    }

    [Theory]
    [InlineData(VmOpKind.Start,    "Failed to start")]
    [InlineData(VmOpKind.Resume,   "Failed to resume")]
    [InlineData(VmOpKind.Pause,    "Failed to pause")]
    [InlineData(VmOpKind.Save,     "Failed to save")]
    [InlineData(VmOpKind.Shutdown, "Failed to shut down")]
    public void ProgressMessage_Failed_NoErrorText_UsesVerbFallback(VmOpKind kind, string expected)
        => Assert.Equal(expected, WmiVmMapper.ProgressMessage(kind, VmOpPhase.Failed, null, null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ProgressMessage_Failed_WhitespaceOnlyError_UsesVerbFallback(string? error)
        => Assert.Equal("Failed to start", WmiVmMapper.ProgressMessage(VmOpKind.Start, VmOpPhase.Failed, null, error));

    [Fact]
    public void ProgressMessage_Failed_TrimsErrorText()
        => Assert.Equal("Failed: disk full",
            WmiVmMapper.ProgressMessage(VmOpKind.Save, VmOpPhase.Failed, null, "  disk full  "));

    [Fact]
    public void ProgressMessage_UnrecognizedKind_FallsBackToGenericVerb()
    {
        // An out-of-range VmOpKind (e.g. from a future enum value not yet handled) must degrade
        // to the "update"/"Working" defaults rather than throwing.
        var bogus = (VmOpKind)999;
        Assert.Equal("Requesting update…", WmiVmMapper.ProgressMessage(bogus, VmOpPhase.Requested, null, null));
        Assert.Equal("Working…",           WmiVmMapper.ProgressMessage(bogus, VmOpPhase.Running, null, null));
    }

    [Fact]
    public void ProgressMessage_UnrecognizedPhase_ReturnsEmpty()
        => Assert.Equal("", WmiVmMapper.ProgressMessage(VmOpKind.Start, (VmOpPhase)999, null, null));

    // ── ActiveJobStatus: the issue #13 fix — mirror MMC's Status column from the active job ──

    private static WmiVmMapper.JobSnapshot Job(ushort state, string? name, int pct) => new(state, name, pct);

    [Fact]
    public void ActiveJobStatus_FormatsRunningJob()   // the exact live-captured resume-from-Saved case
        => Assert.Equal("Restoring (10%)",
            WmiVmMapper.ActiveJobStatus(new[] { Job(4, "Restoring", 10) }));

    [Fact]
    public void ActiveJobStatus_FormatsStartingJob()  // JobState 3 (Starting) is active too
        => Assert.Equal("Saving (0%)",
            WmiVmMapper.ActiveJobStatus(new[] { Job(3, "Saving", 0) }));

    [Theory]
    [InlineData((ushort)7)]    // Completed
    [InlineData((ushort)8)]    // Terminated
    [InlineData((ushort)9)]    // Killed
    [InlineData((ushort)10)]   // Exception
    [InlineData((ushort)2)]    // New (not yet active)
    public void ActiveJobStatus_IgnoresInactiveJobStates(ushort jobState)
        => Assert.Null(WmiVmMapper.ActiveJobStatus(new[] { Job(jobState, "Restoring", 42) }));

    [Fact]
    public void ActiveJobStatus_NoJobs_ReturnsNull()
        => Assert.Null(WmiVmMapper.ActiveJobStatus(Array.Empty<WmiVmMapper.JobSnapshot>()));

    [Fact]
    public void ActiveJobStatus_SkipsCompletedThenPicksActive()
        => Assert.Equal("Restoring (55%)", WmiVmMapper.ActiveJobStatus(new[]
        {
            Job(7, "Applying checkpoint", 100),   // finished → skipped
            Job(4, "Restoring", 55),              // first active → chosen
        }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ActiveJobStatus_IgnoresJobsWithNoElementName(string? name)
        => Assert.Null(WmiVmMapper.ActiveJobStatus(new[] { Job(4, name, 10) }));

    [Fact]
    public void ActiveJobStatus_ClampsPercentOutOfRange()
        => Assert.Equal("Restoring (100%)", WmiVmMapper.ActiveJobStatus(new[] { Job(4, "Restoring", 250) }));
}
