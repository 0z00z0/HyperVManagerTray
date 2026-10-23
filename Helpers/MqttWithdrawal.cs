using ZeroZero.Mqtt;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Taking this host's published device off the broker when publishing is switched off (issue #75).
///
/// <para><b>Why the app needs this at all.</b> Switching publishing off stops the connection and leaves
/// the retained discovery document standing, so the device reads as permanently offline at whatever
/// consumes it. The module removes only on request, from its panel.</para>
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
}
