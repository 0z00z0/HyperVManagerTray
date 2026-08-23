using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.HomeAssistant;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The seam between this app's <c>mqtt</c> config section and the shared WinUI settings panel (issue
/// #75). The panel edits a snapshot and reports each edit back as one facet; these are the mappings
/// that land those facets on the stored settings.
///
/// <para>Assertable here precisely because the arithmetic is not in the panel: the panel is WinUI and
/// this test assembly hosts no Windows App SDK runtime, so anything decided inside the control would
/// be untestable by construction.</para>
/// </summary>
public class MqttPanelSeamTests
{
    /// <summary>Settings whose every field differs from its default, so a mapping that drops a field
    /// cannot look like a pass.</summary>
    private static MqttSettings Stored() => new()
    {
        Enabled              = true,
        Host                 = "broker.lan",
        Port                 = 1884,
        Transport            = MqttTransportSetting.WebSocket,
        UseTls               = true,
        Username             = "hvmt",
        Password             = "s3cret",
        DiscoveryPrefix      = "ha",
        DeviceName           = "Hyper-V host",
        NodeId               = "hypervmanagertray_lab",
        PublishNetwork       = false,
        PublishVmState       = false,
        PublishVmDiagnostics = false,
        PublishVmMetrics     = true,
        LastGoodEndpoint     = new MqttEndpointMemory("broker.lan", "hvmt", 1884, MqttTransport.WebSocket),
    };

    // ── Settings → the snapshot the panel opens on ─────────────────────────────

