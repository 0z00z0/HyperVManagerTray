using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The verdict on whether a machine's own address belongs to the network its virtual switch leads to,
/// and the rule deciding when a machine may be asked for a new one.
///
/// <para>Both exist because the states they tell apart are otherwise reached only by docking a laptop
/// and waiting: a machine moved onto another switch keeps the address it was given until its own
/// address client asks for a different one. What reaches a person is a mark on a card, so the cases
/// that must NEVER produce one — a switch with no host adapter behind it, a host nobody could read, a
/// machine that has not been given an address yet — are the ones tested hardest here.</para>
/// </summary>
public class GuestAddressRulesTests
{
    private const string SwitchA = "9e93c2c2-1111-2222-3333-444455556666";
    private const string SwitchB = "c08cb7b8-aaaa-bbbb-cccc-ddddeeeeffff";
    private const string HostNet = "10.20.30.0/24";

    private static GuestAddressFit Fit(
        string? guestIp, string? vmSwitch = SwitchA, string? appliedSwitch = SwitchA,
        string? hostCidr = HostNet, SwitchUplinkState uplink = SwitchUplinkState.Connected) =>
        GuestAddressRules.For(guestIp, vmSwitch, appliedSwitch, hostCidr, uplink);

    // ── The two verdicts that say something ──────────────────────────────────────

    [Theory]
    [InlineData("10.20.30.1")]
    [InlineData("10.20.30.254")]
    public void AddressInsideTheSwitchNetwork_Fits(string ip) =>
        Assert.Equal(GuestAddressFit.Fits, Fit(ip));

    [Theory]
    [InlineData("172.24.225.24")]   // the shape of the failure this exists for: a NAT address on a bridged switch
    [InlineData("10.20.31.1")]      // one subnet out
    [InlineData("192.168.1.10")]
    public void AddressOutsideTheSwitchNetwork_IsForeign(string ip) =>
        Assert.Equal(GuestAddressFit.Foreign, Fit(ip));

    /// <summary>WMI and the network stack spell GUIDs differently; a machine must still be judged.</summary>
    [Fact]
    public void SwitchIdsAreComparedWithoutCaseOrBraces() =>
        Assert.Equal(GuestAddressFit.Foreign,
            Fit("192.168.1.10", vmSwitch: "{" + SwitchA.ToUpperInvariant() + "}", appliedSwitch: SwitchA));

    // ── Everything that must claim nothing ───────────────────────────────────────

    /// <summary>An internal or private switch has no host adapter behind it, so there is no network to
    /// compare against. Marking one would brand every machine on it wrong by design.</summary>
    [Fact]
    public void NonExternalSwitch_IsNeverJudged() =>
        Assert.Equal(GuestAddressFit.Unknown, Fit("192.168.1.10", uplink: SwitchUplinkState.NotExternal));

    /// <summary>The host's adapters could not be listed. A verdict here would mark every machine on the
    /// host over one query that did not answer.</summary>
    [Fact]
    public void UnreadableHost_IsNeverJudged() =>
        Assert.Equal(GuestAddressFit.Unknown, Fit("192.168.1.10", uplink: SwitchUplinkState.Unknown));

    /// <summary>A switch whose adapter is unplugged already says so on every status surface; the address
    /// on it is not a second, different finding.</summary>
    [Fact]
    public void SwitchWithNoWayOut_IsNeverJudged() =>
        Assert.Equal(GuestAddressFit.Unknown, Fit("192.168.1.10", uplink: SwitchUplinkState.NoUplink));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MachineWithNoAddress_IsNeverJudged(string? ip) =>
        Assert.Equal(GuestAddressFit.Unknown, Fit(ip));

    /// <summary>An address that is not an IPv4 one — the guest reported something else, or nothing
    /// parsable. Early or odd, but not wrong.</summary>
    [Theory]
    [InlineData("fe80::1")]
    [InlineData("not-an-address")]
    public void AddressThatIsNotIpv4_IsNeverJudged(string ip) =>
        Assert.Equal(GuestAddressFit.Unknown, Fit(ip));

