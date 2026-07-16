namespace HyperVManagerTray.Helpers;

/// <summary>
/// Decides whether a host read that has already been taken still describes the host — the one question
/// behind every "may I reuse this list?" in the Settings window.
///
/// <para><b>Why a counter and not a bool.</b> A <c>bool _stale</c> can only record THAT something
/// changed, never WHEN relative to a read that is still in flight — so whichever read lands last clears
/// it, including one that started before the change and therefore knows nothing about it. That is not a
/// hypothetical: <c>SettingsWindow</c> has two independent callers of its host read (a rename, and every
/// rebuild the config editors trigger) with no sequencing between them, so two reads are routinely
/// overlapping. A monotonically increasing generation gives every read a token stamped at its START, and
/// <see cref="IsCurrent"/> compares that token against the generation NOW — so a read is trusted only
/// when nothing has invalidated the host between its start and its landing. Late-arriving stale reads
/// answer "no" no matter what order they complete in.</para>
///
/// <para><b>What counts as an invalidation.</b> Anything that may have changed the host's adapter set or
/// their display names, whether this app caused it (a FriendlyName write; a device disable/enable) or the
/// host did (a <c>NetworkChange</c> — a dock swapped, an adapter up or down). The second category is the
/// load-bearing one and the easiest to forget: an app-caused-only signal makes a list that went stale on
/// its own read as trustworthy, which is exactly how the rename dialog's uniqueness check ends up
/// comparing a new name against adapters that are no longer on the host and lets two of them take one
/// name (investigation §5.5, issue #32).</para>
///
/// <para><b>Err toward stale.</b> An unnecessary invalidation costs one live enumeration on a path the
/// user reaches a handful of times; a missed one is a wrong answer to a device-mutating question. So
/// <see cref="Invalidate"/> is cheap enough (one interlocked increment) to call on every suspicion, and
/// callers are expected to.</para>
///
/// <para><b>Threading.</b> Free-threaded by construction: <see cref="Invalidate"/> arrives on
/// <c>NetworkChange</c>'s thread-pool thread while <see cref="IsCurrent"/> is read on the UI thread and
/// <see cref="BeginRead"/> on either. The counter is the whole state, so an interlocked increment and a
/// volatile read are sufficient — there is nothing here for a lock to keep consistent.</para>
/// </summary>
public sealed class HostReadGeneration
{
    private int _generation;

    /// <summary>The generation a read starting NOW belongs to. Hand the returned token back to
    /// <see cref="IsCurrent"/> when the read lands — never re-read the generation there, which is the
    /// bug this type exists to make unrepresentable.</summary>
    public int BeginRead() => Volatile.Read(ref _generation);

    /// <summary>Records that the host may have changed, so every read already in flight is now suspect.
    /// Cheap by design — see the class remarks on erring toward stale.</summary>
    public void Invalidate() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// True when nothing has invalidated the host since the read that produced <paramref name="token"/>
    /// began — i.e. when that read's result may still be treated as describing the host.
    ///
    /// <para>Also the test for a result that landed earlier: a snapshot published at generation N stops
    /// being current the moment the generation moves past N, without anyone having to go back and touch
    /// it. Holding the token beside the snapshot is how a list that was fresh when it arrived stops
    /// being trusted later.</para>
    /// </summary>
    public bool IsCurrent(int token) => token == Volatile.Read(ref _generation);
}
