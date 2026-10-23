using HyperVManagerTray.Helpers;
using Xunit;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Taking this host's published device off the broker (issue #75). Two things are being guarded, and the
/// second is the dangerous one: that the device goes when publishing is switched off, and that it goes at
/// no other time — the retained document is the receiving end's whole registry entry, and re-announcing
/// it never restores the names, entity ids and areas a user chose there.
/// </summary>
public class MqttWithdrawalTests
{
    private static MqttSettings Settings(bool enabled, string host = "broker.lan") =>
        new() { Enabled = enabled, Host = host };

    // ── When the device is withdrawn by itself ──────────────────────────────────

    /// <summary>The whole point of the automatic half: publishing switched off used to leave the retained
    /// document standing, and the device read offline at the receiving end for ever.</summary>
    [Fact]
    public void OnDisable_IsTrueWhenPublishingIsSwitchedOff() =>
        Assert.True(MqttWithdrawal.OnDisable(Settings(enabled: true), Settings(enabled: false)));

    [Theory]
    [InlineData(true,  true)]    // an unrelated write while publishing stays on
    [InlineData(false, false)]   // an unrelated write while publishing stays off
    [InlineData(false, true)]    // switched ON — the opposite transition
    public void OnDisable_IsFalseWhenPublishingDidNotGoOff(bool before, bool after) =>
        Assert.False(MqttWithdrawal.OnDisable(Settings(before), Settings(after)));

    /// <summary>
    /// The trap this rule exists for. Blanking the broker host also stops the connection, so a rule
    /// written over "should the connection still run" would fire here — and deleting the receiving end's
    /// registry entry over one keystroke in a field being edited is unrecoverable.
    /// </summary>
    [Fact]
    public void OnDisable_IsFalseWhenOnlyTheHostWasBlanked() =>
        Assert.False(MqttWithdrawal.OnDisable(
            Settings(enabled: true, host: "broker.lan"),
            Settings(enabled: true, host: "")));

    /// <summary>The same rule from the other side: switching off is the transition, whether or not the
    /// configuration was ever complete enough to connect with.</summary>
    [Fact]
    public void OnDisable_IsTrueWhenPublishingGoesOffWithNoHostSet() =>
        Assert.True(MqttWithdrawal.OnDisable(
            Settings(enabled: true, host: ""),
            Settings(enabled: false, host: "")));

    [Fact]
    public void OnDisable_RefusesNulls()
    {
        Assert.Throws<ArgumentNullException>(() => MqttWithdrawal.OnDisable(null!, Settings(false)));
        Assert.Throws<ArgumentNullException>(() => MqttWithdrawal.OnDisable(Settings(true), null!));
    }
}
