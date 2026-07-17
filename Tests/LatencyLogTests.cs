using System.Globalization;
using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The interactive-path latency lines (issue #54).
///
/// <para>These tests are mostly about WORDING, which is unusual enough to justify saying why. #54 is
/// instrumentation whose entire value is that the number it prints can be trusted to mean what a reader
/// takes it to mean — it exists because #52's page-in hypothesis cannot be settled without honest
/// numbers, and a figure read as measuring more than it does would settle it WRONGLY, which is worse
/// than the blindness it replaces. The durations themselves are the callers' <c>Stopwatch</c>s and are
/// not this file's business; the claims made about them are, and are the only part a unit test can
/// reach. So the boundary caveats are asserted here as invariants, exactly as
/// <c>NetworkStatusUiTests.IconFor_OnlyAppliedEverRendersAsSuccess</c> asserts that no status may render
/// as a success colour.</para>
/// </summary>
public class LatencyLogTests
{
    // ── The invariant: a measurement names its own boundaries ───────────────────

    // Every line that reports a duration must also say, in the line, what that duration leaves out — the
    // reader has ui.log, not this source file. This is the guard that matters: each caveat below is one
    // "tidy-up" away from deletion, and deleting it converts an honest subset into a confident answer to
    // a question it never measured.
    public static TheoryData<string> LinesReportingADuration() =>
    [
        LatencyLog.RightClickLine(rebuildMs: 1.3, workingSetBytes: 18 * 1024L * 1024, sincePrevious: TimeSpan.FromHours(4)),
        LatencyLog.BalloonLine("Hyper-V Manager Tray", queueMs: 2.0, handoffMs: 1.0),
    ];

    [Theory]
    [MemberData(nameof(LinesReportingADuration))]
    public void EveryDurationLine_StatesWhatItExcludes(string line)
        => Assert.Contains("xclude", line, StringComparison.Ordinal);

    // The specific over-reading #54 is most exposed to: the rebuild figure being quoted as the menu's.
    // RefreshState is the only thing we can time on the right-click path — H.NotifyIcon builds and tracks
    // the native menu after RightClickCommand returns, with no callback — so this line must actively
    // disclaim menu-open latency rather than merely omit the claim.
    [Fact]
    public void RightClickLine_DisclaimsBeingMenuOpenLatency()
    {
        var line = LatencyLog.RightClickLine(1.3, 18 * 1024L * 1024, TimeSpan.FromHours(4));

        Assert.Contains("NOT menu-open latency", line, StringComparison.Ordinal);
        Assert.Contains("native menu build/paint", line, StringComparison.Ordinal);
        // It reports the rebuild, and says "rebuild" when it does.
        Assert.Contains("menu rebuild 1.3 ms", line, StringComparison.Ordinal);
    }

    // "Shown" must never mean "on screen": ShowNotification returning is a handoff to the shell, and the
    // OS renders legacy balloons on its own schedule (#53). A balloon line that let "shown" stand
    // unqualified would attribute the OS's ~1-2 s to the app's queue, or vice versa.
    [Fact]
    public void BalloonLine_SeparatesQueueDelayFromTheShellHandoff()
    {
        var line = LatencyLog.BalloonLine("Tray — network", queueMs: 240, handoffMs: 0.6);

        Assert.Contains("240 ms in the dispatcher queue", line, StringComparison.Ordinal);
        Assert.Contains("Shell_NotifyIcon returned in 0.6 ms", line, StringComparison.Ordinal);
        Assert.Contains("handed to the shell, not on screen", line, StringComparison.Ordinal);
    }

    // ── Right-click line ────────────────────────────────────────────────────────

    [Fact]
    public void RightClickLine_ReportsTheIdleGapAndWorkingSet_TheTwoCovariates52Needs()
    {
        var line = LatencyLog.RightClickLine(0.9, 151 * 1024L * 1024, TimeSpan.FromSeconds(6));

        Assert.Contains("6 s since previous", line, StringComparison.Ordinal);
        Assert.Contains("working set 151 MB at entry", line, StringComparison.Ordinal);
    }

    // The first right-click of a session has no previous one to measure from. It must say so rather than
    // report a zero gap, which would read as a rapid second open — the exact opposite of the truth, and
    // in the one bucket (#52's "first click after idle") the whole diagnosis turns on.
    [Fact]
    public void RightClickLine_FirstOfSession_SaysSoRatherThanReportingAZeroGap()
    {
        var line = LatencyLog.RightClickLine(1.0, 18 * 1024L * 1024, sincePrevious: null);

        Assert.Contains("first this session", line, StringComparison.Ordinal);
        Assert.DoesNotContain("0 s since previous", line, StringComparison.Ordinal);
    }

    // ── Startup line ────────────────────────────────────────────────────────────

    [Fact]
    public void StartupLine_ReportsElapsedSinceProcessStart()
    {
        var line = LatencyLog.StartupLine("tray icon created", 2043, autoRelaunch: false);

        Assert.Contains("Startup: tray icon created at 2043 ms since process start.", line, StringComparison.Ordinal);
    }

    // A self-heal relaunch sleeps 5 s before creating any UI, on purpose. Unmarked, its milestones look
    // like a 5 s regression against a normal boot's.
    [Fact]
    public void StartupLine_AutoRelaunch_DeclaresTheDeliberateDelayItIncludes()
    {
        var relaunch = LatencyLog.StartupLine("tray icon created", 7100, autoRelaunch: true);
        var normal   = LatencyLog.StartupLine("tray icon created", 2043, autoRelaunch: false);

        Assert.Contains("self-heal relaunch", relaunch, StringComparison.Ordinal);
        Assert.Contains("5 s settle delay", relaunch, StringComparison.Ordinal);
        Assert.DoesNotContain("self-heal", normal, StringComparison.Ordinal);
    }

    // ── Formatting ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "0.0 ms")]
    [InlineData(0.04, "0.0 ms")]
    [InlineData(1.34, "1.3 ms")]
    [InlineData(9.99, "10.0 ms")]     // still the sub-10 branch: rounds within it rather than switching
    [InlineData(10, "10 ms")]
    [InlineData(1234.7, "1235 ms")]
    [InlineData(8000, "8000 ms")]
    public void FormatMs_KeepsOneDecimalOnlyWhereItIsMeaningful(double ms, string expected)
        => Assert.Equal(expected, LatencyLog.FormatMs(ms));

    [Theory]
    [InlineData(0L, "0 MB")]
    [InlineData(15_728_640L, "15 MB")]           // 15 MB — a trimmed process
    [InlineData(158_334_976L, "151 MB")]         // ~151 MB — the same process paged back in
    public void FormatMb_ReportsWholeMegabytes(long bytes, string expected)
        => Assert.Equal(expected, LatencyLog.FormatMb(bytes));

    [Theory]
    [InlineData(0.4, "400 ms")]      // sub-second gaps defer to FormatMs, hence whole ms above 10
    [InlineData(0.004, "4.0 ms")]
    [InlineData(12, "12 s")]
    [InlineData(89, "89 s")]
    [InlineData(90, "2 min")]
    [InlineData(3600, "60 min")]
    [InlineData(5400, "1.5 h")]
    [InlineData(15_120, "4.2 h")]
    public void FormatGap_PicksOneHumanScaleUnit(double totalSeconds, string expected)
        => Assert.Equal(expected, LatencyLog.FormatGap(TimeSpan.FromSeconds(totalSeconds)));

    // InvariantGlobalization is on, and ui.log is read by eye and by grep. A decimal comma from an
    // ambient locale would silently change what every figure above parses as.
    [Fact]
    public void Formatting_IsInvariant_RegardlessOfAmbientCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // nb-NO is Espen's locale and uses a decimal comma — the realistic way this would break.
            CultureInfo.CurrentCulture = new CultureInfo("nb-NO");

            Assert.Equal("1.3 ms", LatencyLog.FormatMs(1.34));
            Assert.Equal("4.2 h", LatencyLog.FormatGap(TimeSpan.FromSeconds(15_120)));
            Assert.DoesNotContain(",", LatencyLog.RightClickLine(1.34, 15_728_640, TimeSpan.FromSeconds(15_120)),
                StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
