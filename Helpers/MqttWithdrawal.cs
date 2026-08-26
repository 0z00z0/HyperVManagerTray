using ZeroZero.Mqtt;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Taking this host's published device off the broker (issue #75): when it happens by itself, what it is
/// confirmed with, and what its outcome reads as. Pure — the removal arrives as a delegate — so the rules
/// below are testable without a broker or a WinUI host, the same reason <see cref="VmConnectFlow"/> and
/// <see cref="FailureAnnouncer"/> are.
///
/// <para><b>Why the app needs this at all.</b> Switching publishing off stops the connection and leaves
/// the retained discovery document standing, so the device reads as permanently offline at whatever
/// consumes it and nothing in the app could ever clear it — an external MQTT client was the only way out.
/// The module's removal is deliberately separate and unreachable from the settings, so the application
/// has to reach it: automatically when publishing is switched off, and on demand from Settings.</para>
///
/// <para><b>Switched off is not the same as incomplete.</b> Blanking the broker host also stops the
/// connection, and it is one keystroke in a field being edited. Only the explicit switch withdraws — see
/// <see cref="OnDisable"/> — because an incomplete configuration is a configuration in progress, not an
/// instruction to delete everything the receiving end holds.</para>
/// </summary>
public static class MqttWithdrawal
{
    /// <summary>What an attempted removal did.</summary>
    public enum Outcome
    {
        /// <summary>The device is off the broker: the document, both availability topics and every value
        /// and command topic are emptied.</summary>
        Removed,

        /// <summary>There was no live link to do it over, so nothing was removed and the device still
        /// stands. Not a failure of the removal — it never started.</summary>
        NoConnection,

        /// <summary>The removal was attempted and threw. What reached the broker is unknown.</summary>
        Failed,
    }

    /// <summary>
    /// Whether moving from <paramref name="before"/> to <paramref name="after"/> is publishing being
    /// switched off — the one settings transition that withdraws the device.
    ///
    /// <para>Read <c>Enabled</c> alone, never whether the connection should still run: a host blanked
    /// mid-edit stops the connection too, and treating that as a withdrawal deletes the receiving end's
    /// names, entity ids and areas over a keystroke.</para>
    /// </summary>
    public static bool OnDisable(MqttSettings before, MqttSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return before.Enabled && !after.Enabled;
    }

    /// <summary>The blocking question the explicit control asks before removing anything. States what is
    /// emptied, what the receiving end loses, and that neither comes back.</summary>
    public const string ConfirmPrompt =
        "Remove this host's published device from the broker?\n\n"
        + "The discovery document, both availability topics and every published value and command topic "
        + "are emptied, and publishing is switched off so nothing re-announces it.\n\n"
        + "Whatever consumes the device loses every name, entity id and area chosen for its entities. "
        + "Announcing it again creates them afresh, and the old ids cannot be recovered.\n\n"
        + "This cannot be undone.";

    /// <summary>The outcome as the user is told it, and whether it is a failure (which decides the
    /// channel — see docs\DISPLAY-VOCABULARY.md). States only what was verified: a removal that never
    /// started says the device is still published rather than claiming anything about it.</summary>
    public static (string Message, bool IsFailure) Report(Outcome outcome) => outcome switch
    {
        Outcome.Removed => (
            "The published device has been removed from the broker, and publishing is now off.",
            false),
        Outcome.NoConnection => (
            "There is no live connection to the broker, so nothing was removed and the device is still "
            + "published. Switch publishing on, wait for it to connect, then try again.",
            true),
        _ => (
            "The published device could not be removed, and what reached the broker is not known. See "
            + "mqtt.log under Maintenance for why.",
            true),
    };

    /// <summary>
    /// The explicit control's sequence: ask, remove, report. Returns the outcome, or null when the user
    /// declined — in which case <paramref name="withdraw"/> is never called and nothing is reported.
    /// </summary>
    /// <param name="confirm">Shows <see cref="ConfirmPrompt"/> and returns whether the user agreed.</param>
    /// <param name="withdraw">Performs the removal.</param>
    /// <param name="report">Shows the message, with the failure flag from <see cref="Report"/>.</param>
    public static async Task<Outcome?> RunAsync(
        Func<string, bool>   confirm,
        Func<Task<Outcome>>  withdraw,
        Action<string, bool> report)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(withdraw);
        ArgumentNullException.ThrowIfNull(report);

        if (!confirm(ConfirmPrompt)) return null;

        var outcome = await withdraw();

        // Always reported, including the two that changed nothing: a destructive command answering with
        // silence is indistinguishable from one that worked (docs\DISPLAY-VOCABULARY.md, corollary 2).
        var (message, isFailure) = Report(outcome);
        report(message, isFailure);
        return outcome;
    }
}