    /// <summary>The broker half of the snapshot is <see cref="MqttSettings.ToOptions"/>, and it carries
    /// a credential REFERENCE — never the password, which reaches the panel through the store.</summary>
    [Fact]
    public void ToOptions_carries_the_broker_fields_and_no_password()
    {
        var options = Stored().ToOptions();

        Assert.True(options.Enabled);
        Assert.Equal("broker.lan", options.Host);
        Assert.Equal(1884, options.Port);
        Assert.Equal(MqttTransportSetting.WebSocket, options.Transport);
        Assert.True(options.UseTls);
        Assert.Equal("hvmt", options.Username);
        Assert.Equal("hypervmanagertray_lab", options.NodeId);
        Assert.Equal(MqttSettings.CredentialReference, options.CredentialReference);
        Assert.Equal(new MqttEndpointMemory("broker.lan", "hvmt", 1884, MqttTransport.WebSocket),
                     options.LastGoodEndpoint);

        // MqttOptions has nowhere to put one, and that is the point of the credential store.
        Assert.DoesNotContain("s3cret", options.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A blank prefix is shown as the prefix actually in force. Blank in the field would read
    /// as "the configs go nowhere", which is not what a blank setting means.</summary>
    [Fact]
    public void A_blank_discovery_prefix_shows_as_Home_Assistants_default()
    {
        var settings = new MqttSettings { DiscoveryPrefix = "  " };
        Assert.Equal(HaDiscoveryContext.DefaultPrefix, MqttNaming.EffectiveDiscoveryPrefix(settings));
    }

    [Fact]
    public void A_set_discovery_prefix_is_shown_trimmed()
    {
        var settings = new MqttSettings { DiscoveryPrefix = "  ha  " };
        Assert.Equal("ha", MqttNaming.EffectiveDiscoveryPrefix(settings));
    }

    /// <summary>The device-name placeholder is the name the service actually publishes under, so a
    /// blank field keeps meaning "the default" rather than naming a different default.</summary>
    [Fact]
    public void A_blank_device_name_falls_back_to_the_published_default()
    {
        Assert.Equal(MqttNaming.DefaultDeviceName, MqttNaming.EffectiveDeviceName(new MqttSettings()));
        Assert.Equal(MqttNaming.DefaultDeviceName,
                     MqttNaming.EffectiveDeviceName(new MqttSettings { DeviceName = "   " }));
        Assert.Equal("Hyper-V host", MqttNaming.EffectiveDeviceName(Stored()));
    }

    // ── The publish categories ─────────────────────────────────────────────────

    /// <summary>Every key the window offers reads and writes its own flag, and nothing else's.</summary>
    [Theory]
    [InlineData(MqttPanelSeam.NetworkKey)]
    [InlineData(MqttPanelSeam.VmStateKey)]
    [InlineData(MqttPanelSeam.VmDiagnosticsKey)]
    [InlineData(MqttPanelSeam.VmMetricsKey)]
    public void A_category_toggle_moves_that_category_only(string key)
    {
        var before = new MqttSettings();   // all three groups on, metrics off
        var on  = MqttPanelSeam.WithCategory(before, key, isOn: true);
        var off = MqttPanelSeam.WithCategory(on, key, isOn: false);

        Assert.True(MqttPanelSeam.IsOn(on, key));
        Assert.False(MqttPanelSeam.IsOn(off, key));

        foreach (string other in MqttPanelSeam.Keys.Where(k => k != key))
            Assert.Equal(MqttPanelSeam.IsOn(before, other), MqttPanelSeam.IsOn(off, other));
    }

    /// <summary>The four keys are the whole vocabulary — a fifth would be a toggle nothing reads.</summary>
    [Fact]
    public void The_category_keys_are_exactly_the_four_published_groups()
    {
        Assert.Equal(
            [MqttPanelSeam.NetworkKey, MqttPanelSeam.VmStateKey,
             MqttPanelSeam.VmDiagnosticsKey, MqttPanelSeam.VmMetricsKey],
            MqttPanelSeam.Keys);
    }

    /// <summary>
    /// An unrecognised key changes nothing. A dead toggle is visible; a key that fell through to some
    /// other flag would not be.
    ///
    /// <para>Both values, and from settings where the four flags are not all alike: a fall-through
    /// that happened to write the value a flag already held would otherwise pass unnoticed.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unknown_category_key_writes_nothing(bool isOn)
    {
        var before = Stored();   // three groups off, metrics on — so either value moves something
        var after  = MqttPanelSeam.WithCategory(before, "publishEverything", isOn);

        Assert.False(MqttPanelSeam.IsOn(before, "publishEverything"));
        foreach (string key in MqttPanelSeam.Keys)
            Assert.Equal(MqttPanelSeam.IsOn(before, key), MqttPanelSeam.IsOn(after, key));
    }

    /// <summary>Three groups publish what the app already knows, so they default on; metrics costs a
    /// 2.5 s WMI poll, so it does not.</summary>
    [Fact]
    public void Only_the_category_that_costs_a_poll_defaults_off()
    {
        var fresh = new MqttSettings();

        Assert.True(MqttPanelSeam.IsOn(fresh, MqttPanelSeam.NetworkKey));
        Assert.True(MqttPanelSeam.IsOn(fresh, MqttPanelSeam.VmStateKey));
        Assert.True(MqttPanelSeam.IsOn(fresh, MqttPanelSeam.VmDiagnosticsKey));
        Assert.False(MqttPanelSeam.IsOn(fresh, MqttPanelSeam.VmMetricsKey));
    }

    // ── The immediate commits ──────────────────────────────────────────────────

    [Fact]
    public void The_master_toggle_moves_only_Enabled()
    {
        var before = Stored();
        var after  = MqttPanelSeam.WithEnabled(before, enabled: false);

        Assert.False(after.Enabled);
        AssertOnlyEnabledDiffers(before, after);
    }

    /// <summary>Blank is the "derive one from the machine name" sentinel, so it is stored as blank
    /// rather than as the derived value — which would freeze today's machine name into the file.</summary>
    [Fact]
    public void A_blank_node_id_is_stored_blank_not_resolved()
    {
        var after = MqttPanelSeam.WithNodeId(Stored(), "   ");

        Assert.Equal(string.Empty, after.NodeId);
        Assert.NotEqual(string.Empty,
                        MqttNaming.EffectiveNodeId(after, "hypervmanagertray", "lab-01"));
    }

    [Fact]
    public void A_confirmed_node_id_is_stored_trimmed()
    {
        var after = MqttPanelSeam.WithNodeId(Stored(), "  hypervmanagertray_dock  ");
        Assert.Equal("hypervmanagertray_dock", after.NodeId);
    }

    // ── The broker batch ───────────────────────────────────────────────────────

    /// <summary>Apply commits the broker fields, the device name, the prefix and the password as one.</summary>
    [Fact]
    public void The_broker_batch_writes_every_field_it_owns()
    {
        var committed = new MqttOptions
        {
            Host      = "  broker2.lan ",
            Port      = 8883,
            Transport = MqttTransportSetting.Tcp,
            UseTls    = false,
            Username  = "  other ",
        };

        var after = MqttPanelSeam.WithBroker(Stored(), committed, " Lab host ", " ha2 ", "n3w");

        Assert.Equal("broker2.lan", after.Host);
        Assert.Equal(8883, after.Port);
        Assert.Equal(MqttTransportSetting.Tcp, after.Transport);
        Assert.False(after.UseTls);
        Assert.Equal("other", after.Username);
        Assert.Equal("Lab host", after.DeviceName);
        Assert.Equal("ha2", after.DiscoveryPrefix);
        Assert.Equal("n3w", after.Password);
    }

    /// <summary>
    /// And writes nothing else. The panel holds its snapshot from the moment it opened, so taking the
    /// master toggle, the node id or the remembered endpoint from it would roll back whichever of them
    /// was committed meanwhile — each has its own commit path for exactly that reason.
    /// </summary>
    [Fact]
    public void The_broker_batch_cannot_roll_back_a_facet_committed_meanwhile()
    {
        var stale = Stored().ToOptions() with
        {
            Enabled          = false,
            NodeId           = "an_older_id",
            LastGoodEndpoint = null,
        };

        var current = Stored();
        current.PublishVmMetrics = false;

        var after = MqttPanelSeam.WithBroker(current, stale, "Hyper-V host", "ha", "s3cret");

        Assert.True(after.Enabled);
        Assert.Equal("hypervmanagertray_lab", after.NodeId);
        Assert.Equal(current.LastGoodEndpoint, after.LastGoodEndpoint);
        Assert.False(after.PublishVmMetrics);
    }

    /// <summary>Nulls off the wire land as empty strings, never as a null in the config file.</summary>
    [Fact]
    public void The_broker_batch_normalises_nulls()
    {
        var after = MqttPanelSeam.WithBroker(
            Stored(), new MqttOptions { Host = null!, Username = null! }, null, null, null);

        Assert.Equal(string.Empty, after.Host);
        Assert.Equal(string.Empty, after.Username);
        Assert.Equal(string.Empty, after.DeviceName);
        Assert.Equal(string.Empty, after.DiscoveryPrefix);
        Assert.Equal(string.Empty, after.Password);
    }

    /// <summary>No mapping hands the live settings object to a writer: each returns a copy, so a save
    /// that is refused cannot have already mutated what the app is running on.</summary>
    [Fact]
    public void Every_mapping_returns_a_copy()
    {
        var before = Stored();

        Assert.NotSame(before, MqttPanelSeam.WithEnabled(before, false));
        Assert.NotSame(before, MqttPanelSeam.WithNodeId(before, "x"));
        Assert.NotSame(before, MqttPanelSeam.WithCategory(before, MqttPanelSeam.NetworkKey, true));
        Assert.NotSame(before, MqttPanelSeam.WithBroker(before, before.ToOptions(), "d", "p", "w"));

        Assert.True(before.Enabled);   // the original is untouched by any of them
        Assert.Equal("hypervmanagertray_lab", before.NodeId);
    }

    private static void AssertOnlyEnabledDiffers(MqttSettings before, MqttSettings after)
    {
        Assert.Equal(before.Host, after.Host);
        Assert.Equal(before.Port, after.Port);
        Assert.Equal(before.Transport, after.Transport);
        Assert.Equal(before.UseTls, after.UseTls);
        Assert.Equal(before.Username, after.Username);
        Assert.Equal(before.Password, after.Password);
        Assert.Equal(before.DiscoveryPrefix, after.DiscoveryPrefix);
        Assert.Equal(before.DeviceName, after.DeviceName);
        Assert.Equal(before.NodeId, after.NodeId);
        Assert.Equal(before.LastGoodEndpoint, after.LastGoodEndpoint);
        foreach (string key in MqttPanelSeam.Keys)
            Assert.Equal(MqttPanelSeam.IsOn(before, key), MqttPanelSeam.IsOn(after, key));
    }
}
