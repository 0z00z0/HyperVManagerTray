using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The ordering rule behind "may a host read already taken still be trusted?".
///
/// <para>These are the cases a <c>bool _stale</c> could not express, which is why it was one: a flag
/// records THAT the host changed but not WHEN relative to a read still in flight, so whichever read landed
/// last cleared it — including one that started before the change and knows nothing about it.</para>
/// </summary>
public class HostReadGenerationTests
{
    [Fact]
    public void AReadWithNothingHappeningDuringIt_IsCurrent()
    {
        var gen = new HostReadGeneration();

        var token = gen.BeginRead();

        Assert.True(gen.IsCurrent(token));
    }

    /// <summary>
    /// The rename case (issue #50): a read that began BEFORE the rename must not be trusted after it, no
    /// matter how late it lands. Settings has two independent, unsequenced callers of its host read, so
    /// this read is routinely in flight when a rename starts — and the snapshot it carries still holds the
    /// adapter's PRE-rename description. Trusting it republishes that name as current and feeds the next
    /// rename's uniqueness check a name that no longer exists (issue #32's failure mode).
    /// </summary>
    [Fact]
    public void AReadThatStartedBeforeAnInvalidation_IsNotCurrentWhenItLands()
    {
        var gen = new HostReadGeneration();

        var early = gen.BeginRead();   // e.g. a rebuild's read, already running
        gen.Invalidate();              // a rename lands
        var late  = gen.BeginRead();   // the read the rename asked for

        Assert.False(gen.IsCurrent(early));
        Assert.True(gen.IsCurrent(late));
    }

    /// <summary>
    /// The same read, landing in the OTHER order — the one a flag got wrong. The late read completes
    /// first and is correctly trusted; the early read completing afterwards must still answer "no". With a
    /// bool, this ordering is precisely where the last writer wins and the stale snapshot clears the flag.
    /// </summary>
    [Fact]
    public void AStaleReadLandingAfterAFreshOne_IsStillNotCurrent()
    {
        var gen = new HostReadGeneration();

        var early = gen.BeginRead();
        gen.Invalidate();
        var late  = gen.BeginRead();

        Assert.True(gen.IsCurrent(late));    // the fresh read lands, and is trusted
        Assert.False(gen.IsCurrent(early));  // the stale one lands after it, and is not

        // And the fresh read stays trusted — a late arrival must not unseat it.
        Assert.True(gen.IsCurrent(late));
    }

    /// <summary>
    /// The device-restart case (finding 3): the flow invalidates immediately before disabling the device
    /// and starts NO read of its own, precisely because every enumeration filters on
    /// <c>OperationalStatus == Up</c> and would miss the adapter mid-cycle. So a read in flight across the
    /// restart must answer "not current", and only the read taken AFTER the cycle may be trusted.
    /// </summary>
    [Fact]
    public void AReadSpanningADeviceRestart_IsNotCurrent_ButTheOneAfterItIs()
    {
        var gen = new HostReadGeneration();

        gen.Invalidate();                     // the rename was written
        var duringCycle = gen.BeginRead();    // the read it asked for is running
        gen.Invalidate();                     // "I am about to disable the device"
        // ... the device is down; this read would sample a host missing the adapter ...

        Assert.False(gen.IsCurrent(duringCycle));

        var afterCycle = gen.BeginRead();     // taken once the restart finished
        Assert.True(gen.IsCurrent(afterCycle));
    }

    /// <summary>
    /// A snapshot published while current does not stay current forever: the host moves on without this
    /// app's help. This is the dock-swap case that made the old app-caused-only flag unsound — nothing
    /// renames, so nothing marked the list stale, yet it now describes a different host.
    /// </summary>
    [Fact]
    public void APublishedReadStopsBeingCurrent_WhenTheHostChangesLater()
    {
        var gen = new HostReadGeneration();

        var token = gen.BeginRead();
        Assert.True(gen.IsCurrent(token));

        gen.Invalidate();   // a NetworkChange: a dock was swapped

        Assert.False(gen.IsCurrent(token));
    }

    /// <summary>Invalidations arrive in storms during a dock transition — the event this is fed from fires
    /// repeatedly — so the answer must not depend on how many landed.</summary>
    [Fact]
    public void RepeatedInvalidations_KeepTheReadStale()
    {
        var gen = new HostReadGeneration();

        var token = gen.BeginRead();
        for (var i = 0; i < 50; i++) gen.Invalidate();

        Assert.False(gen.IsCurrent(token));
        Assert.True(gen.IsCurrent(gen.BeginRead()));
    }

    /// <summary>
    /// "Nothing published yet" must never compare current. Settings seeds its held generation with -1 for
    /// exactly this, so the accessor cannot hand out an inventory it never read.
    /// </summary>
    [Fact]
    public void TheNeverReadSentinel_IsNeverCurrent()
    {
        var gen = new HostReadGeneration();

        Assert.False(gen.IsCurrent(-1));

        gen.Invalidate();
        Assert.False(gen.IsCurrent(-1));
    }

    /// <summary>Invalidate arrives on NetworkChange's thread-pool thread while IsCurrent is read on the UI
    /// thread. The counter must not lose increments under that — a lost one is a stale read reported as
    /// current, which is the whole failure this type exists to prevent.</summary>
    [Fact]
    public void ConcurrentInvalidations_AreNotLost()
    {
        var gen   = new HostReadGeneration();
        var token = gen.BeginRead();

        Parallel.For(0, 1000, _ => gen.Invalidate());

        Assert.False(gen.IsCurrent(token));
        // Every one of the 1000 landed: the generation is exactly 1000 past the token, which a non-atomic
        // increment would undershoot. Asserted as the count rather than just "not current" — a torn
        // read-modify-write still moves the counter, so "not current" alone would pass with increments
        // lost, and losing them is how two invalidations collapse into one and a stale read reads current.
        Assert.True(gen.IsCurrent(token + 1000));
    }
}
