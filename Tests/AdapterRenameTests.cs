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
}
