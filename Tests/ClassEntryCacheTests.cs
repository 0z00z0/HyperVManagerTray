using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Issue #50: the network Class key must be walked only when it actually has to be.
///
/// <para><b>What these tests assert is the BOUND, not the answer.</b> Every one of these resolutions
/// already worked before the cache existed — the bug was that each cost a whole-Class-key walk, on a
/// path that runs on every <c>NetworkChange</c>. So a test that only checked the resolved device id
/// would have passed against the code this replaced and proved nothing. <see cref="ClassEntryCache.Walks"/>
/// is the observable that carries the fix, so it is what is asserted, alongside the answer staying
/// right.</para>
/// </summary>
public class ClassEntryCacheTests
{
    private const string GuidA = "{AAAAAAAA-1111-2222-3333-444444444444}";
    private const string GuidB = "{BBBBBBBB-1111-2222-3333-444444444444}";

    private static AdapterNameRules.ClassAdapterEntry Entry(string netCfg, string dev) => new(netCfg, dev);

    /// <summary>A fake Class key whose contents the test can change between walks — standing in for a
    /// dock being plugged in — and which counts how often it was actually read.</summary>
    private sealed class FakeKey(params AdapterNameRules.ClassAdapterEntry[] initial)
    {
        private List<AdapterNameRules.ClassAdapterEntry> _entries = [.. initial];
        public int Reads { get; private set; }

        public List<AdapterNameRules.ClassAdapterEntry> Read()
        {
            Reads++;
            return [.. _entries];
        }

        public void SetTo(params AdapterNameRules.ClassAdapterEntry[] entries) => _entries = [.. entries];
    }

    // ── The bound ────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstResolve_WalksOnce()
    {
        var key   = new FakeKey(Entry(GuidA, @"PCI\DEV_A"));
        var cache = new ClassEntryCache(key.Read);

        var r = cache.Resolve(GuidA);

        Assert.True(r.Success);
        Assert.Equal(@"PCI\DEV_A", r.DeviceInstanceId);
        Assert.Equal(1, cache.Walks);
    }

    /// <summary>
    /// THE test for this issue. Before the cache, every one of these resolutions walked the whole Class
    /// key; <c>BuildResult</c> does one per rule evaluation, i.e. one per NetworkChange event, for a
    /// display-only string. Twenty resolutions must now cost one walk.
    /// </summary>
    [Fact]
    public void RepeatedResolves_ReuseTheFirstWalk()
    {
        var key   = new FakeKey(Entry(GuidA, @"PCI\DEV_A"), Entry(GuidB, @"USB\DEV_B"));
        var cache = new ClassEntryCache(key.Read);

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(@"PCI\DEV_A", cache.Resolve(GuidA).DeviceInstanceId);
            Assert.Equal(@"USB\DEV_B", cache.Resolve(GuidB).DeviceInstanceId);
        }

