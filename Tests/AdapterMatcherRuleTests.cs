using System.Net;
using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Tests for AdapterMatcher rule-evaluation helpers.
/// The high-level Evaluate() method reads live NetworkInterface objects and cannot be
/// exercised here without a real host adapter; these tests verify the pure building-block
/// helpers that underpin every rule condition check.
/// </summary>
public class AdapterMatcherRuleTests
{
    // ── MAC normalization used by every MAC-condition check ───────────────────

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "aabbccddeeff", true)]   // colon lower  == dash lower stripped
    [InlineData("AA-BB-CC-DD-EE-FF", "AABBCCDDEEFF", true)]   // dash upper   == raw upper
    [InlineData("AA:BB:CC:DD:EE:FF", "aabbccddeeff", true)]   // colon upper  == lower raw
    [InlineData("AABBCCDDEEFF",      "001122334455", false)]   // different MACs
    public void NormalizeMac_MacConditionEquality(string a, string b, bool shouldEqual)
    {
        var na = AdapterMatcher.NormalizeMac(a);
        var nb = AdapterMatcher.NormalizeMac(b);
        Assert.Equal(shouldEqual, na == nb);
    }

    // ── CIDR matching: boundary conditions used by IP-condition checks ────────

    [Theory]
    [InlineData("192.168.1.0",   "192.168.1.0/24", true)]    // network address itself
    [InlineData("192.168.1.255", "192.168.1.0/24", true)]    // broadcast address
    [InlineData("192.168.2.0",   "192.168.1.0/24", false)]   // one network above
    [InlineData("172.16.0.1",    "172.16.0.0/12",  true)]    // /12 — RFC-1918 range
    [InlineData("172.31.255.254","172.16.0.0/12",  true)]    // last address in /12
    [InlineData("172.32.0.0",    "172.16.0.0/12",  false)]   // just outside /12
    [InlineData("10.255.255.255","10.0.0.0/8",     true)]    // /8 broadcast
    [InlineData("11.0.0.0",      "10.0.0.0/8",     false)]   // outside /8
    [InlineData("1.2.3.4",       "1.2.3.4/32",     true)]    // /32 exact host
    [InlineData("1.2.3.5",       "1.2.3.4/32",     false)]   // /32 miss
    public void IsInCidr_BoundaryConditions(string ip, string cidr, bool expected)
        => Assert.Equal(expected, AdapterMatcher.IsInCidr(IPAddress.Parse(ip), cidr));

    // ── FormatMac edge cases ──────────────────────────────────────────────────

    [Theory]
    [InlineData("aabbccddeeff", "AA:BB:CC:DD:EE:FF")]  // lower-case input
    [InlineData("000000000000", "00:00:00:00:00:00")]  // all-zero MAC
    [InlineData("ffffffffffff", "FF:FF:FF:FF:FF:FF")]  // broadcast MAC
    public void FormatMac_AdditionalCases(string raw, string expected)
        => Assert.Equal(expected, AdapterMatcher.FormatMac(raw));

    // ── NormalizeMac: idempotent ──────────────────────────────────────────────

    [Fact]
    public void NormalizeMac_IsIdempotent()
    {
        var once  = AdapterMatcher.NormalizeMac("aa:bb:cc:dd:ee:ff");
        var twice = AdapterMatcher.NormalizeMac(once);
        Assert.Equal(once, twice);
    }

    // ── StripFilterSuffix: WFP/NDIS filter-chain suffix stripping ─────────────

    [Theory]
    [InlineData("Lenovo USB Ethernet-WFP Native MAC Layer LightWeight Filter-0000", "Lenovo USB Ethernet")]
    [InlineData("Realtek Gaming GbE Family Controller - WFP Lightweight Filter",    "Realtek Gaming GbE Family Controller")]
    [InlineData("Intel(R) Wi-Fi 6 AX201-NDIS Native MAC Layer LightWeight Filter",  "Intel(R) Wi-Fi 6 AX201")]
    [InlineData("Adapter Name - NDIS Native MAC Layer LightWeight Filter",          "Adapter Name")]
    public void StripFilterSuffix_StripsKnownMarkers(string description, string expected)
        => Assert.Equal(expected, AdapterMatcher.StripFilterSuffix(description));

    [Theory]
    [InlineData("Realtek USB GbE Family Controller #2")]  // no marker at all
    [InlineData("Plain Ethernet Adapter")]
    public void StripFilterSuffix_NoMarker_ReturnsUnchanged(string description)
        => Assert.Equal(description, AdapterMatcher.StripFilterSuffix(description));

    [Fact]
    public void StripFilterSuffix_MarkerAtStart_ReturnsUnchanged()
    {
        // idx == 0 (marker at the very start) is not stripped — there'd be nothing left as a
        // "base name", so the original string is returned rather than an empty string.
        var input = "-WFP Native MAC Layer LightWeight Filter-0000";
        Assert.Equal(input, AdapterMatcher.StripFilterSuffix(input));
    }

    [Fact]
    public void StripFilterSuffix_TrimsWhitespaceBeforeMarker()
        => Assert.Equal("Adapter", AdapterMatcher.StripFilterSuffix("Adapter   -WFP Native MAC Layer LightWeight Filter-0000"));

    [Fact]
    public void StripFilterSuffix_CaseInsensitiveMarkerMatch()
        => Assert.Equal("Adapter", AdapterMatcher.StripFilterSuffix("Adapter-wfp Native MAC Layer lightweight filter-0000"));

    // ── CalculateCidrFromBytes: network/prefix computation from raw address bytes ─────

    private static byte[] Ip(string s) => IPAddress.Parse(s).GetAddressBytes();

    [Theory]
    [InlineData("192.168.1.45",  "255.255.255.0", "192.168.1.0/24")]
    [InlineData("10.0.1.200",    "255.255.254.0", "10.0.0.0/23")]
    [InlineData("172.16.5.9",    "255.240.0.0",   "172.16.0.0/12")]
    [InlineData("8.8.8.8",       "255.255.255.255", "8.8.8.8/32")]
    [InlineData("10.1.2.3",      "0.0.0.0",       "0.0.0.0/0")]
    public void CalculateCidrFromBytes_ComputesNetworkAndPrefix(string ip, string mask, string expected)
        => Assert.Equal(expected, AdapterMatcher.CalculateCidrFromBytes(Ip(ip), Ip(mask)));

    [Fact]
    public void CalculateCidrFromBytes_NonContiguousMask_StillCountsSetBits()
    {
        // 255.0.255.0 is a malformed (non-contiguous) mask, but production code never validates
        // mask well-formedness — it just counts set bits and ANDs the address, so this documents
        // the actual (permissive) behaviour rather than throwing.
        var result = AdapterMatcher.CalculateCidrFromBytes(Ip("10.20.30.40"), Ip("255.0.255.0"));
        Assert.Equal("10.0.30.0/16", result);
    }
}
