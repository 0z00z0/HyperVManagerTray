using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Issue #50: the rename flow's only call site is fed from the host inventory Settings already read, so
/// a rename click must not re-sweep every NIC to rebuild it. These cover the two rules that make reusing
/// it safe — when the caller's list may stand in for a live sweep, and what is compared against what.
/// </summary>
public class AdapterRenameReuseTests
{
    private const string GuidA = "{AAAAAAAA-0000-0000-0000-000000000000}";
    private const string GuidB = "{BBBBBBBB-0000-0000-0000-000000000000}";
    private const string GuidC = "{CCCCCCCC-0000-0000-0000-000000000000}";

    private static PhysicalAdapterInfo Adapter(string guid, string display, string? description = null) =>
        new("Ethernet", description ?? "Raw Factory Description", guid, "AA:BB:CC:DD:EE:FF", display);

    /// <summary>A list the caller says still describes the host.</summary>
    private static AdapterMatcher.KnownAdapters Current(params PhysicalAdapterInfo[] adapters) =>
        new(adapters, IsCurrent: true);

    /// <summary>A list the caller says the host has moved on from.</summary>
    private static AdapterMatcher.KnownAdapters Stale(params PhysicalAdapterInfo[] adapters) =>
        new(adapters, IsCurrent: false);

    // ── When the caller's list may stand in for a live sweep ─────────────────────

    /// <summary>Settings never populated an inventory (host unreachable, or the read has not landed).
    /// The flow must enumerate live, exactly as it always did — this is the degraded path the issue
    /// explicitly requires to keep working.</summary>
    [Fact]
    public void NoInventory_FallsBackToALiveSweep()
    {
        Assert.False(AdapterMatcher.CanRenameFromKnownAdapters(null, GuidA));
        Assert.False(AdapterMatcher.CanRenameFromKnownAdapters(Current(), GuidA));
    }

    [Fact]
    public void CurrentInventoryContainingTheAdapter_MayBeReused()
    {
        Assert.True(AdapterMatcher.CanRenameFromKnownAdapters(
            Current(Adapter(GuidA, "Office dock"), Adapter(GuidB, "Home dock")), GuidA));
    }

    /// <summary>
    /// <b>The load-bearing clause, and the one the membership test could never stand in for.</b> This is
    /// the dock swap: Settings read [Ethernet, Dock LAN]; the user unplugged that dock and plugged in
    /// another. Nothing was renamed, so nothing THIS APP did marks the list stale — only the host knows,
    /// and it says so through NetworkChange. The adapter being renamed (Ethernet) is still right there in
    /// the list, so every membership test passes; what has changed is the set of names the new one must be
    /// unique AGAINST. Reusing it here is how the rename dialog accepts a description a present adapter is
    /// already carrying (§5.5, issue #32).
    /// </summary>
    [Fact]
    public void StaleInventory_IsNotReused_EvenWhenItContainsTheAdapter()
    {
        var adapters = new[] { Adapter(GuidA, "Ethernet"), Adapter(GuidB, "Dock LAN") };

        // Same list, same subject — the ONLY difference is what the caller knows about the host since.
        Assert.True(AdapterMatcher.CanRenameFromKnownAdapters(Current(adapters), GuidA));
        Assert.False(AdapterMatcher.CanRenameFromKnownAdapters(Stale(adapters), GuidA));
    }

    /// <summary>
    /// Second line of defence, not the test (see the gate's remarks): the flow's only call site builds its
    /// rename buttons FROM this list, so the subject is present by construction and this clause is a
    /// tautology on the real path. It stays because a list declared current that nevertheless lacks the
    /// adapter describes a host the caller does not have, and "I cannot see the subject" is never grounds
    /// to trust the comparands.
    /// </summary>
    [Fact]
    public void InventoryWithoutTheAdapter_IsNotReused()
    {
        Assert.False(AdapterMatcher.CanRenameFromKnownAdapters(
            Current(Adapter(GuidA, "Office dock"), Adapter(GuidB, "Home dock")), GuidC));
    }

    /// <summary>Interface GUIDs compare case-insensitively everywhere else in this app; a casing
    /// difference must not force a needless live sweep.</summary>
    [Fact]
    public void GuidMatching_IsCaseInsensitive()
    {
        Assert.True(AdapterMatcher.CanRenameFromKnownAdapters(
            Current(Adapter(GuidA.ToUpperInvariant(), "Office dock")), GuidA.ToLowerInvariant()));
    }

