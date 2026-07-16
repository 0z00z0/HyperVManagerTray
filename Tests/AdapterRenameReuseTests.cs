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

    // ── When the caller's list may stand in for a live sweep ─────────────────────

    /// <summary>Settings never populated an inventory (host unreachable, or the read has not landed).
    /// The flow must enumerate live, exactly as it always did — this is the degraded path the issue
    /// explicitly requires to keep working.</summary>
    [Fact]
    public void NoInventory_FallsBackToALiveSweep()
    {
        Assert.False(AdapterMatcher.CanRenameFromKnownAdapters(null, GuidA));
        Assert.False(AdapterMatcher.CanRenameFromKnownAdapters([], GuidA));
    }

    [Fact]
    public void InventoryContainingTheAdapter_MayBeReused()
    {
        IReadOnlyList<PhysicalAdapterInfo> known = [Adapter(GuidA, "Office dock"), Adapter(GuidB, "Home dock")];

        Assert.True(AdapterMatcher.CanRenameFromKnownAdapters(known, GuidA));
    }

    /// <summary>
    /// The load-bearing clause. A non-empty list that does not contain the adapter being renamed is not a
    /// slightly-stale view of this host — it describes a DIFFERENT device set (read before a dock swap).
    /// Answering a uniqueness check from it would compare the new name against names no longer present,
    /// and let two adapters end up sharing one. Non-empty is not the same as fresh.
    /// </summary>
    [Fact]
    public void InventoryWithoutTheAdapter_IsNotReused()
    {
        IReadOnlyList<PhysicalAdapterInfo> known = [Adapter(GuidA, "Office dock"), Adapter(GuidB, "Home dock")];

        Assert.False(AdapterMatcher.CanRenameFromKnownAdapters(known, GuidC));
    }

    /// <summary>Interface GUIDs compare case-insensitively everywhere else in this app; a casing
    /// difference must not force a needless live sweep.</summary>
    [Fact]
    public void GuidMatching_IsCaseInsensitive()
    {
        IReadOnlyList<PhysicalAdapterInfo> known = [Adapter(GuidA.ToUpperInvariant(), "Office dock")];

        Assert.True(AdapterMatcher.CanRenameFromKnownAdapters(known, GuidA.ToLowerInvariant()));
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
}
