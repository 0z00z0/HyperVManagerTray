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
    [InlineData(-5,      0)]
    [InlineData(0,       0)]
    [InlineData(100,     100)]
    [InlineData(200_000, 100_000)]
    public void NormalizePriority_Clamps(int input, int expected)
        => Assert.Equal(expected, SettingsOptions.NormalizePriority(input));

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
        var vm = new VmTarget { Id = "Alpha", Name = "Alpha", OnBridgeLostAction = "pause", OnBridgeLostDelaySeconds = 0 };

        Assert.Equal(0, SettingsOptions.EffectiveBridgeLostDelaySeconds(vm));
        Assert.Equal("Immediate", SettingsOptions.FormatDelay(SettingsOptions.EffectiveBridgeLostDelaySeconds(vm)));
    }

    /// <summary>"Unset" needs no sentinel: the model's own default supplies 30, so an omitted value
    /// never reaches the monitor as 0. This is why dropping the ternary loses nothing.</summary>
    [Fact]
    public void EffectiveBridgeLostDelaySeconds_OmittedValueStillDefaultsTo30() =>
        Assert.Equal(30, SettingsOptions.EffectiveBridgeLostDelaySeconds(new VmTarget { Id = "Alpha", Name = "Alpha" }));

    // Every value the picker offers must survive to the monitor exactly as chosen.
    [Fact]
    public void EffectiveBridgeLostDelaySeconds_EveryPresetSurvivesUnchanged()
    {
        foreach (var preset in SettingsOptions.BridgeLostDelaySeconds)
        {
            var vm = new VmTarget { Id = "Alpha", Name = "Alpha", OnBridgeLostDelaySeconds = preset };
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
            new VmTarget { Id = "Alpha", Name = "Alpha", OnBridgeLostDelaySeconds = stored }));
    // ── IsPersistableRule: never write an inert catch-all (the #31-batch review) ─────────

    /// <summary>
    /// The exact object Settings' "Add rule" button used to persist the instant it was clicked, before
    /// the user had typed a character. It must not reach config.json.
    /// </summary>
    private static NetworkRule BlankTemplate() =>
        new() { Name = "New rule", Priority = 100, SwitchId = "" };

    /// <summary>
    /// Why this matters, in one test. A rule with no conditions matches EVERY network — that is
    /// AdapterMatcher.MatchingNic's documented contract, not an accident — so the blank template wins
    /// evaluation ahead of every real rule the user goes on to write, while its blank switch binds
    /// nothing. Red icon, no reconnects, and because the active rule's name is then never "Fallback",
    /// App.HandleBridgeTransition's bridgeJustLost edge never fires either: every VM's configured
    /// on-bridge-lost pause/save/shutdown silently stops running. Issue #38's empty default config makes
    /// this the ONLY rule on a fresh install — i.e. the first thing a new user clicks breaks the app,
    /// quietly.
    /// </summary>
    [Fact]
    public void IsPersistableRule_RejectsTheBlankAddRuleTemplate()
    {
        var blank = BlankTemplate();

        Assert.True(SettingsOptions.DeclaresNoCondition(blank), "the blank template matches every network");
        Assert.False(SettingsOptions.IsPersistableRule(blank));
    }

    /// <summary>A rule needs a switch to bind AND a condition saying when. Neither half alone is enough:
    /// a conditionless rule is a catch-all however good its switch, and a switchless one binds nothing
    /// however precise its conditions.</summary>
    [Fact]
    public void IsPersistableRule_NeedsBothASwitchAndACondition()
    {
        var conditionOnly = BlankTemplate();
        conditionOnly.Conditions.IpCidr = "10.0.0.0/24";
        Assert.False(SettingsOptions.IsPersistableRule(conditionOnly));   // no switch to bind

        var switchOnly = BlankTemplate();
        switchOnly.SwitchId = "Bridged";
        Assert.False(SettingsOptions.IsPersistableRule(switchOnly));      // matches everything

        var complete = BlankTemplate();
        complete.SwitchId = "Bridged";
        complete.Conditions.IpCidr = "10.0.0.0/24";
        Assert.True(SettingsOptions.IsPersistableRule(complete));         // says what and when

        var byMac = BlankTemplate();
        byMac.SwitchId = "Bridged";
        byMac.Conditions.AdapterMac = "AA:BB:CC:DD:EE:FF";
        Assert.True(SettingsOptions.IsPersistableRule(byMac));            // either condition qualifies
    }

    /// <summary>
    /// Judge the CLEANED rule. ConfigManager.CleanRule drops a malformed MAC/CIDR to null, so a rule
    /// whose only condition doesn't parse becomes a catch-all the moment it is written — it must be
    /// treated as one here rather than on the strength of the unparseable text the user typed.
    /// </summary>
    [Fact]
    public void IsPersistableRule_TreatsARuleWhoseOnlyConditionIsMalformedAsACatchAll()
    {
        var rule = BlankTemplate();
        rule.SwitchId = "Bridged";
        rule.Conditions.IpCidr = "not-a-cidr";

        // Believing the typed text, this looks like it declares a condition …
        Assert.False(SettingsOptions.DeclaresNoCondition(rule));
        // … but what would actually be persisted has none at all, so it matches every network.
        var cleaned = HyperVManagerTray.Services.ConfigManager.CleanRule(rule);
        Assert.True(SettingsOptions.DeclaresNoCondition(cleaned));
        Assert.False(SettingsOptions.IsPersistableRule(cleaned));
    }

    /// <summary>Blank strings are not conditions — "" must not read as "match on this".</summary>
    [Fact]
    public void DeclaresNoCondition_TreatsBlanksAsNoCondition()
    {
        var rule = BlankTemplate();
        rule.SwitchId = "Bridged";
        rule.Conditions.AdapterMac = "";
        rule.Conditions.IpCidr     = "   ";

        Assert.True(SettingsOptions.DeclaresNoCondition(rule));
        Assert.False(SettingsOptions.IsPersistableRule(rule));
    }

    // ── The collapsed rule row's summary line ───────────────────────────────────

    /// <summary>
    /// The refusals a collapsed rule row has to show, because they are the only place a rule that binds
    /// nothing is visible without opening every rule in turn. A rule naming a switch this host does not
    /// have looks complete in the editor and does nothing at all in use.
    /// </summary>
    [Fact]
    public void DescribeRule_SaysWhyARuleCannotWork()
    {
        var onHost = new[] { new HostSwitch("{11111111-1111-1111-1111-111111111111}", "Bridged") };

        var noSwitch = BlankTemplate();
        noSwitch.Conditions.IpCidr = "10.0.0.0/23";
        Assert.Contains("Cannot work: no virtual switch is chosen.",
                        SettingsOptions.DescribeRule(noSwitch, hostReadable: true, onHost));

        var missing = BlankTemplate();
        missing.Conditions.IpCidr = "10.0.0.0/23";
        missing.SwitchId   = "{99999999-9999-9999-9999-999999999999}";
        missing.SwitchName = "Lab";
        Assert.Contains("Cannot work: the switch “Lab” is not on this host.",
                        SettingsOptions.DescribeRule(missing, hostReadable: true, onHost));

        // An unreadable host says nothing about which switches exist, so the refusal is withheld rather
        // than asserted from an empty list — the row must not accuse a switch of being missing because
        // Virtual Machine Management happened to be stopped.
        Assert.DoesNotContain("Cannot work",
                              SettingsOptions.DescribeRule(missing, hostReadable: false, []));
    }
}
