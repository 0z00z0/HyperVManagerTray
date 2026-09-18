using HyperVManagerTray.Services;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// The manual override's order of work and the rule for how long it holds. Pure: every side effect
/// arrives as a delegate, so the one ordering that reaches a person — a VM is never moved onto a switch
/// that could not be bound — is testable without a live Hyper-V host.
///
/// <para>An override onto a switch a rule names (a bridged switch) first binds that switch to the adapter
/// the computer is connected through now, wired or Wi-Fi, and only then moves the VM. Binding drops the
/// host's network for a moment, so a person is asked first; a remote command passes no question.</para>
/// </summary>
public static class OverrideFlow
{
    /// <summary>What an override did.</summary>
    public enum Outcome
    {
        /// <summary>The VM is on the switch, and a bridged switch is bound to the current adapter.</summary>
        Applied,
        /// <summary>The VM is not a managed VM; nothing was attempted.</summary>
        NotConfigured,
        /// <summary>A bridged switch needed an adapter and none is connected; nothing was changed.</summary>
        NoAdapter,
        /// <summary>The person declined the network drop; nothing was changed.</summary>
        Declined,
        /// <summary>Another network pass held the lock too long; nothing was changed.</summary>
        Busy,
        /// <summary>The switch could not be bound; the VM was left where it was.</summary>
        BindFailed,
        /// <summary>The switch is fine (bound, or no bind needed) but the VM could not be moved onto it.</summary>
        MoveFailed,
    }

    /// <summary>
    /// Runs the override: ask (only when a bind will drop the network), take the network lock, bind, move.
    /// A failed bind returns before the move, so the VM keeps the network it had.
    /// </summary>
    /// <param name="needsBind">True when the target switch is one a rule names.</param>
    /// <param name="adapterInterfaceId">The connected adapter's interface identifier, or null/empty.</param>
    /// <param name="confirmDrop">Asks the person whether the network may drop; true to go ahead.</param>
    /// <param name="acquire">Takes the network lock; false when it could not be had in time.</param>
    /// <param name="bind">Binds the switch to the adapter.</param>
    /// <param name="move">Moves the VM's network adapter onto the switch; true when confirmed there.</param>
    public static async Task<Outcome> RunAsync(
        bool needsBind, string? adapterInterfaceId,
        Func<bool> confirmDrop, Func<Task<bool>> acquire,
        Func<Task<SwitchBindOutcome>> bind, Func<Task<bool>> move)
    {
        if (needsBind)
        {
            if (string.IsNullOrWhiteSpace(adapterInterfaceId)) return Outcome.NoAdapter;
            if (!confirmDrop()) return Outcome.Declined;
        }

        if (!await acquire().ConfigureAwait(true)) return Outcome.Busy;

        if (needsBind && await bind().ConfigureAwait(true) == SwitchBindOutcome.Failed)
            return Outcome.BindFailed;

        return await move().ConfigureAwait(true) ? Outcome.Applied : Outcome.MoveFailed;
    }

    /// <summary>How long a hold ignores network changes after its own bind, which drops and restores the
    /// host's connection and so fires the very events that would otherwise end it at once.</summary>
    public static readonly TimeSpan SettleAfterBind = TimeSpan.FromSeconds(60);

    /// <summary>What an evaluation during an override hold means for the hold.</summary>
    public enum HoldVerdict
    {
        /// <summary>Still inside the settle window: keep the override, record nothing yet.</summary>
        Settling,
        /// <summary>First look after settling: this network is the one the override belongs to.</summary>
        Capture,
        /// <summary>Same network as captured: keep the override.</summary>
        Holds,
        /// <summary>A different network: the override ends and the rules decide.</summary>
        Ends,
    }

    /// <summary>The network an evaluation describes: the matched rule, the adapter and its gateway.</summary>
    public static string Fingerprint(MatchResult evaluated) =>
        string.Join("|", evaluated.RuleId, evaluated.HostAdapterInterfaceId, evaluated.Gateway)
              .ToUpperInvariant();

    /// <summary>Decides whether an override still holds after a network change.</summary>
    public static HoldVerdict DecideHold(DateTime nowUtc, DateTime settleUntilUtc, string? heldFingerprint, string currentFingerprint)
    {
        if (nowUtc < settleUntilUtc) return HoldVerdict.Settling;
        if (heldFingerprint is null) return HoldVerdict.Capture;
        return string.Equals(heldFingerprint, currentFingerprint, StringComparison.Ordinal)
            ? HoldVerdict.Holds
            : HoldVerdict.Ends;
    }
}
