namespace HyperVManagerTray.Services;

/// <summary>
/// Outcome of a <see cref="HyperVManager.UpdateSwitchBindingAsync"/> attempt (issue #29, finding 1).
/// The distinction matters to the caller's skip-cache: a failed bind was previously indistinguishable
/// from an already-bound no-op (both returned <c>false</c>), so <see cref="NetworkMonitor"/> cached the
/// target adapter as bound and never retried. Only <see cref="Bound"/>/<see cref="AlreadyBound"/> mean
/// the switch is now on the requested adapter; <see cref="Failed"/> must leave the cache clear so the
/// next network change retries.
///
/// <para><b>Why this sits in its own file rather than nested in <see cref="HyperVManager"/> (issue
/// #37).</b> It is pure vocabulary — three names describing what happened — with no dependency on WMI
/// or anything else. It moved out so the pure, unit-tested
/// <see cref="Helpers.NetworkStatusUi.FromBindOutcome"/> can map it to a UI decision without dragging
/// <see cref="HyperVManager"/> (System.Management, live-host writes) into the test assembly, which
/// links only WMI-free sources by design. The mapping "a Failed bind can never render as success" is
/// the invariant issue #37 exists to enforce, so it must be testable.</para>
/// </summary>
public enum SwitchBindOutcome
{
    /// <summary>A real rebind was performed (external uplink re-pointed or added).</summary>
    Bound,

    /// <summary>The switch was already External + sharing + on this adapter — nothing changed.</summary>
    AlreadyBound,

    /// <summary>The bind could not be performed (adapter absent, switch/port not found, or an error).</summary>
    Failed,
}
