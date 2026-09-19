using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// What a failed VM start says, and where it is shown (issue #69). Pure text and decisions, so the
/// balloon and the "Start &amp; Connect" report are testable without a tray icon or a Hyper-V host.
/// </summary>
internal static class VmPowerUi
{
    /// <summary>What <see cref="WmiVmMapper.ProgressMessage"/> puts in front of Hyper-V's own reason.</summary>
    private const string FailedPrefix = "Failed: ";

    /// <summary>
    /// A failed start is announced even while the dashboard is open: the card's inline report is cut to
    /// a few words and fades after 45 s, and an automatic start may concern a VM with no card at all.
    /// Other failed power actions keep deferring to the card while the dashboard is visible.
    /// </summary>
    internal static bool SuppressWhenDashboardVisible(VmOpKind kind) => kind != VmOpKind.Start;

    /// <summary>Hyper-V's reason for a failure, taken from the progress message, or null when it gave none.</summary>
    internal static string? Reason(string? progressMessage)
    {
        if (string.IsNullOrWhiteSpace(progressMessage)) return null;
        var text = progressMessage.Trim();
        if (!text.StartsWith(FailedPrefix, StringComparison.Ordinal)) return null;
        var reason = text[FailedPrefix.Length..].Trim();
        return reason.Length == 0 ? null : reason;
    }

    /// <summary>The balloon for a failed start: the VM, that it did not start, and why.</summary>
    internal static string StartFailedMessage(string vmName, string? progressMessage) =>
        $"'{vmName}' did not start. {ReasonSentence(progressMessage)}";

    /// <summary>
    /// "Start &amp; Connect" giving up: both halves — the VM did not start, and its console was not
    /// opened — with the reason, since this report can replace the start balloon on screen.
    /// </summary>
    internal static string StartAndConnectAbandonedMessage(string vmName, string? progressMessage) =>
        $"'{vmName}' did not start, so its console was not opened. {ReasonSentence(progressMessage)}";

    private static string ReasonSentence(string? progressMessage) =>
        Reason(progressMessage) is { } reason
            ? $"Hyper-V reported: {reason}"
            : "Hyper-V gave no reason; see vm-power.log.";
}
