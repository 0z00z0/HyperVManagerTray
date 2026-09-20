namespace HyperVManagerTray.Helpers;

/// <summary>
/// What a virtual switch's way out to the rest of the network is doing, as far as the app has been
/// able to establish it.
///
/// <para><b>Why this is an enum and not a bool.</b> The reading it replaces was a set of switch IDs
/// whose uplink was down, which can express only "down" and "not down" — and a failed adapter query
/// produced an empty set, so "we could not look" arrived at every status surface spelled exactly like
/// "everything is fine". That is the overclaim issue #37 removed from the apply path, and a tray icon
/// driven from the old shape would have gone green on a host nobody managed to read. Each member below
/// is a different state of knowledge, and only <see cref="Connected"/> means the traffic has somewhere
/// to go.</para>
/// </summary>
public enum SwitchUplinkState
{
    /// <summary>Nothing has been established: the host's adapters could not be listed, or no reading
    /// has been taken yet. The default (0), deliberately — an unstamped verdict must claim nothing.</summary>
    Unknown,

    /// <summary>The switch has no external port, so it has no uplink to lose. An internal or private
    /// switch is like this by design and must never be branded broken for it.</summary>
    NotExternal,

    /// <summary>The adapter the switch's external port sits on reports a connected medium. The only
    /// member that means traffic can leave.</summary>
    Connected,

    /// <summary>The switch is external but has no way out: the adapter it is bound to reports no
    /// connected medium, or its port no longer resolves to an adapter at all. A VM on this switch
    /// reads "Media disconnected" in the guest.</summary>
    NoUplink,
}

/// <summary>
/// The decision behind <see cref="SwitchUplinkState"/>, pure and separate from the WMI traversal that
/// feeds it (<c>VmService.ReadSwitchUplinks</c>), so each answer can be tested without a Hyper-V host
/// or a dock to unplug — which is the only way this particular decision can be tested at all, since
/// the states it distinguishes are reached by pulling a cable out of a running machine.
/// </summary>
public static class SwitchUplinkRules
{
    /// <summary>
    /// The verdict for one switch.
    ///
    /// <para><paramref name="connectedMacs"/> is the set of normalised hardware addresses of the
    /// physical adapters reporting a connected medium, or <b>null</b> when that list could not be read.
    /// Null dominates everything: with no list there is no comparand, and answering
    /// <see cref="SwitchUplinkState.NoUplink"/> from a failed query would mark every switch on the host
    /// broken because one WMI call did not answer.</para>
    ///
    /// <para><paramref name="portMac"/> is the switch's external port's permanent hardware address,
    /// already normalised. Empty means the port no longer resolves to an adapter — the switch is
    /// external and bound to nothing, which is a missing uplink and not an unknown one: the app looked
    /// and found no adapter behind the port.</para>
    /// </summary>
    public static SwitchUplinkState For(bool hasExternalPort, string? portMac, IReadOnlySet<string>? connectedMacs)
    {
        if (connectedMacs is null) return SwitchUplinkState.Unknown;
        if (!hasExternalPort)      return SwitchUplinkState.NotExternal;
        if (string.IsNullOrEmpty(portMac)) return SwitchUplinkState.NoUplink;
        return connectedMacs.Contains(portMac) ? SwitchUplinkState.Connected : SwitchUplinkState.NoUplink;
    }

    /// <summary>
    /// True only when the app has ESTABLISHED that this switch has no way out — the one verdict any
    /// surface may report as a problem.
    ///
    /// <para>Asked through this method rather than compared inline at each surface, for the reason
    /// <c>NetworkStatusUi.IsFailure</c> exists: a call site that writes
    /// <c>state != SwitchUplinkState.Connected</c> quietly folds <see cref="SwitchUplinkState.Unknown"/>
    /// and <see cref="SwitchUplinkState.NotExternal"/> into the warning, and the app starts announcing a
    /// broken bridge on an internal switch and on every host it could not read.</para>
    /// </summary>
    public static bool HasNoConnectionOut(SwitchUplinkState state) =>
        state == SwitchUplinkState.NoUplink;
}
