using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Unit tests for the pure adapter-rename logic (issue #15) — the injection-defense and
/// wrong-device-defense surface. Only <see cref="AdapterNameRules"/> is exercised; the device-mutating
/// SetupAPI path (<c>AdapterRenamer</c>) is intentionally never linked or invoked here.
/// </summary>
public class AdapterRenameTests
{
    // ── ValidateName: acceptance ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Home")]
    [InlineData("Office LAN")]
    [InlineData("Dock 1")]
    [InlineData("Petterhagen - Dell docking")]
    [InlineData("Realtek USB GbE Family Controller #2")]
    [InlineData("Wi-Fi (5GHz)")]
    [InlineData("net_work.01")]
    [InlineData("ABCdef123")]
    public void ValidateName_AcceptsAllowedNames(string input)
    {
        var v = AdapterNameRules.ValidateName(input);
        Assert.True(v.IsValid, v.Error);
        Assert.Equal(input.Trim(), v.Sanitized);
    }

    [Fact]
    public void ValidateName_TrimsSurroundingWhitespace()
    {
        var v = AdapterNameRules.ValidateName("   Home network   ");
        Assert.True(v.IsValid);
        Assert.Equal("Home network", v.Sanitized);
    }

    [Fact]
    public void ValidateName_AcceptsExactly200Chars()
    {
        var v = AdapterNameRules.ValidateName(new string('a', 200));
        Assert.True(v.IsValid);
    }

    // ── ValidateName: rejection ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ValidateName_RejectsEmptyOrWhitespace(string? input)
    {
        var v = AdapterNameRules.ValidateName(input);
        Assert.False(v.IsValid);
        Assert.NotNull(v.Error);
    }

    [Fact]
    public void ValidateName_RejectsOverLength()
    {
        var v = AdapterNameRules.ValidateName(new string('a', 201));
        Assert.False(v.IsValid);
    }

    [Theory]
    [InlineData("bad/name")]     // path separator
    [InlineData("bad\\name")]    // path separator
    [InlineData("a; rm -rf")]    // shell metacharacters
    [InlineData("a & b")]        // ampersand
    [InlineData("a | b")]        // pipe
    [InlineData("$(whoami)")]    // command substitution
    [InlineData("a`b`")]         // backtick
    [InlineData("na:me")]        // colon
    [InlineData("na\"me")]       // quote
    [InlineData("na'me")]        // apostrophe
    [InlineData("na<me>")]       // angle brackets
    [InlineData("na@me")]        // at sign
    [InlineData("na%me")]        // percent
    [InlineData("na*me")]        // wildcard
    public void ValidateName_RejectsDisallowedCharacters(string input)
    {
        var v = AdapterNameRules.ValidateName(input);
        Assert.False(v.IsValid);
        Assert.NotNull(v.Error);
    }

    [Theory]
    [InlineData("line1\nline2")] // newline
    [InlineData("tab\there")]    // tab in the middle
    [InlineData("nul\0byte")]    // embedded NUL
    public void ValidateName_RejectsControlCharacters(string input)
    {
        var v = AdapterNameRules.ValidateName(input);
        Assert.False(v.IsValid);
    }

    // ── IsNameUnique ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsNameUnique_TrueWhenNoCollision()
        => Assert.True(AdapterNameRules.IsNameUnique("Dock 2", new[] { "Dock 1", "Wi-Fi" }));

    [Fact]
    public void IsNameUnique_FalseOnExactCollision()
        => Assert.False(AdapterNameRules.IsNameUnique("Dock 1", new[] { "Dock 1", "Wi-Fi" }));

    [Fact]
    public void IsNameUnique_IsCaseInsensitiveAndTrims()
        => Assert.False(AdapterNameRules.IsNameUnique("  dock 1 ", new[] { "DOCK 1" }));

    [Fact]
    public void IsNameUnique_TrueAgainstEmptyList()
        => Assert.True(AdapterNameRules.IsNameUnique("Anything", System.Array.Empty<string>()));

    // ── ChooseDisplayName (which string the UI shows, issue #32) ─────────────────
    //
    // The bug: the rename writes the device FriendlyName, but every UI surface displayed
    // NetworkInterface.Description — a different property that a FriendlyName write never changes — so
    // a successful rename was invisible in the app. These tests pin the decision: FriendlyName wins
    // when present, and EVERY unavailable case (absent / ambiguous / unreadable, all of which the
    // caller signals as null) degrades to the description rather than throwing or blanking.

    [Fact]
    public void ChooseDisplayName_PrefersFriendlyNameOverDescription()
        => Assert.Equal(
            "Dell docking (Petterhaugen)",
            AdapterNameRules.ChooseDisplayName(
                "Dell docking (Petterhaugen)", "Realtek USB GbE Family Controller #2"));

    [Fact]
    public void ChooseDisplayName_FallsBackToDescriptionWhenFriendlyNameAbsent()
        => Assert.Equal(
            "Realtek USB GbE Family Controller #2",
            AdapterNameRules.ChooseDisplayName(null, "Realtek USB GbE Family Controller #2"));

    /// <summary>
    /// A device resolving to 0 or &gt;1 entries, or an unreadable Enum key, reaches this method as a
    /// null FriendlyName — indistinguishable from "no explicit FriendlyName", and handled identically.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ChooseDisplayName_FallsBackWhenFriendlyNameIsMissingOrBlank(string? friendlyName)
        => Assert.Equal(
            "Intel(R) Ethernet Connection",
            AdapterNameRules.ChooseDisplayName(friendlyName, "Intel(R) Ethernet Connection"));

    [Fact]
    public void ChooseDisplayName_TrimsTheChosenFriendlyName()
        => Assert.Equal("Office dock", AdapterNameRules.ChooseDisplayName("  Office dock  ", "Realtek"));

    [Fact]
    public void ChooseDisplayName_TrimsTheFallbackDescription()
        => Assert.Equal("Realtek", AdapterNameRules.ChooseDisplayName(null, "  Realtek  "));

    /// <summary>Unreachable in practice, but the display must never be blank.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData("", "   ")]
    public void ChooseDisplayName_NeverReturnsBlank(string? friendlyName, string? description)
    {
        var name = AdapterNameRules.ChooseDisplayName(friendlyName, description);
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.Equal(AdapterNameRules.UnknownDisplayName, name);
    }

    /// <summary>
    /// A FriendlyName equal to the factory description is still returned (the user is free to rename an
    /// adapter to its own description) — the method never second-guesses an explicitly present value.
    /// </summary>
    [Fact]
    public void ChooseDisplayName_ReturnsFriendlyNameEvenWhenEqualToDescription()
        => Assert.Equal("Realtek", AdapterNameRules.ChooseDisplayName("Realtek", "Realtek"));

    /// <summary>
    /// End-to-end of the reported case: the resolved device has a FriendlyName, so the picker/tray show
    /// it instead of the IP-Helper description that the rename provably never touched.
    /// </summary>
    [Fact]
    public void ChooseDisplayName_ShowsRenamedNameForTheReportedAdapter()
    {
        var entries = new[]
        {
            new AdapterNameRules.ClassAdapterEntry(
                "{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", @"USB\VID_0BDA&PID_8153\000002000000"),
        };

        var resolution = AdapterNameRules.ResolveDeviceInstanceId(
            "{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", entries);
        Assert.True(resolution.Success);

        // Stands in for AdapterRenamer.ReadFriendlyName(resolution.DeviceInstanceId) => (true, "…").
        var name = AdapterNameRules.ChooseDisplayName(
            "Dell docking (Petterhaugen)", "Realtek USB GbE Family Controller #2");

        Assert.Equal("Dell docking (Petterhaugen)", name);
    }

    /// <summary>An ambiguous device is never guessed at — the display falls back to the description.</summary>
    [Fact]
    public void ChooseDisplayName_FallsBackWhenDeviceResolutionIsAmbiguous()
    {
        var entries = new[]
        {
            new AdapterNameRules.ClassAdapterEntry("{AAAA0000-0000-0000-0000-000000000000}", @"USB\DEV\1"),
            new AdapterNameRules.ClassAdapterEntry("{AAAA0000-0000-0000-0000-000000000000}", @"USB\DEV\2"),
        };

        var resolution = AdapterNameRules.ResolveDeviceInstanceId(
            "{AAAA0000-0000-0000-0000-000000000000}", entries);
        Assert.False(resolution.Success);

        // Resolution failed, so the caller passes null — no FriendlyName is ever read.
        Assert.Equal(
            "Realtek USB GbE Family Controller #2",
            AdapterNameRules.ChooseDisplayName(null, "Realtek USB GbE Family Controller #2"));
    }

    // ── FriendlyNameApplied (write read-back verdict, issue #15) ─────────────────

    [Fact]
    public void FriendlyNameApplied_TrueWhenPresentAndExactMatch()
        => Assert.True(AdapterNameRules.FriendlyNameApplied(true, "Dell docking (Petterhaugen)", "Dell docking (Petterhaugen)"));

    [Fact]
    public void FriendlyNameApplied_FalseWhenAbsent()
        => Assert.False(AdapterNameRules.FriendlyNameApplied(false, null, "Dell docking (Petterhaugen)"));

    [Fact]
    public void FriendlyNameApplied_FalseWhenValueDiffers()
        => Assert.False(AdapterNameRules.FriendlyNameApplied(true, "Realtek USB GbE Family Controller #2", "Dell docking (Petterhaugen)"));

    [Fact]
    public void FriendlyNameApplied_FalseWhenPresentButNull()
        => Assert.False(AdapterNameRules.FriendlyNameApplied(true, null, "Dell docking"));

    [Fact]
    public void FriendlyNameApplied_IsCaseSensitiveOrdinal()
        => Assert.False(AdapterNameRules.FriendlyNameApplied(true, "dell docking", "Dell docking"));

    [Fact]
    public void FriendlyNameApplied_FalseWhenTruncatedToSingleChar()
        // The exact ANSI-marshaling corruption the fix guards against: a UTF-16 buffer fed to the
        // ANSI SetupAPI entry point would land as the first character only ("D"). Read-back rejects it.
        => Assert.False(AdapterNameRules.FriendlyNameApplied(true, "D", "Dell docking (Petterhaugen)"));

    // ── ResolveDeviceInstanceId ──────────────────────────────────────────────────

    private static AdapterNameRules.ClassAdapterEntry Entry(string guid, string dev)
        => new(guid, dev);

    [Fact]
    public void Resolve_SingleMatch_Succeeds()
    {
        var entries = new[]
        {
            Entry("{FB095B22-2E76-4DEC-BA3F-0084EA984B38}", "USB\\VID_0BDA&PID_8153\\001000001"),
            Entry("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", "USB\\VID_0BDA&PID_8153\\000002000000"),
        };

        var r = AdapterNameRules.ResolveDeviceInstanceId("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", entries);

        Assert.True(r.Success);
        Assert.Equal("USB\\VID_0BDA&PID_8153\\000002000000", r.DeviceInstanceId);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveOnGuid()
    {
        var entries = new[] { Entry("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", "DEV\\1") };
        var r = AdapterNameRules.ResolveDeviceInstanceId("{becde8f3-29f7-41e4-9862-8097b2bb14ef}", entries);
        Assert.True(r.Success);
        Assert.Equal("DEV\\1", r.DeviceInstanceId);
    }

    [Fact]
    public void Resolve_NoMatch_Aborts()
    {
        var entries = new[] { Entry("{AAAAAAAA-0000-0000-0000-000000000000}", "DEV\\1") };
        var r = AdapterNameRules.ResolveDeviceInstanceId("{BBBBBBBB-0000-0000-0000-000000000000}", entries);
        Assert.False(r.Success);
        Assert.Null(r.DeviceInstanceId);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Resolve_MultipleDistinctDevices_Aborts()
    {
        // Same GUID pointing at two DIFFERENT device instances — must never guess.
        var entries = new[]
        {
            Entry("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", "DEV\\1"),
            Entry("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", "DEV\\2"),
        };
        var r = AdapterNameRules.ResolveDeviceInstanceId("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", entries);
        Assert.False(r.Success);
        Assert.Null(r.DeviceInstanceId);
    }

    [Fact]
    public void Resolve_DuplicateSameDevice_CollapsesToSuccess()
    {
        // Same GUID + same device listed twice → still a single unambiguous target.
        var entries = new[]
        {
            Entry("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", "DEV\\1"),
            Entry("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", "DEV\\1"),
        };
        var r = AdapterNameRules.ResolveDeviceInstanceId("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", entries);
        Assert.True(r.Success);
        Assert.Equal("DEV\\1", r.DeviceInstanceId);
    }

    [Fact]
    public void Resolve_IgnoresEntriesWithBlankDeviceId()
    {
        var entries = new[]
        {
            Entry("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", "   "),
            Entry("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", "DEV\\real"),
        };
        var r = AdapterNameRules.ResolveDeviceInstanceId("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", entries);
        Assert.True(r.Success);
        Assert.Equal("DEV\\real", r.DeviceInstanceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankInterfaceGuid_Aborts(string? guid)
    {
        var entries = new[] { Entry("{BECDE8F3-29F7-41E4-9862-8097B2BB14EF}", "DEV\\1") };
        var r = AdapterNameRules.ResolveDeviceInstanceId(guid!, entries);
        Assert.False(r.Success);
    }

    // ── ParseFactoryDescription: the INF-indirect form (issue #33) ───────────────

    /// <summary>The exact value from the reporting machine — the case that motivated issue #33.</summary>
    [Fact]
    public void ParseFactory_InfIndirect_ReturnsLiteralAfterSeparator()
    {
        Assert.Equal(
            "Realtek USB GbE Family Controller",
            AdapterNameRules.ParseFactoryDescription(
                @"@oem241.inf,%rtl8153.devicedesc%;Realtek USB GbE Family Controller"));
    }

    [Theory]
    [InlineData(@"@netrtwlane.inf,%rtlwlan.devicedesc%;Realtek 8822CE Wireless LAN 802.11ac PCI-E NIC",
                "Realtek 8822CE Wireless LAN 802.11ac PCI-E NIC")]
    [InlineData(@"@net1ic.inf,%e1000.devicedesc%;Intel(R) Ethernet Connection I219-LM",
                "Intel(R) Ethernet Connection I219-LM")]
    public void ParseFactory_InfIndirect_RealWorldShapes(string raw, string expected)
        => Assert.Equal(expected, AdapterNameRules.ParseFactoryDescription(raw));

    /// <summary>
    /// The literal is taken from the FIRST ';' — the INF-indirect format has exactly one separator, and
    /// the '@inf,%key%' prefix can never contain one. Splitting on the LAST ';' would silently truncate
    /// a display name that legitimately contains a semicolon.
    /// </summary>
    [Fact]
    public void ParseFactory_InfIndirect_KeepsSemicolonInsideDisplayName()
    {
        Assert.Equal(
            "Acme NIC; Model 5; rev B",
            AdapterNameRules.ParseFactoryDescription(@"@acme.inf,%acme.devicedesc%;Acme NIC; Model 5; rev B"));
    }

    [Fact]
    public void ParseFactory_InfIndirect_TrimsAroundLiteral()
    {
        Assert.Equal(
            "Realtek USB GbE Family Controller",
            AdapterNameRules.ParseFactoryDescription(@"  @oem241.inf,%k%;   Realtek USB GbE Family Controller   "));
    }

    // ── ParseFactoryDescription: the plain-literal form ──────────────────────────

    /// <summary>Many devices store a plain literal with no '@inf,%key%;' prefix at all.</summary>
    [Theory]
    [InlineData("Realtek USB GbE Family Controller")]
    [InlineData("Intel(R) Wi-Fi 6 AX201 160MHz")]
    [InlineData("Hyper-V Virtual Ethernet Adapter")]
    public void ParseFactory_PlainLiteral_ReturnedVerbatim(string raw)
        => Assert.Equal(raw, AdapterNameRules.ParseFactoryDescription(raw));

    /// <summary>
    /// Without a leading '@' there is no indirect form to strip, so the whole value IS the literal —
    /// semicolons included. This must NOT be split.
    /// </summary>
    [Fact]
    public void ParseFactory_PlainLiteral_WithSemicolon_NotSplit()
        => Assert.Equal("Acme NIC; Model 5", AdapterNameRules.ParseFactoryDescription("Acme NIC; Model 5"));

    [Fact]
    public void ParseFactory_PlainLiteral_Trimmed()
        => Assert.Equal("Acme NIC", AdapterNameRules.ParseFactoryDescription("   Acme NIC   "));

    // ── ParseFactoryDescription: unusable shapes all yield null, never a blank ───

    /// <summary>An indirect form with no ';' carries no literal to fall back on — unusable.</summary>
    [Fact]
    public void ParseFactory_InfIndirect_NoSeparator_ReturnsNull()
        => Assert.Null(AdapterNameRules.ParseFactoryDescription(@"@oem241.inf,%rtl8153.devicedesc%"));

    /// <summary>A trailing ';' with nothing after it would otherwise yield an empty "original".</summary>
    [Theory]
    [InlineData(@"@oem241.inf,%rtl8153.devicedesc%;")]
    [InlineData(@"@oem241.inf,%rtl8153.devicedesc%;   ")]
    public void ParseFactory_InfIndirect_EmptyLiteral_ReturnsNull(string raw)
        => Assert.Null(AdapterNameRules.ParseFactoryDescription(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void ParseFactory_NullOrBlank_ReturnsNull(string? raw)
        => Assert.Null(AdapterNameRules.ParseFactoryDescription(raw));

    /// <summary>
    /// Refuse a value carrying control characters rather than hand it back — the caller would otherwise
    /// write it to the device on a later Reset.
    /// </summary>
    [Theory]
    [InlineData("Acme\0NIC")]                     // embedded NUL
    [InlineData("Acme\u0007NIC")]                // BEL
    [InlineData("Acme\nNIC")]                     // newline
    [InlineData("@acme.inf,%k%;Acme\0NIC")]       // ...also past the INF-indirect prefix
    [InlineData("@acme.inf,%k%;Acme\u0007NIC")]
    public void ParseFactory_ControlCharacters_ReturnsNull(string raw)
        => Assert.Null(AdapterNameRules.ParseFactoryDescription(raw));

    /// <summary>Whatever the input, the parse never yields an empty or whitespace-only name.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"@a.inf,%k%;")]
    [InlineData(@"@a.inf,%k%")]
    [InlineData("Realtek USB GbE Family Controller")]
    [InlineData(@"@oem241.inf,%k%;Realtek USB GbE Family Controller")]
    public void ParseFactory_NeverReturnsBlank(string? raw)
    {
        var parsed = AdapterNameRules.ParseFactoryDescription(raw);
        if (parsed is not null) Assert.False(string.IsNullOrWhiteSpace(parsed));
    }

    // ── CaptureOriginal: DeviceDesc is preferred over FriendlyName (issue #33) ───

    /// <summary>
    /// THE BUG. The config record was lost (uninstall/reinstall) while the registry rename survived, so
    /// FriendlyName already holds a PREVIOUS rename's output. The factory description must win — before
    /// #33 this recorded "Dell docking (Petterhaugen)" as "the original" and Reset became a no-op.
    /// </summary>
    [Fact]
    public void CaptureOriginal_PrefersFactoryOverAPriorRenamesOutput()
    {
        var c = AdapterNameRules.CaptureOriginal(
            factoryDescription: "Realtek USB GbE Family Controller",
            friendlyNamePresent: true,
            friendlyName: "Dell docking (Petterhaugen)");

        Assert.Equal("Realtek USB GbE Family Controller", c.OriginalFriendlyName);
        Assert.False(c.OriginalWasAbsent);
    }

    /// <summary>
    /// A device with no explicit FriendlyName displays its DeviceDesc literal, so writing that literal
    /// back on Reset reproduces the original display exactly. Reset becomes AVAILABLE where it was not
    /// before — and it is still a write, never a delete (§5.4).
    /// </summary>
    [Fact]
    public void CaptureOriginal_FactoryDerivable_NoFriendlyName_IsStillRestorable()
    {
        var c = AdapterNameRules.CaptureOriginal("Realtek USB GbE Family Controller", false, null);

        Assert.Equal("Realtek USB GbE Family Controller", c.OriginalFriendlyName);
        Assert.False(c.OriginalWasAbsent);
    }

    // ── CaptureOriginal: the fallback is exactly the pre-#33 behaviour ───────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CaptureOriginal_NoFactory_FallsBackToFriendlyName(string? factory)
    {
        var c = AdapterNameRules.CaptureOriginal(factory, true, "Realtek USB GbE Family Controller");

        Assert.Equal("Realtek USB GbE Family Controller", c.OriginalFriendlyName);
        Assert.False(c.OriginalWasAbsent);
    }

    /// <summary>Nothing to restore at all — Reset must not be offered, and must never delete.</summary>
    [Fact]
    public void CaptureOriginal_NoFactory_NoFriendlyName_MarksOriginalAbsent()
    {
        var c = AdapterNameRules.CaptureOriginal(null, false, null);

        Assert.Equal(string.Empty, c.OriginalFriendlyName);
        Assert.True(c.OriginalWasAbsent);
    }

    /// <summary>A present-but-null FriendlyName must record a blank, not throw.</summary>
    [Fact]
    public void CaptureOriginal_NoFactory_PresentButNullFriendlyName_RecordsBlank()
    {
        var c = AdapterNameRules.CaptureOriginal(null, true, null);

        Assert.Equal(string.Empty, c.OriginalFriendlyName);
        Assert.False(c.OriginalWasAbsent);
    }

    [Fact]
    public void CaptureOriginal_TrimsFactoryDescription()
        => Assert.Equal("Acme NIC", AdapterNameRules.CaptureOriginal("  Acme NIC  ", true, "Renamed").OriginalFriendlyName);

    /// <summary>End to end from the raw registry value: the parse feeds the capture.</summary>
    [Fact]
    public void CaptureOriginal_FromRawDeviceDesc_RecoversFactoryName()
    {
        var factory = AdapterNameRules.ParseFactoryDescription(
            @"@oem241.inf,%rtl8153.devicedesc%;Realtek USB GbE Family Controller");
        var c = AdapterNameRules.CaptureOriginal(factory, true, "Dell docking (Petterhaugen)");

        Assert.Equal("Realtek USB GbE Family Controller", c.OriginalFriendlyName);
    }

    // ── RepairOriginal: correcting records poisoned before the #33 fix ───────────

    private static AdapterNameRules.OriginalCapture Stored(string original, bool absent = false)
        => new(original, absent);

    /// <summary>
    /// The poisoned record from the issue: originalFriendlyName == currentFriendlyName == a prior
    /// rename's output, while the true original is the factory description.
    /// </summary>
    [Fact]
    public void RepairOriginal_PoisonedRecord_IsCorrectedToFactory()
    {
        var r = AdapterNameRules.RepairOriginal(
            Stored("Dell docking (Petterhaugen)"), "Realtek USB GbE Family Controller");

        Assert.NotNull(r);
        Assert.Equal("Realtek USB GbE Family Controller", r!.OriginalFriendlyName);
        Assert.False(r.OriginalWasAbsent);
    }

    /// <summary>Already correct → leave the record completely untouched (no needless config write).</summary>
    [Fact]
    public void RepairOriginal_AlreadyFactory_LeavesRecordUntouched()
        => Assert.Null(AdapterNameRules.RepairOriginal(
            Stored("Realtek USB GbE Family Controller"), "Realtek USB GbE Family Controller"));

    /// <summary>
    /// The conservative half of the rule, and the part that matters: with no ground truth to re-derive
    /// from, the stored original is NEVER guessed at or blanked.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RepairOriginal_NoDerivableFactory_LeavesRecordUntouched(string? factory)
    {
        Assert.Null(AdapterNameRules.RepairOriginal(Stored("Dell docking (Petterhaugen)"), factory));
        Assert.Null(AdapterNameRules.RepairOriginal(Stored(string.Empty, absent: true), factory));
    }

    /// <summary>
    /// A record marked "original absent" is repairable once a factory description IS derivable: the
    /// device does have a restorable original after all, so Reset should be offered.
    /// </summary>
    [Fact]
    public void RepairOriginal_OriginalWasAbsent_BecomesRestorableWhenFactoryDerivable()
    {
        var r = AdapterNameRules.RepairOriginal(
            Stored(string.Empty, absent: true), "Realtek USB GbE Family Controller");

        Assert.NotNull(r);
        Assert.Equal("Realtek USB GbE Family Controller", r!.OriginalFriendlyName);
        Assert.False(r.OriginalWasAbsent);
    }

    /// <summary>
    /// The broad rule catches a record that was poisoned and then renamed AGAIN — where
    /// original != current, so the narrower "original == current" signature would miss it.
    /// </summary>
    [Fact]
    public void RepairOriginal_PoisonedThenRenamedAgain_IsStillCorrected()
    {
        var r = AdapterNameRules.RepairOriginal(
            Stored("Dell docking (Petterhaugen)"), "Realtek USB GbE Family Controller");

        Assert.NotNull(r);
        Assert.Equal("Realtek USB GbE Family Controller", r!.OriginalFriendlyName);
    }

    /// <summary>Comparison is ordinal: a case/whitespace drift from the factory string is corrected.</summary>
    [Theory]
    [InlineData("realtek usb gbe family controller")]
    [InlineData("Realtek USB GbE Family Controller ")]
    public void RepairOriginal_DriftFromFactory_IsCorrected(string stored)
    {
        var r = AdapterNameRules.RepairOriginal(Stored(stored), "Realtek USB GbE Family Controller");

        Assert.NotNull(r);
        Assert.Equal("Realtek USB GbE Family Controller", r!.OriginalFriendlyName);
    }

    [Fact]
    public void RepairOriginal_TrimsFactoryBeforeComparing()
        => Assert.Null(AdapterNameRules.RepairOriginal(
            Stored("Realtek USB GbE Family Controller"), "  Realtek USB GbE Family Controller  "));

    /// <summary>
    /// The self-healing invariant: whatever RepairOriginal yields for a derivable factory description
    /// is exactly what CaptureOriginal would now write — repairing then re-capturing is stable.
    /// </summary>
    [Fact]
    public void RepairOriginal_IsIdempotent()
    {
        const string factory = "Realtek USB GbE Family Controller";

        var first = AdapterNameRules.RepairOriginal(Stored("Dell docking (Petterhaugen)"), factory);
        Assert.NotNull(first);

        // A second pass over the already-repaired record finds nothing left to do.
        Assert.Null(AdapterNameRules.RepairOriginal(first!, factory));
        Assert.Equal(AdapterNameRules.CaptureOriginal(factory, true, "Dell docking (Petterhaugen)"), first);
    }
}
