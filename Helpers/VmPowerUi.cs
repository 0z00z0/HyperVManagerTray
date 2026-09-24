using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// What a failed VM power action says in a balloon, and whether the balloon is shown at all (issue
/// #69). Pure text and decisions, so the announcement and the "Start &amp; Connect" report are testable
/// without a tray icon or a Hyper-V host.
///
/// <para>The sentence comes from <see cref="VmFailureText"/> by way of
/// <see cref="VmOperationProgress.Detail"/>, so the balloon, the card's tooltip and the published
/// entity all say the same thing.</para>
/// </summary>
internal static class VmPowerUi
{
    /// <summary>
    /// A failed start is announced even while the dashboard is open: an automatic start may concern a
    /// machine with no card at all, and the card's own report is one short line. Other failed power
    /// actions keep deferring to the card while the dashboard is visible.
    /// </summary>
    internal static bool SuppressWhenDashboardVisible(VmOpKind kind) => kind != VmOpKind.Start;

    /// <summary>The balloon for any failed power action: the machine, what did not happen to it, and why.</summary>
    internal static string FailedMessage(string vmName, VmOpKind kind, string? detail) =>
        $"'{vmName}' {DidNot(kind)}. {ReasonSentence(detail)}";

    /// <summary>
    /// "Start &amp; Connect" giving up: both halves — the machine did not start, and its console was not
    /// opened — with the reason, since this report can replace the start balloon on screen.
    /// </summary>
    internal static string StartAndConnectAbandonedMessage(string vmName, string? detail) =>
        $"'{vmName}' did not start, so its console was not opened. {ReasonSentence(detail)}";

    /// <summary>What did not happen to the machine, as the balloon puts it.</summary>
    private static string DidNot(VmOpKind kind) => kind switch
    {
        VmOpKind.Start    => "did not start",
        VmOpKind.Resume   => "was not resumed",
        VmOpKind.Pause    => "was not paused",
        VmOpKind.Save     => "was not saved",
        VmOpKind.Shutdown => "was not shut down",
        _                 => "was not changed",
    };

    private static string ReasonSentence(string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? "What happened is recorded in vm-power.log."
            : detail.Trim();
}
