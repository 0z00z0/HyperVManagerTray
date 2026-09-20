using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The decision that says a virtual switch has no way out to the rest of the network.
///
/// <para><b>Why this one is worth a test when so little else here is.</b> Every surface that tells the
/// user their VM has no network — the tray icon's colour, the hover, the balloon, the host card and the
/// VM card — renders THIS verdict and nothing else. A wrong answer here is therefore not a wrong
/// pixel: it is either a silent bridge with nothing behind it (the state the release exists to end) or
/// a false alarm over a healthy machine. And it cannot be checked any other way, because the states it
/// separates are reached by physically unplugging a dock from a running host.</para>
/// </summary>
public class SwitchUplinkRulesTests
{
    private const string Dock = "AABBCCDDEEFF";
    private const string WiFi = "112233445566";

    private static IReadOnlySet<string> Connected(params string[] macs) =>
        new HashSet<string>(macs, StringComparer.OrdinalIgnoreCase);

    // ── The three answers the release turns on ──────────────────────────────────

    /// <summary>
    /// A healthy bridge: the switch's uplink port sits on an adapter that reports a connected medium.
    /// The only verdict any surface may render as a working connection.
    /// </summary>
    [Fact]
    public void For_AdapterConnected_IsConnected() =>
        Assert.Equal(SwitchUplinkState.Connected,
            SwitchUplinkRules.For(hasExternalPort: true, portMac: Dock, connectedMacs: Connected(Dock, WiFi)));

    /// <summary>
    /// The dock is unplugged: the port still names its adapter, but no adapter with that hardware
    /// address reports a connected medium. This is the reported case — the guest reads "Media
    /// disconnected" while every surface in the app used to show a confirmed green bridge.
    /// </summary>
    [Fact]
    public void For_BoundAdapterDisconnected_IsNoUplink() =>
        Assert.Equal(SwitchUplinkState.NoUplink,
            SwitchUplinkRules.For(hasExternalPort: true, portMac: Dock, connectedMacs: Connected(WiFi)));

    /// <summary>
    /// The switch is external but its port resolves to no adapter at all — bound to nothing. A missing
    /// uplink, NOT an unknown one: the app looked at the port and found nothing behind it, which is a
    /// fact about the host rather than a failure to read it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void For_ExternalPortWithNoAdapter_IsNoUplink(string? portMac) =>
        Assert.Equal(SwitchUplinkState.NoUplink,
            SwitchUplinkRules.For(hasExternalPort: true, portMac: portMac, connectedMacs: Connected(WiFi)));

    // ── The two states that must never be mistaken for a fault ──────────────────

    /// <summary>
    /// An internal or private switch has no external port and no uplink to lose. Marking it short of one
    /// would put a warning over a switch working exactly as designed — including the NAT fallback this
    /// app moves VMs onto precisely WHEN the bridge is gone, which would then report the rescue as the
    /// emergency.
    /// </summary>
    [Fact]
    public void For_SwitchWithNoExternalPort_IsNotExternal() =>
        Assert.Equal(SwitchUplinkState.NotExternal,
            SwitchUplinkRules.For(hasExternalPort: false, portMac: "", connectedMacs: Connected(Dock)));

    /// <summary>
    /// The adapter list could not be read. Null dominates every other input — including a port that
    /// names no adapter — because with no list there is nothing to compare against, and answering
    /// "no uplink" from a query that did not run would brand every switch on the host broken on the
    /// strength of one failed WMI call.
    /// </summary>
    [Theory]
    [InlineData(true,  Dock)]
    [InlineData(true,  "")]
    [InlineData(false, "")]
    public void For_AdapterListUnreadable_IsUnknown(bool hasExternalPort, string portMac) =>
        Assert.Equal(SwitchUplinkState.Unknown,
            SwitchUplinkRules.For(hasExternalPort, portMac, connectedMacs: null));

    // ── Only one verdict is a problem ───────────────────────────────────────────

    /// <summary>
    /// Enumerated rather than listed: a verdict added later has to be given a side here deliberately
    /// instead of inheriting one. The whole point of the enum is that "we could not look" and "there is
    /// nothing to look at" are not faults, and an inline <c>!= Connected</c> at any call site would make
    /// both into one.
    /// </summary>
    [Theory]
    [InlineData(SwitchUplinkState.NoUplink,    true)]
    [InlineData(SwitchUplinkState.Connected,   false)]
    [InlineData(SwitchUplinkState.NotExternal, false)]
    [InlineData(SwitchUplinkState.Unknown,     false)]
    public void HasNoConnectionOut_OnlyTheEstablishedFaultCounts(SwitchUplinkState state, bool expected) =>
        Assert.Equal(expected, SwitchUplinkRules.HasNoConnectionOut(state));

    [Fact]
    public void HasNoConnectionOut_CoversEveryVerdict()
    {
        foreach (var state in Enum.GetValues<SwitchUplinkState>())
            Assert.Equal(state == SwitchUplinkState.NoUplink, SwitchUplinkRules.HasNoConnectionOut(state));
    }

    // ── What the surfaces are allowed to say about it ───────────────────────────

