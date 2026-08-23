using HyperVManagerTray.Models;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Helpers;

/// <summary>The four publish categories as one comparable value.</summary>
internal readonly record struct PublishCategories(
    bool Network, bool VmState, bool VmDiagnostics, bool Metrics)
{
    public static PublishCategories Of(MqttSettings settings) => new(
        settings.PublishNetwork, settings.PublishVmState,
        settings.PublishVmDiagnostics, settings.PublishVmMetrics);
}

/// <summary>
/// The decisions <c>MqttService</c> takes at its call sites (issue #75). Lifted out because that
/// service holds the live broker connection and is deliberately not linked into the test assembly —
/// which left every guard below unasserted while it lived inline.
/// </summary>
internal static class MqttReconcile
{
    /// <summary>Whether a scheduled reconcile has been overtaken by a later one and must stand down.
    /// Each carries its own config snapshot, so an older one landing last would apply stale settings.</summary>
    public static bool Superseded(long ticket, long latest) => ticket != latest;

    /// <summary>Whether the abandoned identity's retained topics can be cleared right now. Each term is
    /// a reason not to try: nothing built yet, no live session, or an address that has not moved.</summary>
    public static bool CanClear(bool hasNode, bool hasConnection, bool isConnected, bool hasTopics,
                                MqttIdentity? published, MqttIdentity next) =>
        hasNode && hasConnection && isConnected && hasTopics
        && MqttIdentity.Abandons(published, next);

    /// <summary>Whether the node and connection must be rebuilt. Both halves of the identity are fixed
    /// at construction, so a move to either needs a fresh pair.</summary>
    public static bool NeedsRecreate(bool hasConnection, string appliedIdentity, string nextIdentity) =>
        !hasConnection || !string.Equals(appliedIdentity, nextIdentity, StringComparison.Ordinal);

    /// <summary>Whether the entity set must be republished on the connection already up: which entities
    /// are announced turns on the VM list, the switch-override options and the publish categories.</summary>
    public static bool NeedsEntityRebuild(
        IReadOnlyList<string> appliedVms, IReadOnlyList<string> nextVms,
        IReadOnlyList<string> appliedSwitches, IReadOnlyList<string> nextSwitches,
        PublishCategories applied, PublishCategories next) =>
        !nextVms.SequenceEqual(appliedVms, StringComparer.Ordinal)
        || !nextSwitches.SequenceEqual(appliedSwitches, StringComparer.Ordinal)
        || applied != next;

    /// <summary>Whether the connection has to be re-applied. A remembered endpoint moving is not a
    /// reason: <c>Apply</c> drops the live session to rebuild it, and the endpoint is written back BY
    /// that session, so re-applying for it would reconnect once per successful connect.</summary>
    public static bool NeedsApply(MqttOptions? applied, string appliedPassword,
                                  MqttOptions next, string? nextPassword) =>
        applied is not { } previous
        || previous with { LastGoodEndpoint = null } != next with { LastGoodEndpoint = null }
        || !string.Equals(appliedPassword, nextPassword ?? string.Empty, StringComparison.Ordinal);
}
