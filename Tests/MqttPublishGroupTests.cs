using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.Discovery;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The publish groups this app declares, and where a fresh installation starts (issue #75).
///
/// <para>Three of the four are on: the published surface is the point of the feature, and a group is
/// switched off to reduce it. <b>VM metrics is the exception and must be off</b> — its readings only
/// flow while <c>VmService.SubscribeMetrics()</c> is held, which is a 2.5-second Hyper-V query loop in
/// an app that otherwise does no in-process WMI work while idle. Shipping it on opts every installation
/// into that loop without anyone asking for it.</para>
/// </summary>
public class MqttPublishGroupTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    /// <summary>A config.json exactly as the app writes one for itself when the file is missing — the
    /// blank slate, with no <c>mqtt</c> section at all.</summary>
    private ConfigManager FreshInstallation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvmt_mqtt_groups_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, DefaultConfig.Json);
        _tempFiles.Add(path);
        return new ConfigManager(path, NullLogger<ConfigManager>.Instance);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ── What the app declares ───────────────────────────────────────────────────

    [Fact]
    public void TheDeclaredGroupsAreTheFourTheSettingsPanelRenders()
        => Assert.Equal(["network", "vm", "diagnostics", "metrics"],
                        MqttEntityTable.Groups.Select(g => g.Key));

    [Theory]
    [InlineData(MqttEntityTable.NetworkGroup,     true)]
    [InlineData(MqttEntityTable.VmGroup,          true)]
    [InlineData(MqttEntityTable.DiagnosticsGroup, true)]
    [InlineData(MqttEntityTable.MetricsGroup,     false)]
    public void OnlyTheMetricsGroupIsDeclaredOffByDefault(string key, bool defaultOn)
        => Assert.Equal(defaultOn, MqttEntityTable.Groups.Single(g => g.Key == key).DefaultOn);

    /// <summary>A group shipped off has to say why, on the row itself: the description is where a
    /// default that is not the obvious one gets justified.</summary>
    [Fact]
    public void TheMetricsGroupSaysWhyItIsOff()
    {
        var metrics = MqttEntityTable.Groups.Single(g => g.Key == MqttEntityTable.MetricsGroup);

        Assert.False(string.IsNullOrWhiteSpace(metrics.Description));
        Assert.All(
            MqttEntityTable.Groups.Where(g => g.DefaultOn),
            g => Assert.Equal("", g.Description));
    }

    [Fact]
    public void EveryGroupCarriesALabelAndAnInfoLine()
        => Assert.All(MqttEntityTable.Groups, g =>
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Label));
            Assert.False(string.IsNullOrWhiteSpace(g.Info));
        });

    // ── Where a fresh installation starts ───────────────────────────────────────

    /// <summary>
    /// The end of the chain, through the real config file: the blank slate stores no group state, so
    /// every group falls through to its declared default — and metrics therefore reads as OFF without
    /// anyone having touched it.
    /// </summary>
    [Fact]
    public void AFreshConfigDoesNotOptIntoMetricsPublishing()
    {
        using var config = FreshInstallation();
        using var store = new MqttConfigStore(config, raise: work => work());
        var groups = new PublishGroupSet(store, MqttEntityTable.Groups);

        Assert.Empty(store.Read().Groups);   // nothing stored — the default is doing the deciding

        var snapshot = groups.Snapshot();
        Assert.True(snapshot.IsEnabled(MqttEntityTable.NetworkGroup));
        Assert.True(snapshot.IsEnabled(MqttEntityTable.VmGroup));
        Assert.True(snapshot.IsEnabled(MqttEntityTable.DiagnosticsGroup));
        Assert.False(snapshot.IsEnabled(MqttEntityTable.MetricsGroup));
    }

    /// <summary>…and the consequence that matters: nothing subscribes to the 2.5-second WMI loop, even
    /// with a live broker connection.</summary>
    [Fact]
    public void AFreshConfigHoldsNoMetricsSubscription()
    {
        using var config = FreshInstallation();
        using var store = new MqttConfigStore(config, raise: work => work());
        var groups = new PublishGroupSet(store, MqttEntityTable.Groups);

        int subscribed = 0;
        var hold = new MqttMetricsHold(() => subscribed++, () => { });

        hold.Update(groups.IsEnabled(MqttEntityTable.MetricsGroup), connected: true);

        Assert.False(hold.IsHeld);
        Assert.Equal(0, subscribed);
    }

    /// <summary>A group toggle is one of the two controls that commit on the spot, so it goes through
    /// the settings store and reaches config.json — and survives the reload that follows.</summary>
    [Fact]
    public void SwitchingMetricsOnIsStoredAndSurvivesAReload()
    {
        using var config = FreshInstallation();
        using var store = new MqttConfigStore(config, raise: work => work());
        var groups = new PublishGroupSet(store, MqttEntityTable.Groups);

        groups.Set(MqttEntityTable.MetricsGroup, true);

        Assert.True(groups.Snapshot().IsEnabled(MqttEntityTable.MetricsGroup));
        Assert.True(config.Current.Mqtt.Settings.Groups[MqttEntityTable.MetricsGroup]);

        Assert.True(config.Load().Succeeded);
        Assert.True(config.Current.Mqtt.Settings.Groups[MqttEntityTable.MetricsGroup]);
    }

    // ── What a toggle costs ─────────────────────────────────────────────────────

    /// <summary>
    /// <c>DiscoveryPublisher</c>'s constructor subscribes to the group set itself, so a toggle already
    /// drives one announcement pass. This is the premise <c>MqttService</c> relies on: its own handler
    /// reconciles the metrics hold and must NOT republish as well, or every checkbox announces the
    /// whole document twice.
    /// </summary>
    [Fact]
    public void AGroupToggleDrivesOneAnnouncementPassFromThePublisherAlone()
    {
        using var config = FreshInstallation();
        using var store = new MqttConfigStore(config, raise: work => work());
        var groups = new PublishGroupSet(store, MqttEntityTable.Groups);

        int passes = 0;
        using var publisher = new DiscoveryPublisher(new DiscoveryPublisherSetup
        {
            IsConnected       = () => false,
            TopicRoot         = MqttEntityTable.TopicRoot,
            Device            = new DiscoveryDevice("ZeroZero Software", "HyperVManagerTray", "0.0.0"),
            Origin            = new DiscoveryOrigin("HyperVManagerTray", "0.0.0"),
            Entities          = MqttEntitySet.Empty,
            Ledger            = new TransientLedgerStore(),
            Groups            = groups,
            // The channel hand-over runs once per pass, before the pass can be skipped for want of a
            // connection — so it counts passes without a broker or a publisher double.
            SetChannelsAsync  = (_, _) => { passes++; return Task.CompletedTask; },
            SetCommandTargets = DiscoveryWiring.NoCommandHandover,
        });

        groups.Set(MqttEntityTable.MetricsGroup, true);

        Assert.Equal(1, passes);
    }
}
