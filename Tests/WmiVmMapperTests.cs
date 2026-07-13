using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

public class WmiVmMapperTests
{
    [Theory]
    [InlineData((ushort)2, "Running")]
    [InlineData((ushort)3, "Off")]
    [InlineData((ushort)9, "Paused")]       // Quiesce — a user-initiated pause reports EnabledState 9
    [InlineData((ushort)32768, "Paused")]   // host critical-pause
    [InlineData((ushort)32769, "Saved")]
    [InlineData((ushort)32773, "Saving")]
    [InlineData((ushort)32770, "Starting")]
    [InlineData((ushort)9999, "Unknown")]
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
            uptimeMs: 3_900_000, statusDescription: "Operating normally", memMaxBytes: 4L * 1024 * 1024 * 1024,
            switchName: "Bridged");

        Assert.Equal("Running", s.State);
        Assert.True(s.IsRunning);
        Assert.Equal(12, s.Cpu);
        Assert.Equal(2048L * 1048576L, s.MemAssigned);
        Assert.Equal("1h 5m", UptimeFormatter.Format(s));   // proves Uptime is a parseable TimeSpan string
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

    [Theory]
    [InlineData("Saving, 47 %", 47)]
    [InlineData("Restoring, 8%", 8)]
    [InlineData("Operating normally", null)]
    [InlineData("", null)]
    public void PercentFromStatus_ExtractsPercent(string desc, int? expected)
        => Assert.Equal(expected, WmiVmMapper.PercentFromStatus(desc));

    [Theory]
    [InlineData("Restoring, 72 %", "Restoring")]   // comma + spaces
    [InlineData("Saving, 5 %", "Saving")]
    [InlineData("Restoring 72%", "Restoring")]     // issue #13: comma-less shape the old parse missed
    [InlineData("Saving 47%", "Saving")]
    // Health element [0] joined before the operation verb by ReadSummaries — verb still resolves.
    [InlineData("Operating normally Restoring 72%", "Restoring")]
    [InlineData("Operating normally Saving, 5 %", "Saving")]
    [InlineData("Operating normally", null)]       // no percentage → fall back to coarse state
    [InlineData(",missing verb", null)]            // no percentage
    [InlineData("", null)]
    [InlineData(null, null)]
    public void LeadingVerbFromStatus_ExtractsLeadingVerb(string? desc, string? expected)
        => Assert.Equal(expected, WmiVmMapper.LeadingVerbFromStatus(desc));
}
