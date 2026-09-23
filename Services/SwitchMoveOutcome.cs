namespace HyperVManagerTray.Services;

/// <summary>
/// Outcome of a <see cref="HyperVManager.ApplySwitchAsync"/> attempt.
///
/// <para><b>Why three names and not a bool.</b> The bool this replaced answered one question — is the
/// adapter on the requested switch — and both of its true cases arrived spelled the same way. That is
/// enough for the caller that only reports failures, and not enough for anything that must act on the
/// change itself: asking a guest for a new address is right after a real move and wrong after a no-op,
/// where re-cycling the link of a machine that never left its switch would drop a working network for
/// no reason on every evaluation. <see cref="Helpers.GuestAddressRules.ShouldRenewAfter"/> holds that
/// rule, and it needs <see cref="Moved"/> and <see cref="AlreadyThere"/> told apart to hold it.</para>
///
/// <para>Its own file, alongside <see cref="SwitchBindOutcome"/> and for the same reason: pure
/// vocabulary, so the rule that reads it can be linked into the test assembly without dragging
/// <see cref="HyperVManager"/> and <c>System.Management</c> with it.</para>
/// </summary>
public enum SwitchMoveOutcome
{
    /// <summary>The adapter was connected somewhere else (or nowhere) and is now on the requested
    /// switch — the network under the guest has genuinely changed.</summary>
    Moved,

    /// <summary>The adapter was already on the requested switch, so nothing was touched. A success:
    /// the caller asked for a state and the state holds.</summary>
    AlreadyThere,

    /// <summary>The reconnect could not be performed (switch, VM or adapter not found, or the modify
    /// failed) and the adapter is NOT on the requested switch.</summary>
    Failed,
}
