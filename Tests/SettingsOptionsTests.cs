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

    // ── Network rules editor helpers (issue #23) ─────────────────────────────────

    [Theory]
    [InlineData(null,                 true)]   // blank = "don't match on MAC"
    [InlineData("",                   true)]
    [InlineData("   ",                true)]
    [InlineData("AA:BB:CC:DD:EE:FF",  true)]
    [InlineData("aa-bb-cc-dd-ee-ff",  true)]
    [InlineData("AABBCCDDEEFF",       true)]
    [InlineData("AA:BB:CC:DD:EE",     false)]  // too short
    [InlineData("GG:BB:CC:DD:EE:FF",  false)]  // non-hex
    [InlineData("not-a-mac",          false)]
    public void IsValidMac_AcceptsWellFormedOrBlank(string? mac, bool expected)
        => Assert.Equal(expected, SettingsOptions.IsValidMac(mac));

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "AA:BB:CC:DD:EE:FF")]
    [InlineData("AABBCCDDEEFF",      "AA:BB:CC:DD:EE:FF")]
    [InlineData("aa-bb-cc-dd-ee-ff", "AA:BB:CC:DD:EE:FF")]
    public void CanonicalizeMac_NormalisesToColonUpper(string input, string expected)
        => Assert.Equal(expected, SettingsOptions.CanonicalizeMac(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanonicalizeMac_BlankBecomesNull(string? input)
        => Assert.Null(SettingsOptions.CanonicalizeMac(input));

    [Fact]
    public void CanonicalizeMac_InProgressValueNotMangled()
        // A value that isn't yet a valid MAC is returned trimmed, not silently dropped (the UI gates
        // saving on IsValidMac, so a partial value is never persisted, but it must survive display).
        => Assert.Equal("AA:BB", SettingsOptions.CanonicalizeMac("  AA:BB  "));

    [Theory]
    [InlineData(null,             true)]   // blank = "don't match on IP"
    [InlineData("",               true)]
    [InlineData("10.0.0.0/23",    true)]
    [InlineData("192.168.1.0/24", true)]
    [InlineData("10.0.0.1/32",    true)]
    [InlineData("0.0.0.0/0",      true)]
    [InlineData("10.0.0.0/33",    false)]  // prefix > 32
    [InlineData("10.0.0.0/-1",    false)]
    [InlineData("10.0.0.0",       false)]  // no prefix
    [InlineData("not/24",         false)]
    [InlineData("999.0.0.0/24",   false)]  // bad octet
    public void IsValidCidr_AcceptsWellFormedOrBlank(string? cidr, bool expected)
        => Assert.Equal(expected, SettingsOptions.IsValidCidr(cidr));

    [Theory]
    [InlineData("VM1, VM2, VM3",   new[] { "VM1", "VM2", "VM3" })]
    [InlineData(" VM1 ,VM2,, VM1", new[] { "VM1", "VM2" })]        // trims, drops blanks, dedupes
    [InlineData("VM1\nVM2\r\nVM3", new[] { "VM1", "VM2", "VM3" })] // newline-separated too
    [InlineData("",                new string[0])]
    [InlineData(null,              new string[0])]
    public void ParseVmList_CleansAndDedupes(string? text, string[] expected)
        => Assert.Equal(expected, SettingsOptions.ParseVmList(text));

    [Fact]
    public void ParseVmList_DedupeIsCaseInsensitiveFirstSpellingWins()
        => Assert.Equal(["Alpha"], SettingsOptions.ParseVmList("Alpha, alpha, ALPHA"));

    [Fact]
    public void JoinVmList_RoundTripsWithParse()
    {
        var original = new[] { "Alpha", "Beta", "Gamma" };
        Assert.Equal(original, SettingsOptions.ParseVmList(SettingsOptions.JoinVmList(original)));
    }

    // ── Newline-only VM list (fix 8: the editor representation) ──────────────────

    [Theory]
    [InlineData("VM1\nVM2\nVM3",   new[] { "VM1", "VM2", "VM3" })]
    [InlineData("VM1\r\nVM2\r\n",  new[] { "VM1", "VM2" })]
    [InlineData(" VM1 \n VM1 \nVM2", new[] { "VM1", "VM2" })]      // trims, dedupes
    [InlineData("",                new string[0])]
    [InlineData(null,              new string[0])]
    public void ParseVmLines_SplitsOnNewlinesOnly(string? text, string[] expected)
        => Assert.Equal(expected, SettingsOptions.ParseVmLines(text));

    [Fact]
    public void ParseVmLines_DoesNotSplitOnComma()
        // The whole point of the newline representation: a VM name containing a comma is ONE entry.
        => Assert.Equal(["Web, App"], SettingsOptions.ParseVmLines("Web, App"));

    [Fact]
    public void JoinVmLines_RoundTripsAVmNameContainingAComma()
    {
        // "Web, App" corrupts through the comma-based Join/Parse; the newline pair preserves it.
        var original = new[] { "Web, App", "Db" };
        Assert.Equal(original, SettingsOptions.ParseVmLines(SettingsOptions.JoinVmLines(original)));
    }

    [Theory]
    [InlineData(-5,      0)]
    [InlineData(0,       0)]
    [InlineData(100,     100)]
    [InlineData(200_000, 100_000)]
    public void NormalizePriority_Clamps(int input, int expected)
        => Assert.Equal(expected, SettingsOptions.NormalizePriority(input));
}