    /// <summary>The machine sits on some other switch than the one the rules applied, so the app holds no
    /// reading of what that switch leads to.</summary>
    [Fact]
    public void MachineOnAnotherSwitch_IsNeverJudged() =>
        Assert.Equal(GuestAddressFit.Unknown, Fit("192.168.1.10", vmSwitch: SwitchB, appliedSwitch: SwitchA));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoHostNetworkResolved_IsNeverJudged(string? cidr) =>
        Assert.Equal(GuestAddressFit.Unknown, Fit("192.168.1.10", hostCidr: cidr));

    [Theory]
    [InlineData(null, SwitchA)]
    [InlineData("", SwitchA)]
    [InlineData(SwitchA, null)]
    [InlineData(SwitchA, "")]
    public void SwitchNotIdentified_IsNeverJudged(string? vmSwitch, string? appliedSwitch) =>
        Assert.Equal(GuestAddressFit.Unknown, Fit("192.168.1.10", vmSwitch, appliedSwitch));

    // ── When a machine may be asked for a new address ────────────────────────────

    /// <summary>The whole point of splitting the move outcome three ways: a machine that was already on
    /// the switch has not changed network, and cycling its link would drop a working connection on every
    /// evaluation.</summary>
    [Theory]
    [InlineData(SwitchMoveOutcome.Moved,        true)]
    [InlineData(SwitchMoveOutcome.AlreadyThere, false)]
    [InlineData(SwitchMoveOutcome.Failed,       false)]
    public void OnlyARealMoveMayAskForANewAddress(SwitchMoveOutcome outcome, bool expected) =>
        Assert.Equal(expected, GuestAddressRules.ShouldRenewAfter(outcome));

    // ── What the card shows ──────────────────────────────────────────────────────

    [Fact]
    public void FittingOrUnknownAddress_IsShownWithNoMark()
    {
        Assert.Equal("10.20.30.5", GuestAddressUi.AddressText("10.20.30.5", GuestAddressFit.Fits, renewing: false));
        Assert.Equal("10.20.30.5", GuestAddressUi.AddressText("10.20.30.5", GuestAddressFit.Unknown, renewing: false));
        Assert.Equal("", GuestAddressUi.TooltipFor(GuestAddressFit.Fits, renewing: false));
    }

    [Fact]
    public void ForeignAddress_IsMarkedAndExplained()
    {
        Assert.Equal("≠ 172.24.225.24",
            GuestAddressUi.AddressText("172.24.225.24", GuestAddressFit.Foreign, renewing: false));
        Assert.NotEqual("", GuestAddressUi.TooltipFor(GuestAddressFit.Foreign, renewing: false));
    }

    /// <summary>A renewal in flight is the newer fact and explains the mark it replaces, so it wins even
    /// while the address is still the one that did not belong.</summary>
    [Fact]
    public void RenewalInFlight_ReplacesTheMismatchMark()
    {
        Assert.Equal("… 172.24.225.24",
            GuestAddressUi.AddressText("172.24.225.24", GuestAddressFit.Foreign, renewing: true));
        Assert.Contains("updated", GuestAddressUi.TooltipFor(GuestAddressFit.Foreign, renewing: true));
    }

    /// <summary>A machine with no address at all still shows that it is being asked for one.</summary>
    [Fact]
    public void RenewalInFlightWithNoAddress_StillShowsTheMark() =>
        Assert.Equal("…", GuestAddressUi.AddressText(null, GuestAddressFit.Unknown, renewing: true));

    /// <summary>The two marks must be distinguishable at a glance, and both are characters the brand mono
    /// face covers — checked against the shipped CascadiaMono.ttf, which lacks the obvious alternatives
    /// (a warning triangle, a circular-arrow refresh) and would silently fall back to a wider face,
    /// under-measuring the row.</summary>
    [Fact]
    public void TheTwoMarksAreSingleDistinctCharacters()
    {
        Assert.NotEqual(GuestAddressUi.ForeignMark, GuestAddressUi.RenewingMark);
        Assert.Equal(1, GuestAddressUi.ForeignMark.Length);
        Assert.Equal(1, GuestAddressUi.RenewingMark.Length);
    }
}