    // ── What the uniqueness check compares ───────────────────────────────────────

    [Fact]
    public void OtherAdapterDisplayNames_ExcludesTheAdapterBeingRenamed()
    {
        IReadOnlyList<PhysicalAdapterInfo> all =
            [Adapter(GuidA, "Office dock"), Adapter(GuidB, "Home dock"), Adapter(GuidC, "Onboard")];

        var others = AdapterMatcher.OtherAdapterDisplayNames(all, GuidA);

        Assert.Equal(["Home dock", "Onboard"], others);
    }

    /// <summary>
    /// Issue #32, guarded here because this is where the two strings meet: the rename writes a
    /// FriendlyName, so the names it must be unique against are the others' FriendlyNames — DisplayName.
    /// Returning Description would compare the candidate against factory strings nobody is using and let
    /// a rename collide with a name that IS in use.
    /// </summary>
    [Fact]
    public void OtherAdapterDisplayNames_ReturnsDisplayNames_NotDescriptions()
    {
        IReadOnlyList<PhysicalAdapterInfo> all =
        [
            Adapter(GuidA, "Office dock",  description: "Realtek USB GbE Family Controller"),
            Adapter(GuidB, "Home dock",    description: "Realtek USB GbE Family Controller #2"),
        ];

        var others = AdapterMatcher.OtherAdapterDisplayNames(all, GuidA);

        Assert.Equal(["Home dock"], others);
        Assert.DoesNotContain("Realtek USB GbE Family Controller #2", others);
    }

    [Fact]
    public void OtherAdapterDisplayNames_ExcludesSelfCaseInsensitively()
    {
        IReadOnlyList<PhysicalAdapterInfo> all = [Adapter(GuidA.ToUpperInvariant(), "Office dock"), Adapter(GuidB, "Home dock")];

        Assert.Equal(["Home dock"], AdapterMatcher.OtherAdapterDisplayNames(all, GuidA.ToLowerInvariant()));
    }

    /// <summary>A single-adapter host: nothing to be unique against, and no exception.</summary>
    [Fact]
    public void OtherAdapterDisplayNames_SoleAdapterYieldsNothing()
    {
        Assert.Empty(AdapterMatcher.OtherAdapterDisplayNames([Adapter(GuidA, "Office dock")], GuidA));
    }

    // ── The harm the currency clause prevents, end to end ────────────────────────

    /// <summary>
    /// Ties the gate to the thing it protects. Settings read [Ethernet, Dock LAN] and the user then swapped
    /// docks — so the host now carries [Ethernet, Dock LAN 2] while the snapshot still says otherwise. The
    /// user renames Ethernet to "Dock LAN 2".
    ///
    /// <para>This is what makes the currency clause more than tidiness: the uniqueness check downstream is
    /// working perfectly. Given the stale comparands it CORRECTLY reports the name as free, because in that
    /// list it is. The only defence against two adapters ending up with one description is refusing to hand
    /// it those comparands in the first place.</para>
    /// </summary>
    [Fact]
    public void RenamingToASwappedInDocksName_IsOnlyCaughtBecauseTheStaleListIsRefused()
    {
        var beforeTheSwap = new[] { Adapter(GuidA, "Ethernet"), Adapter(GuidB, "Dock LAN") };
        var onTheHostNow  = new[] { Adapter(GuidA, "Ethernet"), Adapter(GuidC, "Dock LAN 2") };

        // The gate refuses the stale list, so the flow sweeps live and compares against the real host.
        Assert.False(AdapterMatcher.CanRenameFromKnownAdapters(Stale(beforeTheSwap), GuidA));
        Assert.False(AdapterNameRules.IsNameUnique(
            "Dock LAN 2", AdapterMatcher.OtherAdapterDisplayNames(onTheHostNow, GuidA)));

        // Had it been reused, the collision would have been invisible: the name is genuinely absent from
        // the pre-swap list, so nothing further down the flow could have caught it.
        Assert.True(AdapterNameRules.IsNameUnique(
            "Dock LAN 2", AdapterMatcher.OtherAdapterDisplayNames(beforeTheSwap, GuidA)));
    }
}
