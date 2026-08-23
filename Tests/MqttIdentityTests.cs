using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;
using ZeroZero.Mqtt.HomeAssistant;

namespace HyperVManagerTray.Tests;

/// <summary>The address the retained topics are filed under, and the decision that keeps a changed one
/// from stranding them. What stranding costs: <c>docs/mqtt-integration.md</c>.</summary>
public class MqttIdentityTests
{
    private const string Root = "hypervmanagertray";
    private const string Machine = "lab-01";

    private static MqttIdentity Identity(MqttSettings settings) =>
        MqttIdentity.For(settings, Root, Machine);

    // ── What the identity is ───────────────────────────────────────────────────

    /// <summary>Blank fields resolve to the pair actually published under, not to blanks: the derived
    /// node id and Home Assistant's own discovery prefix.</summary>
    [Fact]
    public void A_blank_section_resolves_to_the_derived_pair()
    {
        var identity = Identity(new MqttSettings());

        Assert.Equal($"{Root}_lab_01", identity.NodeId);
        Assert.Equal(HaDiscoveryContext.DefaultPrefix, identity.Prefix);
    }

    [Fact]
    public void A_custom_node_id_is_sanitised_the_way_it_is_published()
    {
        var identity = Identity(new MqttSettings { NodeId = "Office ThinkPad" });
        Assert.Equal("office_thinkpad", identity.NodeId);
    }

    // ── When a move strands topics ─────────────────────────────────────────────

    /// <summary>Nothing has been published yet, so there is nothing to strand.</summary>
    [Fact]
    public void Nothing_is_abandoned_before_the_first_publish()
    {
        Assert.False(MqttIdentity.Abandons(null, Identity(new MqttSettings { NodeId = "lab" })));
    }

    [Fact]
    public void A_node_id_change_abandons_the_old_identity()
    {
        var before = Identity(new MqttSettings { NodeId = "lab" });
        var after  = Identity(new MqttSettings { NodeId = "dock" });

        Assert.True(MqttIdentity.Abandons(before, after));
    }

    /// <summary>Clearing a custom id back to blank moves to the derived one, which is just as much of
    /// a move — the entities are renamed either way.</summary>
    [Fact]
    public void Clearing_a_custom_node_id_abandons_the_old_identity()
    {
        var before = Identity(new MqttSettings { NodeId = "lab" });
        var after  = Identity(new MqttSettings { NodeId = "" });

        Assert.True(MqttIdentity.Abandons(before, after));
    }

    [Fact]
    public void A_discovery_prefix_change_abandons_the_old_identity()
    {
        var before = Identity(new MqttSettings { NodeId = "lab", DiscoveryPrefix = "homeassistant" });
        var after  = Identity(new MqttSettings { NodeId = "lab", DiscoveryPrefix = "ha" });

        Assert.True(MqttIdentity.Abandons(before, after));
    }

    // ── When a move strands nothing ────────────────────────────────────────────

    /// <summary>Writing the default prefix out explicitly is not a move: the panel's Apply commits a
    /// blank box as Home Assistant's default, and clearing there evicts every entity for nothing.</summary>
    [Fact]
    public void Spelling_out_the_default_prefix_abandons_nothing()
    {
        var before = Identity(new MqttSettings { NodeId = "lab", DiscoveryPrefix = "" });
        var after  = Identity(new MqttSettings
        {
            NodeId = "lab",
            DiscoveryPrefix = HaDiscoveryContext.DefaultPrefix,
        });

        Assert.Equal(before, after);
        Assert.False(MqttIdentity.Abandons(before, after));
    }

    /// <summary>The device name is not part of the address. It republishes over the same topics, so
    /// clearing them would evict every entity and re-announce it a moment later.</summary>
    [Fact]
    public void A_device_name_change_abandons_nothing()
    {
        var before = Identity(new MqttSettings { NodeId = "lab", DeviceName = "Hyper-V host" });
        var after  = Identity(new MqttSettings { NodeId = "lab", DeviceName = "Lab host" });

        Assert.False(MqttIdentity.Abandons(before, after));
    }

    /// <summary>Neither is anything else in the section. A broker port edit must not evict the entity
    /// set on its way to reconnecting.</summary>
    [Fact]
    public void A_broker_edit_abandons_nothing()
    {
        var before = Identity(new MqttSettings { NodeId = "lab", Host = "broker.lan", Port = 1883 });
        var after  = Identity(new MqttSettings { NodeId = "lab", Host = "broker2.lan", Port = 8883 });

        Assert.False(MqttIdentity.Abandons(before, after));
    }

    /// <summary>Turning publishing off is not a move either: the stop hook clears the SAME identity as
    /// the session ends, so a second clear here publishes to a broker about to be dropped.</summary>
    [Fact]
    public void Disabling_publishing_abandons_nothing()
    {
        var before = Identity(new MqttSettings { NodeId = "lab", Enabled = true });
        var after  = Identity(new MqttSettings { NodeId = "lab", Enabled = false });

        Assert.False(MqttIdentity.Abandons(before, after));
    }

    /// <summary>Whitespace around a node id is not a different id.</summary>
    [Fact]
    public void Retyping_the_same_node_id_with_padding_abandons_nothing()
    {
        var before = Identity(new MqttSettings { NodeId = "lab" });
        var after  = Identity(new MqttSettings { NodeId = "  lab  " });

        Assert.False(MqttIdentity.Abandons(before, after));
    }
}