        Assert.Equal(1, cache.Walks);
        Assert.Equal(1, key.Reads);
    }

    /// <summary>
    /// A cold cache that cannot answer must NOT walk twice: it just read the key, so a second read would
    /// return the same entries and reach the same answer. Without this guard the refresh-on-miss rule
    /// below doubles the cost of exactly the adapter that was already the most expensive — one with no
    /// Class entry at all, which resolves by falling back every single time.
    /// </summary>
    [Fact]
    public void ColdCacheMiss_DoesNotWalkTwice()
    {
        var key   = new FakeKey(Entry(GuidA, @"PCI\DEV_A"));
        var cache = new ClassEntryCache(key.Read);

        Assert.False(cache.Resolve(GuidB).Success);
        Assert.Equal(1, cache.Walks);
    }

    // ── Self-healing: the cache must never answer for a device set it no longer describes ──

    /// <summary>
    /// A dock plugged in after the cache warmed. The new adapter is absent from the cached entries, so
    /// the cache re-walks and finds it. Without this the adapter would resolve to nothing and display its
    /// raw description forever — issue #32's failure, reintroduced by a cache.
    /// </summary>
    [Fact]
    public void WarmCacheMiss_RewalksAndFindsTheNewDevice()
    {
        var key   = new FakeKey(Entry(GuidA, @"PCI\DEV_A"));
        var cache = new ClassEntryCache(key.Read);

        Assert.True(cache.Resolve(GuidA).Success);   // warms it
        Assert.Equal(1, cache.Walks);

        key.SetTo(Entry(GuidA, @"PCI\DEV_A"), Entry(GuidB, @"USB\DEV_B"));   // dock arrives

        var r = cache.Resolve(GuidB);

        Assert.True(r.Success);
        Assert.Equal(@"USB\DEV_B", r.DeviceInstanceId);
        Assert.Equal(2, cache.Walks);
    }

    /// <summary>
    /// The re-walk happens at most ONCE per resolve. An adapter that genuinely has no Class entry (not a
    /// stale cache — a real absence) must cost one walk, not an unbounded retry loop.
    /// </summary>
    [Fact]
    public void WarmCacheMiss_ThatIsARealAbsence_WalksOncePerResolve()
    {
        var key   = new FakeKey(Entry(GuidA, @"PCI\DEV_A"));
        var cache = new ClassEntryCache(key.Read);

        cache.Resolve(GuidA);              // warm: walk 1
        Assert.False(cache.Resolve(GuidB).Success);   // walk 2 (refresh), still absent
        Assert.Equal(2, cache.Walks);
    }

    /// <summary>
    /// An adapter re-installed under a new device instance while the cache held the old one. The stale
    /// entry makes the GUID resolve to TWO devices, which <c>ResolveDeviceInstanceId</c> refuses (it
    /// never guesses which dock). That refusal is a non-success, so it re-walks — and the fresh entries
    /// hold exactly one. A cache that treated only 0-matches as "cannot answer" would abort here forever.
    /// </summary>
    [Fact]
    public void WarmCacheAmbiguity_RewalksAndResolvesCleanly()
    {
        var key   = new FakeKey(Entry(GuidA, @"PCI\DEV_OLD"));
        var cache = new ClassEntryCache(key.Read);

        cache.Resolve(GuidA);   // warm

        // The re-install: same NetCfgInstanceId, new device instance. A cache holding both is ambiguous.
        key.SetTo(Entry(GuidA, @"PCI\DEV_OLD"), Entry(GuidA, @"PCI\DEV_NEW"));
        Assert.False(cache.Resolve(GuidB).Success);   // forces the refresh; now the cache is ambiguous

        key.SetTo(Entry(GuidA, @"PCI\DEV_NEW"));      // the old entry is gone for good
        var r = cache.Resolve(GuidA);

        Assert.True(r.Success);
        Assert.Equal(@"PCI\DEV_NEW", r.DeviceInstanceId);
    }

    // ── Degradation ──────────────────────────────────────────────────────────────

    /// <summary>An unreadable Class key degrades to "no entries" — the caller falls back to the
    /// description — and never throws out of a display path.</summary>
    [Fact]
    public void UnreadableKey_DegradesToAFailureAndNeverThrows()
    {
        var cache = new ClassEntryCache(() => throw new UnauthorizedAccessException("hive fault"));

        var r = cache.Resolve(GuidA);

        Assert.False(r.Success);
        Assert.Null(r.DeviceInstanceId);
    }

    /// <summary>A key that is unreadable once and readable later must not be written off: the failure
    /// cached "no entries", and a later resolve for an absent GUID re-walks and picks up the real ones.</summary>
    [Fact]
    public void UnreadableKey_RecoversOnALaterWalk()
    {
        bool broken = true;
        var  cache  = new ClassEntryCache(() =>
            broken ? throw new UnauthorizedAccessException("hive fault")
                   : [Entry(GuidA, @"PCI\DEV_A")]);

        Assert.False(cache.Resolve(GuidA).Success);
        broken = false;

        Assert.True(cache.Resolve(GuidA).Success);   // warm-miss ⇒ refresh ⇒ found
    }

    [Fact]
    public void Invalidate_ForcesTheNextResolveToWalk()
    {
        var key   = new FakeKey(Entry(GuidA, @"PCI\DEV_A"));
        var cache = new ClassEntryCache(key.Read);

        cache.Resolve(GuidA);
        cache.Resolve(GuidA);
        Assert.Equal(1, cache.Walks);

        cache.Invalidate();
        cache.Resolve(GuidA);

        Assert.Equal(2, cache.Walks);
    }

    /// <summary>The cache changes when a walk happens, never what a walk means: resolution stays
    /// <c>AdapterNameRules.ResolveDeviceInstanceId</c>'s (case-insensitive, per its own tests).</summary>
    [Fact]
    public void Resolve_StaysCaseInsensitive_LikeTheRuleItDelegatesTo()
    {
        var cache = new ClassEntryCache(() => [Entry(GuidA.ToUpperInvariant(), @"PCI\DEV_A")]);

        Assert.Equal(@"PCI\DEV_A", cache.Resolve(GuidA.ToLowerInvariant()).DeviceInstanceId);
    }
}
