using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The decisions <c>MqttService</c> takes at its call sites. Each has a cost for being wrong that a
/// broker cannot demonstrate on demand:
///
/// <list type="bullet">
///   <item><see cref="MqttReconcile.Superseded"/>: two reloads in quick succession each schedule a
///   reconcile carrying its own config snapshot; the older one landing last writes the user's newer
///   settings back out.</item>
///   <item><see cref="MqttReconcile.CanClear"/>: clearing an abandoned identity with no live session
///   reaches nothing and costs the reconnect the new settings are for.</item>
///   <item><see cref="MqttReconcile.NeedsApply"/>: a remembered endpoint is written back BY the
///   session it describes, so re-applying for it reconnects once per successful connect.</item>
/// </list>
/// </summary>
public class MqttReconcileTests
{
    private const string Root = "hypervmanagertray";
    private const string Machine = "lab-01";

    private static MqttIdentity Identity(string nodeId, string prefix = "homeassistant") =>
        MqttIdentity.For(new MqttSettings { NodeId = nodeId, DiscoveryPrefix = prefix }, Root, Machine);

    // ── A reconcile overtaken by a newer one ───────────────────────────────────

    [Fact]
    public void The_newest_ticket_is_the_one_that_runs()
    {
        Assert.False(MqttReconcile.Superseded(ticket: 7, latest: 7));
    }

    /// <summary>The whole point: an older snapshot must stand down rather than roll the newer one back.</summary>
    [Fact]
    public void An_older_ticket_stands_down()
    {
        Assert.True(MqttReconcile.Superseded(ticket: 6, latest: 7));
    }

    // ── Clearing an abandoned identity ─────────────────────────────────────────

    [Fact]
    public void A_moved_identity_on_a_live_session_is_cleared()
    {
        Assert.True(MqttReconcile.CanClear(
            hasNode: true, hasConnection: true, isConnected: true, hasTopics: true,
            published: Identity("lab"), next: Identity("dock")));
    }

    /// <summary>Nothing has been published yet, so nothing can be stranded.</summary>
    [Fact]
    public void An_unpublished_identity_is_not_cleared()
    {
        Assert.False(MqttReconcile.CanClear(
            hasNode: true, hasConnection: true, isConnected: true, hasTopics: true,
            published: null, next: Identity("dock")));
    }

    [Fact]
    public void An_unchanged_identity_is_not_cleared()
    {
        Assert.False(MqttReconcile.CanClear(
            hasNode: true, hasConnection: true, isConnected: true, hasTopics: true,
            published: Identity("lab"), next: Identity("lab")));
    }

    /// <summary>A discovery-prefix move strands just as much as a node-id move: the configs live
    /// under the prefix.</summary>
    [Fact]
    public void A_prefix_move_alone_is_cleared()
    {
        Assert.True(MqttReconcile.CanClear(
            hasNode: true, hasConnection: true, isConnected: true, hasTopics: true,
            published: Identity("lab", "homeassistant"), next: Identity("lab", "ha")));
    }

    [Theory]
    // Nothing built yet, so there is no node to clear through.
    [InlineData(false, true, true, true)]
    // No connection to clear over.
    [InlineData(true, false, true, true)]
    // The broker is not answering: nothing retained is reachable from this process.
    [InlineData(true, true, false, true)]
    // The connection has never been applied, so it addresses no topics.
    [InlineData(true, true, true, false)]
    public void Every_missing_term_stands_the_clear_down(
        bool hasNode, bool hasConnection, bool isConnected, bool hasTopics)
    {
        Assert.False(MqttReconcile.CanClear(hasNode, hasConnection, isConnected, hasTopics,
                                            published: Identity("lab"), next: Identity("dock")));
    }

    // ── Rebuilding the node and connection ─────────────────────────────────────

    [Fact]
    public void Nothing_built_yet_needs_a_connection()
    {
        Assert.True(MqttReconcile.NeedsRecreate(hasConnection: false, "same", "same"));
    }

