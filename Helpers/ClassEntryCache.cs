namespace HyperVManagerTray.Helpers;

/// <summary>
/// Holds the network Class key's <c>NetCfgInstanceId</c> → <c>DeviceInstanceID</c> entries across reads,
/// so resolving an adapter's PnP device costs a whole-Class-key walk only when the answer is not already
/// in hand (issue #50).
///
/// <para><b>The cost this exists to remove.</b>
/// <see cref="AdapterDeviceRegistry.ReadClassAdapterEntries"/> enumerates every subkey of the network
/// Class key, which is why its own doc says callers "must call it ONCE and reuse the result". Three
/// call sites honoured that within a single enumeration and re-walked between them:
/// <c>AdapterMatcher.BuildResult</c> ran the walk on EVERY rule evaluation — i.e. on every
/// <c>NetworkChange</c> event — to obtain <c>HostAdapterName</c>, a display-only string; and the rename
/// flow walked it twice more per click. "Once per enumeration" was never the real bound: the mapping is
/// the same mapping every time, and nothing expressed that.</para>
///
/// <para><b>Why caching this mapping is safe, and caching a name would not be.</b> This caches ONLY the
/// GUID → device-instance mapping, which is a property of the device's installation: it is assigned when
/// the driver binds the adapter and does not change while that installation lives. It is emphatically
/// NOT the adapter's display name. The <c>FriendlyName</c> — the string this app's own rename writes,
/// and the string issue #32 was about showing wrongly — is deliberately NOT cached here: every caller
/// still reads it fresh through <see cref="AdapterDeviceRegistry.ReadFriendlyName"/>, which is a direct
/// open of one known Enum key rather than a walk. So a rename is visible on the very next read, and this
/// cache can never make a stale name appear. That split is the whole design; do not extend this cache to
/// hold names.</para>
///
/// <para><b>Self-healing, so no invalidation call site can be forgotten.</b> A device plugged in after
/// the cache warmed is simply absent from it, and an absent (or, after a re-install, ambiguous) entry set
/// cannot answer — so <see cref="Resolve"/> re-walks ONCE and retries on any non-success. That bounds the
/// worst case at exactly today's behaviour (one walk per resolve, for an adapter that has no Class entry
/// at all) while the normal case costs none. Nothing has to remember to invalidate this; a removed
/// device's entry is never asked about again, because only a currently-enumerated NIC's GUID is ever
/// looked up.</para>
///
/// <para><b>Threading.</b> Production shares one instance across the rule-evaluation thread, the Settings
/// background read and the rename flow, so every access is under the lock. The walk itself happens inside
/// it: walks are rare now, and a torn read of the entry list would be a wrong-device resolution.</para>
///
/// <para><b>Testability.</b> The reader arrives as a delegate — the same reason
/// <see cref="VmConnectFlow"/> takes its side effects as delegates — so the re-walk rule below is
/// unit-testable without a registry, and <see cref="Walks"/> lets a test assert the bound rather than
/// merely the answer.</para>
/// </summary>
public sealed class ClassEntryCache
{
    private readonly Func<List<AdapterNameRules.ClassAdapterEntry>> _readEntries;
    private readonly object _lock = new();
    private List<AdapterNameRules.ClassAdapterEntry>? _entries;

    /// <summary>How many times the whole Class key has actually been walked. The point of this class is
    /// that this number stops tracking the number of resolves; the tests assert exactly that.</summary>
    public int Walks { get; private set; }

    public ClassEntryCache(Func<List<AdapterNameRules.ClassAdapterEntry>> readEntries)
        => _readEntries = readEntries ?? throw new ArgumentNullException(nameof(readEntries));

    /// <summary>
    /// Resolves <paramref name="interfaceGuid"/> to its PnP device instance, walking the Class key only
    /// when the cached entries cannot answer.
    ///
    /// <para>A cold cache walks once and answers from that walk — it is already as fresh as a walk can
    /// make it, so a miss against it is a real miss (an adapter with no Class entry) and must NOT trigger
    /// a second walk. Only a WARM cache that fails to answer is evidence of the device set having changed
    /// underneath it, and only that re-walks. An unreadable Class key degrades to "no entries", which
    /// resolves to a failure the caller falls back from — never an exception.</para>
    /// </summary>
    public AdapterNameRules.DeviceResolution Resolve(string interfaceGuid)
    {
        lock (_lock)
        {
            bool justWalked = false;
            if (_entries is null)
            {
                _entries   = Walk();
                justWalked = true;
            }

            var resolution = AdapterNameRules.ResolveDeviceInstanceId(interfaceGuid, _entries);

            // Success, or a miss against entries we have this instant — either way another walk would
            // read the same key and reach the same answer.
            if (resolution.Success || justWalked) return resolution;

            // A warm cache could not answer: the device set has changed since it was built (a dock
            // plugged in, an adapter re-installed). Re-walk once and let the fresh entries decide.
            _entries = Walk();
            return AdapterNameRules.ResolveDeviceInstanceId(interfaceGuid, _entries);
        }
    }

    /// <summary>Drops the cached entries so the next <see cref="Resolve"/> walks again. Not needed for a
    /// rename (which changes no mapping — see the class remarks); present for a caller that knows the
    /// device set changed and would rather pay the walk up front than through a miss.</summary>
    public void Invalidate()
    {
        lock (_lock) _entries = null;
    }

    private List<AdapterNameRules.ClassAdapterEntry> Walk()
    {
        Walks++;
        try   { return _readEntries(); }
        catch { return []; }   // unreadable key ⇒ no entries ⇒ every Resolve falls back. Never throws.
    }
}
