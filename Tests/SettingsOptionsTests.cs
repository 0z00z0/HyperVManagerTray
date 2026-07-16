using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
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
    // One style at every magnitude (issue #42) — this row asserted "1h 30m" while the row above it
    // asserted "1 h" and the minutes rows assert "1 min 30 s": the picker mixed two conventions.
    [InlineData(5400, "1 h 30 min")]
    [InlineData(7260, "2 h 1 min")]
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

    // ── SuggestionItems (issue #41) ─────────────────────────────────────────────
    // The pickers are ASSISTIVE, never restrictive. These tests pin that promise: the live values are
    // offered, but a value the live list has never heard of is never dropped — a rule is legitimately
    // written before the switch or VM it names exists, and the host may be offline entirely.

    [Fact]
    public void SuggestionItems_OffersTheLiveValuesSorted()
        => Assert.Equal(["Bridged", "Default Switch", "NAT"],
                        SettingsOptions.SuggestionItems(null, ["NAT", "Bridged", "Default Switch"]));

    [Fact]
    public void SuggestionItems_KeepsACurrentValueTheHostDoesNotHave()
    {
        // The load-bearing case: a rule prepared for a not-yet-created switch must still show its own
        // value — first, so it reads as the current choice rather than buried among the live ones.
        var items = SettingsOptions.SuggestionItems("Not-Yet-Created", ["Bridged", "NAT"]);
        Assert.Equal(["Not-Yet-Created", "Bridged", "NAT"], items);
    }

    [Fact]
    public void SuggestionItems_DoesNotDuplicateACurrentValueTheHostAlsoHas()
        => Assert.Equal(["Bridged", "NAT"], SettingsOptions.SuggestionItems("Bridged", ["NAT", "Bridged"]));

    [Fact]
    public void SuggestionItems_CurrentValueMatchesLiveCaseInsensitively()
        // "bridged" and "Bridged" are the same switch — offering both would invite the typo this removes.
        => Assert.Equal(["Bridged"], SettingsOptions.SuggestionItems("bridged", ["Bridged"]));

    [Fact]
    public void SuggestionItems_DropsBlanksAndDeduplicatesLiveValues()
        => Assert.Equal(["Bridged"], SettingsOptions.SuggestionItems("  ", ["Bridged", "  ", "bridged", ""]));

    [Fact]
    public void SuggestionItems_TrimsTheCurrentValue()
        => Assert.Equal(["Bridged"], SettingsOptions.SuggestionItems("  Bridged  ", []));

    [Fact]
    public void SuggestionItems_NoLiveValues_YieldsJustTheCurrentOne()
        // Host offline / enumeration failed: the picker degrades to the text box it replaced, and the
        // user's own value is still there. This is a supported state, not an error.
        => Assert.Equal(["Bridged"], SettingsOptions.SuggestionItems("Bridged", null));

    [Fact]
    public void SuggestionItems_NothingAtAll_IsEmptyNotNull()
        => Assert.Empty(SettingsOptions.SuggestionItems(null, null));

    // ── NormalizeNicName (issue #41) ────────────────────────────────────────────

    [Theory]
    [InlineData(null,             "Network Adapter")]
    [InlineData("",               "Network Adapter")]
    [InlineData("   ",            "Network Adapter")]
    [InlineData("Ethernet 2",     "Ethernet 2")]
    [InlineData("  Ethernet 2  ", "Ethernet 2")]
    [InlineData("vNIC — LAN",     "vNIC — LAN")]
    public void NormalizeNicName_BlankBecomesTheDefaultAndTheRestIsPreserved(string? input, string expected)
        => Assert.Equal(expected, SettingsOptions.NormalizeNicName(input));

    // ── AppendVmLine (issue #41) ────────────────────────────────────────────────
    // Picking a VM from the host must ADD to what the user has, never replace it.

    [Fact]
    public void AppendVmLine_AddsToAnEmptyBox()
        => Assert.Equal("Alpha", SettingsOptions.AppendVmLine("", "Alpha"));

    [Fact]
    public void AppendVmLine_AppendsWithoutDiscardingExistingNames()
        => Assert.Equal(["Alpha", "Beta"],
                        SettingsOptions.ParseVmLines(SettingsOptions.AppendVmLine("Alpha", "Beta")));

    [Fact]
    public void AppendVmLine_AlreadyListed_IsUnchanged()
        => Assert.Equal("Alpha", SettingsOptions.AppendVmLine("Alpha", "Alpha"));

    [Fact]
    public void AppendVmLine_AlreadyListedInAnotherCase_IsUnchanged()
        => Assert.Equal("Alpha", SettingsOptions.AppendVmLine("Alpha", "ALPHA"));

    [Fact]
    public void AppendVmLine_BlankName_IsUnchanged()
        => Assert.Equal("Alpha", SettingsOptions.AppendVmLine("Alpha", "  "));

    [Fact]
    public void AppendVmLine_PreservesAVmNameContainingAComma()
        // The newline representation (fix 8) must survive the picker too.
        => Assert.Equal(["Web, App", "Db"],
                        SettingsOptions.ParseVmLines(SettingsOptions.AppendVmLine("Web, App", "Db")));

    [Fact]
    public void AppendVmLine_PickedValueSerialisesIdenticallyToTheHandTypedEquivalent()
    {
        // Acceptance criterion: a value chosen from a picker round-trips exactly as if it were typed.
        var picked    = SettingsOptions.ParseVmLines(SettingsOptions.AppendVmLine("Alpha", "Beta"));
        var handTyped = SettingsOptions.ParseVmLines("Alpha\r\nBeta");
        Assert.Equal(handTyped, picked);
    }

    // ── The on-bridge-lost delay the monitor actually honours ────────────────────

    /// <summary>
    /// THE defect: 0 is a real, user-selectable delay ("Immediate"), not a "not set" sentinel. The
    /// monitor read it as <c>delay &gt; 0 ? delay : 30</c> and waited 30 s instead — so a VM the user
    /// told to pause immediately on losing the bridge sat running for half a minute, and the picker
    /// stated a delay the app did not honour.
    /// </summary>
    [Fact]
    public void EffectiveBridgeLostDelaySeconds_ZeroMeansImmediateNotUnset()
    {
        var vm = new VmTarget { Name = "Alpha", OnBridgeLostAction = "pause", OnBridgeLostDelaySeconds = 0 };

        Assert.Equal(0, SettingsOptions.EffectiveBridgeLostDelaySeconds(vm));
        Assert.Equal("Immediate", SettingsOptions.FormatDelay(SettingsOptions.EffectiveBridgeLostDelaySeconds(vm)));
    }

    /// <summary>"Unset" needs no sentinel: the model's own default supplies 30, so an omitted value
    /// never reaches the monitor as 0. This is why dropping the ternary loses nothing.</summary>
    [Fact]
    public void EffectiveBridgeLostDelaySeconds_OmittedValueStillDefaultsTo30() =>
        Assert.Equal(30, SettingsOptions.EffectiveBridgeLostDelaySeconds(new VmTarget { Name = "Alpha" }));

    // Every value the picker offers must survive to the monitor exactly as chosen.
    [Fact]
    public void EffectiveBridgeLostDelaySeconds_EveryPresetSurvivesUnchanged()
    {
        foreach (var preset in SettingsOptions.BridgeLostDelaySeconds)
        {
            var vm = new VmTarget { Name = "Alpha", OnBridgeLostDelaySeconds = preset };
            Assert.Equal(preset, SettingsOptions.EffectiveBridgeLostDelaySeconds(vm));
        }
    }

    // A hand-edited negative is still clamped to the model default, and an absurd value to the cap —
    // the monitor and the picker apply the same clamp because they call the same helper.
    [Theory]
    [InlineData(-5, 30)]
    [InlineData(999_999, 86_400)]
    public void EffectiveBridgeLostDelaySeconds_ClampsHandEditedValues(int stored, int expected) =>
        Assert.Equal(expected, SettingsOptions.EffectiveBridgeLostDelaySeconds(
            new VmTarget { Name = "Alpha", OnBridgeLostDelaySeconds = stored }));
}