    [Fact]
    public void A_moved_identity_needs_a_fresh_connection()
    {
        Assert.True(MqttReconcile.NeedsRecreate(hasConnection: true, "Tray (lab) homeassistant",
                                                                    "Tray (dock) homeassistant"));
    }

    [Fact]
    public void An_unchanged_identity_keeps_the_live_connection()
    {
        Assert.False(MqttReconcile.NeedsRecreate(hasConnection: true, "Tray (lab) homeassistant",
                                                                     "Tray (lab) homeassistant"));
    }

    // ── Republishing the entity set ────────────────────────────────────────────

    private static PublishCategories All => new(true, true, true, true);

    [Fact]
    public void An_unchanged_set_is_not_republished()
    {
        Assert.False(MqttReconcile.NeedsEntityRebuild(
            ["DevBox"], ["DevBox"], ["Bridged"], ["Bridged"], All, All));
    }

    [Fact]
    public void A_new_managed_vm_republishes_the_set()
    {
        Assert.True(MqttReconcile.NeedsEntityRebuild(
            ["DevBox"], ["DevBox", "Lab"], ["Bridged"], ["Bridged"], All, All));
    }

    /// <summary>The rule switches are the switch-override select's options, so a change to them changes
    /// what is announced.</summary>
    [Fact]
    public void A_new_rule_switch_republishes_the_set()
    {
        Assert.True(MqttReconcile.NeedsEntityRebuild(
            ["DevBox"], ["DevBox"], ["Bridged"], ["Bridged", "Isolated"], All, All));
    }

    /// <summary>Without this, a category switched off in Settings leaves its entities in Home Assistant
    /// until the next connect, reporting nothing.</summary>
    [Fact]
    public void A_switched_off_category_republishes_the_set()
    {
        Assert.True(MqttReconcile.NeedsEntityRebuild(
            ["DevBox"], ["DevBox"], ["Bridged"], ["Bridged"], All, All with { Metrics = false }));
    }

    [Fact]
    public void Publish_categories_compare_by_value()
    {
        var settings = new MqttSettings { PublishVmMetrics = true };
        Assert.Equal(PublishCategories.Of(settings), PublishCategories.Of(settings.Copy()));
        Assert.NotEqual(PublishCategories.Of(settings),
                        PublishCategories.Of(new MqttSettings { PublishVmMetrics = false }));
    }

    // ── Re-applying the broker options ─────────────────────────────────────────

    private static MqttOptions Options(string host = "broker.lan", int? port = 1883) => new()
    {
        Enabled = true, Host = host, Port = port, CredentialReference = "broker", NodeId = "lab",
    };

    [Fact]
    public void The_first_reconcile_applies()
    {
        Assert.True(MqttReconcile.NeedsApply(null, string.Empty, Options(), "pw"));
    }

    [Fact]
    public void Unchanged_options_are_not_re_applied()
    {
        Assert.False(MqttReconcile.NeedsApply(Options(), "pw", Options(), "pw"));
    }

    [Fact]
    public void A_moved_host_is_re_applied()
    {
        Assert.True(MqttReconcile.NeedsApply(Options(), "pw", Options("other.lan"), "pw"));
    }

    /// <summary>The password never reaches <see cref="MqttOptions"/>, so it has to be compared beside it
    /// or a corrected password would never reconnect.</summary>
    [Fact]
    public void A_changed_password_is_re_applied()
    {
        Assert.True(MqttReconcile.NeedsApply(Options(), "old", Options(), "new"));
    }

    [Fact]
    public void A_null_password_reads_as_blank()
    {
        Assert.False(MqttReconcile.NeedsApply(Options(), string.Empty, Options(), null));
    }

    /// <summary>The endpoint is written back BY the session it describes. Re-applying for it drops that
    /// session, which reconnects, which remembers again — one reconnect per successful connect.</summary>
    [Fact]
    public void A_newly_remembered_endpoint_is_not_re_applied()
    {
        var next = Options() with
        {
            LastGoodEndpoint = new MqttEndpointMemory("broker.lan", "", 1883, MqttTransport.Tcp),
        };
        Assert.False(MqttReconcile.NeedsApply(Options(), "pw", next, "pw"));
    }
}
