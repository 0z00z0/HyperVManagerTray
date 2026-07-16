using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// <c>MatchResult.WithRefreshedDisplayFrom</c> — how an adapter rename gets its new name onto the
/// dashboard without asking for the apply pass that issue #49 correctly took away.
///
/// <para>A rename writes only <c>adapterNames</c>, which <c>ConfigManager.NonNetworkProperties</c> excludes
/// from <c>AffectsNetwork</c>, so no re-evaluation runs — and the re-evaluation was also the only thing
/// re-deriving <c>HostAdapterName</c> from the host and republishing it. These cover the two halves of
/// putting that back: refresh the strings, and never touch anything else.</para>
/// </summary>
public class MatchResultDisplayRefreshTests
{
    private static MatchResult Applied(string adapterName = "Realtek USB GbE Family Controller") =>
        new("Office LAN", "Bridged", ["vDev"])
        {
            ApplyStatus              = NetworkStatusUi.SwitchApplyStatus.Applied,
            HostAdapterName          = adapterName,
            HostAdapterInterfaceName = "Ethernet 5",
            HostIp                   = "10.0.0.45",
            Gateway                  = "10.0.0.1",
            DnsServers               = ["10.0.0.1"],
        };

    /// <summary>The whole point: the adapter was renamed, a fresh evaluation re-read the FriendlyName, and
    /// the confirmed outcome adopts the new string.</summary>
    [Fact]
    public void ARenamedAdapter_PublishesItsNewDisplayName()
    {
        var confirmed = Applied("Realtek USB GbE Family Controller");
        var fresh     = Applied("Office dock");

        var refreshed = confirmed.WithRefreshedDisplayFrom(fresh);

        Assert.NotNull(refreshed);
        Assert.Equal("Office dock", refreshed.HostAdapterName);
    }

    /// <summary>
    /// A rename must refresh the DISPLAY and nothing else. The outcome fields are what every status
    /// surface renders (issue #37) and must survive verbatim — a rename is not evidence about whether the
    /// bind or the VM reconnects succeeded, and the identity fields are equal by the guard anyway.
    /// </summary>
    [Fact]
    public void RefreshingTheDisplay_CarriesTheConfirmedOutcomeThrough()
    {
        var confirmed = Applied() with { UserInitiated = true };
        var fresh     = Applied("Office dock") with { HostIp = "10.0.0.99", Gateway = "10.0.0.254" };

        var refreshed = confirmed.WithRefreshedDisplayFrom(fresh)!;

        // Display: re-read from the host.
        Assert.Equal("Office dock", refreshed.HostAdapterName);
        Assert.Equal("10.0.0.99",   refreshed.HostIp);
        Assert.Equal("10.0.0.254",  refreshed.Gateway);

        // Outcome and identity: the confirmed result's own, untouched.
        Assert.Equal(NetworkStatusUi.SwitchApplyStatus.Applied, refreshed.ApplyStatus);
        Assert.Equal("Office LAN", refreshed.RuleName);
        Assert.Equal("Bridged",    refreshed.VirtualSwitch);
        Assert.Equal("Ethernet 5", refreshed.HostAdapterInterfaceName);
        Assert.Equal(["vDev"],     refreshed.TargetVms);
        Assert.True(refreshed.UserInitiated);
        Assert.Empty(refreshed.FailedVms);
    }

    /// <summary>
    /// The host says a DIFFERENT rule now matches — a real network change. Publishing the fresh result
    /// here would assert an intent nobody applied (issue #37's exact lie), and acting on it would be the
    /// fan-out #49 removed. A rename must never be the reason a real change is reported early: say nothing
    /// and let the debounced apply pass own it.
    /// </summary>
    [Fact]
    public void ADifferentRule_RefreshesNothing()
    {
        var confirmed = Applied();
        var fresh     = Applied("Office dock") with { RuleName = "Home LAN" };

        Assert.Null(confirmed.WithRefreshedDisplayFrom(fresh));
    }

    /// <summary>The switch moved — same reasoning: not a display change, and not this path's business.</summary>
    [Fact]
    public void ADifferentSwitch_RefreshesNothing()
    {
        Assert.Null(Applied().WithRefreshedDisplayFrom(Applied() with { VirtualSwitch = "Default Switch" }));
    }

    /// <summary>
    /// The rule now resolves to a different HOST ADAPTER. This is the one that would hurt most quietly:
    /// the display fields would be adopted from an adapter the switch is not bound to, so the dashboard
    /// would name the new dock while the switch still points at the old one — a confident, wrong answer,
    /// which is the whole class of bug #32 and #37 exist to remove.
    /// </summary>
    [Fact]
    public void ADifferentHostAdapter_RefreshesNothing()
    {
        var confirmed = Applied();
        var fresh     = Applied("Home dock") with { HostAdapterInterfaceName = "Ethernet 7" };

        Assert.Null(confirmed.WithRefreshedDisplayFrom(fresh));
    }

    /// <summary>
    /// The last apply FAILED. A rename fixes nothing about a failed bind, so quietly refreshing the
    /// strings on it would freshen the description of a state the app never achieved — and, because this
    /// path republishes what it returns, would re-announce that failure on a rename. The retry belongs to
    /// the apply pass (see MatchResult.ConfirmsSameOutcomeFor, whose Applied clause this inherits).
    /// </summary>
    [Fact]
    public void AnUnconfirmedOutcome_IsNeverRefreshedInPlace()
    {
        foreach (var status in new[]
                 {
                     NetworkStatusUi.SwitchApplyStatus.NotEvaluated,
                     NetworkStatusUi.SwitchApplyStatus.BindFailed,
                     NetworkStatusUi.SwitchApplyStatus.VmConnectFailed,
                 })
        {
            var unconfirmed = Applied() with { ApplyStatus = status };

            Assert.Null(unconfirmed.WithRefreshedDisplayFrom(Applied("Office dock")));
        }
    }

    /// <summary>Nothing changed at all — the common case when a NetworkChange happens to fire first and
    /// the display is already right. Still a valid refresh, and still nothing but strings.</summary>
    [Fact]
    public void AnUnchangedHost_RefreshesToTheSameValues()
    {
        var confirmed = Applied();

        var refreshed = confirmed.WithRefreshedDisplayFrom(Applied());

        Assert.NotNull(refreshed);
        // Field-by-field, not Assert.Equal on the records: MatchResult's synthesised equality compares its
        // IReadOnlyList members by reference, so two separately-built results with identical contents are
        // never equal. Nothing to fix in the type — nothing compares MatchResults for equality in
        // production (ConfirmsSameOutcomeFor does the real comparison, element-wise and deliberately).
        Assert.Equal(confirmed.HostAdapterName, refreshed.HostAdapterName);
        Assert.Equal(confirmed.HostIp,          refreshed.HostIp);
        Assert.Equal(confirmed.Gateway,         refreshed.Gateway);
        Assert.Equal(confirmed.DnsServers,      refreshed.DnsServers);
        Assert.Equal(confirmed.ApplyStatus,     refreshed.ApplyStatus);
    }
}