    /// <summary>
    /// The amber tray state is reachable ONLY from a confirmed apply onto a non-fallback switch with a
    /// confirmed-down uplink. Enumerating the apply statuses keeps a future one from slipping into it:
    /// a state that says "the bridge is up but empty" must never be shown over a pass that did not land,
    /// which has its own, louder report.
    /// </summary>
    [Fact]
    public void BridgeHasNoUplink_NeedsAConfirmedApplyOnANonFallbackSwitch()
    {
        foreach (var status in Enum.GetValues<NetworkStatusUi.SwitchApplyStatus>())
            foreach (var bridgedTarget in new[] { true, false })
                foreach (var uplink in Enum.GetValues<SwitchUplinkState>())
                {
                    bool expected = status == NetworkStatusUi.SwitchApplyStatus.Applied
                                 && bridgedTarget
                                 && uplink == SwitchUplinkState.NoUplink;

                    Assert.Equal(expected, NetworkStatusUi.BridgeHasNoUplink(status, bridgedTarget, uplink));
                }
    }

    /// <summary>
    /// The icon for the state, and the two claims it must not make: not the green that says traffic is
    /// flowing, and not the grey that says the app has not looked.
    /// </summary>
    [Fact]
    public void IconFor_BridgeWithNoUplink_IsItsOwnStateAndNeverASuccessColour()
    {
        var icon = NetworkStatusUi.IconFor(NetworkStatusUi.SwitchApplyStatus.Applied,
                                           bridgedTarget: true, SwitchUplinkState.NoUplink);

        Assert.Equal(TrayIconState.BridgeNoUplink, icon);
        Assert.NotEqual(TrayIconState.Bridged,  icon);
        Assert.NotEqual(TrayIconState.Fallback, icon);
        Assert.NotEqual(TrayIconState.Unknown,  icon);
    }

    /// <summary>
    /// An uplink nobody could read leaves every surface exactly as it was. This is the clause that keeps
    /// a failed WMI query from turning the whole tray amber, and it is the one an optimisation is most
    /// likely to drop.
    /// </summary>
    [Theory]
    [InlineData(SwitchUplinkState.Unknown)]
    [InlineData(SwitchUplinkState.NotExternal)]
    [InlineData(SwitchUplinkState.Connected)]
    public void IconFor_WithoutAnEstablishedFault_IsUnchanged(SwitchUplinkState uplink) =>
        Assert.Equal(TrayIconState.Bridged,
            NetworkStatusUi.IconFor(NetworkStatusUi.SwitchApplyStatus.Applied, bridgedTarget: true, uplink));

    /// <summary>
    /// The NAT fallback is not a bridge and has no uplink to miss, so a down verdict on it — which
    /// cannot happen through the app's own call sites, since they answer NotExternal for it — still must
    /// not repaint the confirmed blue.
    /// </summary>
    [Fact]
    public void IconFor_FallbackNeverShowsTheBridgeWarning() =>
        Assert.Equal(TrayIconState.Fallback,
            NetworkStatusUi.IconFor(NetworkStatusUi.SwitchApplyStatus.Applied,
                                    bridgedTarget: false, SwitchUplinkState.NoUplink));

    /// <summary>
    /// One phrase, three surfaces. The VM card's sub-row, the host card's rule row and the tray hover
    /// all append the same words, so a report of "no connection out" reads identically wherever it is
    /// seen — <c>docs/DISPLAY-VOCABULARY.md</c> corollary 4.
    /// </summary>
    [Fact]
    public void TheSamePhraseReachesTheHostRowAndTheTooltip()
    {
        var rule    = NetworkStatusUi.RuleRowText("Office LAN",
                          NetworkStatusUi.SwitchApplyStatus.Applied, SwitchUplinkState.NoUplink);
        var tooltip = NetworkStatusUi.TooltipSwitchSuffix(
                          NetworkStatusUi.SwitchApplyStatus.Applied, SwitchUplinkState.NoUplink);

        Assert.EndsWith(NetworkStatusUi.NoConnectionOutSuffix, rule, StringComparison.Ordinal);
        Assert.Equal(NetworkStatusUi.NoConnectionOutSuffix, tooltip);
        Assert.Contains(NetworkStatusUi.NoConnectionOut, rule, StringComparison.Ordinal);
    }

    /// <summary>
    /// A healthy bridge keeps the rule row bare and the hover suffix empty — the wording only appears
    /// when there is something to say, or it stops meaning anything.
    /// </summary>
    [Fact]
    public void AHealthyBridgeSaysNothingExtra()
    {
        Assert.Equal("Office LAN", NetworkStatusUi.RuleRowText("Office LAN",
            NetworkStatusUi.SwitchApplyStatus.Applied, SwitchUplinkState.Connected));
        Assert.Equal("", NetworkStatusUi.TooltipSwitchSuffix(
            NetworkStatusUi.SwitchApplyStatus.Applied, SwitchUplinkState.Connected));
    }

    /// <summary>
    /// The host card's adapter row when the evaluation found no adapter at all. The placeholder dash was
    /// not wrong so much as mute: it left the person reading four blank rows with no statement of why.
    /// </summary>
    [Theory]
    [InlineData("—")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void HostAdapterRowText_WithNoAdapter_SaysSo(string? name) =>
        Assert.Equal("No adapter connected", NetworkStatusUi.HostAdapterRowText(name));

    [Fact]
    public void HostAdapterRowText_WithAnAdapter_ShowsIt() =>
        Assert.Equal("Dock Ethernet", NetworkStatusUi.HostAdapterRowText("Dock Ethernet"));
}
