using HyperVManagerTray.Helpers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Tests for <see cref="SettingsOptions"/> — the pure config↔UI mapping behind the Settings window
/// (issue #18). The load-bearing guarantee is a lossless round-trip: a stored value maps to a picker
/// index and back to the same canonical value, and a hand-edited/unknown value degrades predictably
/// rather than being silently dropped.
/// </summary>
public class SettingsOptionsTests
{
    // ── Bridge-lost action ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null,        null)]
    [InlineData("",          null)]
    [InlineData("none",      null)]
    [InlineData("None",      null)]
    [InlineData("  none  ",  null)]
    [InlineData("bogus",     null)]
    [InlineData("pause",     "pause")]
    [InlineData("PAUSE",     "pause")]
    [InlineData(" Save ",    "save")]
    [InlineData("shutdown",  "shutdown")]
    public void NormalizeBridgeLostAction_CanonicalisesOrDropsUnknown(string? input, string? expected)
        => Assert.Equal(expected, SettingsOptions.NormalizeBridgeLostAction(input));

    [Theory]
    [InlineData(null,       0)]
    [InlineData("none",     0)]
    [InlineData("pause",    1)]
    [InlineData("save",     2)]
    [InlineData("shutdown", 3)]
    [InlineData("garbage",  0)]
    public void BridgeLostActionToIndex_MapsToRow(string? action, int expectedIndex)
        => Assert.Equal(expectedIndex, SettingsOptions.BridgeLostActionToIndex(action));

    [Theory]
    [InlineData("pause")]
    [InlineData("save")]
    [InlineData("shutdown")]
    [InlineData(null)]
    public void BridgeLostAction_IndexRoundTrips(string? action)
    {
        int index = SettingsOptions.BridgeLostActionToIndex(action);
        var back  = SettingsOptions.IndexToBridgeLostAction(index);
        Assert.Equal(SettingsOptions.NormalizeBridgeLostAction(action), back);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(-1)]
    public void IndexToBridgeLostAction_OutOfRange_IsNull(int index)
        => Assert.Null(SettingsOptions.IndexToBridgeLostAction(index));

    [Fact]
    public void IndexToBridgeLostAction_PastEnd_IsNull()
        => Assert.Null(SettingsOptions.IndexToBridgeLostAction(SettingsOptions.BridgeLostActions.Count));

    // ── Delay ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0,      0)]
    [InlineData(30,     30)]
    [InlineData(86_400, 86_400)]
    [InlineData(999_999, 86_400)]   // clamped to the ceiling
    [InlineData(-1,     30)]        // negative → model default
    public void NormalizeDelaySeconds_ClampsAndDefaults(int input, int expected)
        => Assert.Equal(expected, SettingsOptions.NormalizeDelaySeconds(input));

    [Theory]
    [InlineData(0,   "Immediate")]
    [InlineData(5,   "5 s")]
    [InlineData(45,  "45 s")]
    [InlineData(60,  "1 min")]
    [InlineData(90,  "1 min 30 s")]
    [InlineData(300, "5 min")]
    [InlineData(3600, "1 h")]
    [InlineData(5400, "1h 30m")]
    public void FormatDelay_ReadsNaturally(int seconds, string expected)
        => Assert.Equal(expected, SettingsOptions.FormatDelay(seconds));

    // ── Log level ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    [InlineData(LogLevel.None)]
    public void LogLevel_IndexRoundTrips(LogLevel level)
    {
        int index = SettingsOptions.LogLevelToIndex(level);
        Assert.Equal(level, SettingsOptions.IndexToLogLevel(index));
    }

    [Fact]
    public void LogLevelToIndex_UnknownDefaultsToDebug()
    {
        // LogLevel has no value 99; the mapping must fall back to Debug's row rather than -1.
        int debugIndex = SettingsOptions.LogLevelToIndex(LogLevel.Debug);
        Assert.Equal(debugIndex, SettingsOptions.LogLevelToIndex((LogLevel)99));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    public void IndexToLogLevel_OutOfRange_IsDebug(int index)
        => Assert.Equal(LogLevel.Debug, SettingsOptions.IndexToLogLevel(index));

    [Fact]
    public void LogLevels_CoverEveryEnumMember()
    {
        // A missing member would make that level unselectable in the picker.
        foreach (LogLevel level in Enum.GetValues<LogLevel>())
            Assert.Contains(SettingsOptions.LogLevels, o => o.Value == level);
    }
}
