using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Guards the two MQTT call sites no other test can reach. <c>Services\MqttService.cs</c> composes a live
/// broker connection over <c>NetworkMonitor</c>, <c>VmService</c> and <c>HyperVManager</c>, and
/// <c>UI\SettingsWindow.xaml.cs</c> is a WinUI code-behind: neither is linkable here, so the pure files
/// they drive (<c>MqttPublishGate</c>, <c>MqttWithdrawal</c>) would go on passing untouched if the shell
/// stopped calling them. That is the vacuous green <see cref="VmConnectFlowSourceTests"/> was written
/// against, and these are the same coarse instrument for the same reason: aimed at the shortcut
/// reappearing during an unrelated edit, not at an adversary. If one fails, answer its question — do not
/// delete it.
/// </summary>
public class MqttServiceSourceTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    /// <summary>Source with comments stripped: these tests assert what the code DOES, and the prose in
    /// these two files names the very calls being counted.</summary>
    private static string Source(params string[] parts)
    {
        var path = Path.Combine([RepoRoot(), .. parts]);
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        return Regex.Replace(File.ReadAllText(path), @"//[^\n]*", "");
    }

    private static string ServiceSource()  => Source("Services", "MqttService.cs");
    private static string SettingsSource() => Source("UI", "SettingsWindow.xaml.cs");

    /// <summary>One method's body, from its signature to the next member at class indent.</summary>
    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found — fix this test, don't skip it.");

        int end = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of '{signature}' — fix this test, don't skip it.");

        return source[start..end];
    }

    // ── Defect 1: the withdrawal ────────────────────────────────────────────────

    /// <summary>
    /// The connection's removal, never the publisher's. Both are called <c>RemoveDeviceAsync</c> and both
    /// compile here, which is what makes this worth asserting: <c>DiscoveryPublisher</c>'s form withdraws
    /// the discovery document and leaves the connection live and enabled, so the next announcement — the
    /// birth message, a reconnect, a group toggle — recreates the device that was just deleted. The
    /// connection's form empties the command topics and both availability topics with it, and ends
    /// disabled.
    /// </summary>
    [Fact]
    public void Withdrawal_UsesTheConnectionsRemoval()
    {
        var src = ServiceSource();

        Assert.Matches(new Regex(@"connection\.RemoveDeviceAsync\s*\("), src);
        Assert.DoesNotContain("_publisher.RemoveDeviceAsync", src);
    }

    /// <summary>
    /// Withdraw first, apply second. The apply for a switched-off configuration disconnects the client,
    /// and <c>MqttConnection.RemoveDeviceAsync</c> returns false with nothing removed when there is no
    /// live link — so the reversed order is a silent no-op that leaves the device standing exactly as
    /// before the fix.
    /// </summary>
    [Fact]
    public void Withdrawal_HappensBeforeTheApplyThatDisconnects()
    {
        var body = Body(ServiceSource(), "private async Task ReconcileAsync(bool withdraw)");

        int removal = body.IndexOf("RemoveDeviceAsync", StringComparison.Ordinal);
        int apply   = body.IndexOf("ApplyAsync", StringComparison.Ordinal);

        Assert.True(removal >= 0, "The reconcile no longer removes the device.");
        Assert.True(apply   >= 0, "The reconcile no longer applies the new settings.");
        Assert.True(removal < apply,
            "The apply runs before the removal. An apply for a switched-off configuration disconnects, "
          + "and the removal then finds no live link and silently removes nothing.");
    }

    /// <summary>
    /// The reconcile awaits the apply and holds the gate across both halves. <c>MqttConnection.Apply</c>
    /// discards its own task, so the fire-and-forget form returns before the apply has done anything —
    /// and one toggle flip raises the settings change twice, on the UI thread and on the pool. An apply
    /// landing part-way through a removal publishes offline over the availability topic the removal has
    /// just emptied, which puts the device back as the permanently-offline ghost being removed.
    /// </summary>
    [Fact]
    public void Withdrawal_AndTheApplyAreSerialisedAgainstEachOther()
    {
        var body = Body(ServiceSource(), "private async Task ReconcileAsync(bool withdraw)");

        Assert.Matches(new Regex(@"await\s+_reconcile\.WaitAsync\(\)"), body);
        Assert.Matches(new Regex(@"await\s+connection\.ApplyAsync\("), body);
        Assert.DoesNotMatch(new Regex(@"[^c]Apply\(\)"), body);   // never the fire-and-forget form
    }

    /// <summary>The transition is decided by the tested rule, not re-derived inline. Reading anything
    /// wider than <c>Enabled</c> — "should the connection still run", say — makes a blanked host field
    /// delete the receiving end's registry entry.</summary>
    [Fact]
    public void Withdrawal_DecidesTheTransitionThroughMqttWithdrawal()
    {
        Assert.Matches(new Regex(@"MqttWithdrawal\.OnDisable\s*\("), ServiceSource());
    }

    /// <summary>The Settings control asks before it removes. The confirmation, the wording and the report
    /// are all <c>MqttWithdrawal</c>'s; a hand-rolled <c>Confirm</c> plus a direct call would satisfy the
    /// compiler and lose every rule tested against them.</summary>
    [Fact]
    public void SettingsRoutesTheRemovalThroughMqttWithdrawal()
    {
        var src = SettingsSource();

        Assert.Matches(new Regex(@"MqttWithdrawal\.RunAsync\s*\("), src);
        Assert.Contains("WithdrawDeviceAsync", src);   // the flow is given the real removal
    }

    // ── Defect 2: the initial push ──────────────────────────────────────────────

    /// <summary>
    /// Every app event goes through the gate. <c>MqttConnection.RequestPublish</c> checks only that the
    /// feature is enabled, not that the socket is up, so a direct call from a state handler is the fifty
    /// failed publishes at launch — and the handlers are where a "just signal it" line naturally goes
    /// back. The single permitted mention is the gate's own callback.
    /// </summary>
    [Fact]
    public void StateChanges_ReachTheConnectionOnlyThroughTheGate()
    {
        var src = ServiceSource();
        int calls = Regex.Matches(src, @"RequestPublish\s*\(").Count;

        Assert.True(calls == 1,
            $"MqttService calls RequestPublish {calls} times; exactly one — the MqttPublishGate callback — "
          + "may exist. A state handler calling it directly publishes into a client that is still "
          + "connecting, which is 50 Error lines in mqtt.log at every launch.");
        Assert.Matches(new Regex(@"new MqttPublishGate\(\s*\(\)\s*=>\s*_connection\?\.RequestPublish\(\)"), src);
    }

    /// <summary>The other half: a held change has to be released, or the gate turns the noise into a
    /// stranded value. The connection's own state event is what releases it.</summary>
    [Fact]
    public void TheGateIsReleasedByTheConnectionState()
    {
        var body = Body(ServiceSource(), "private void OnConnectionState(MqttConnectionState state)");

        Assert.Matches(new Regex(@"_publish\.OnState\s*\(\s*state\s*\)"), body);
    }

    /// <summary>And the signal side is actually wired: without this the two tests above pass trivially on
    /// a service that never publishes a state change at all.</summary>
    [Fact]
    public void StateChanges_AreSignalledWithTheConnectionsOwnLiveness()
    {
        var body = Body(ServiceSource(), "private void SignalPublish()");

        Assert.Matches(new Regex(@"_publish\.Signal\(_connection\?\.IsConnected \?\? false\)"), body);
    }

    // ── Defect 3: resume from standby (issue #83) ───────────────────────────────
    //
    // Start() and BeginDisposeAsync() are not usable with Body() here: it locates a method's end by
    // finding the next "private" member, and Start() is followed by public properties before the next
    // private one, while BeginDisposeAsync() is the last private member in the class (only the public
    // Dispose() follows) — so these three assert directly against the whole source, the same way the
    // withdrawal-routing and gate tests above do.

    /// <summary><c>MqttConnection.OnPowerResume</c> documents that it does not subscribe to system events
    /// itself — the host must. Without this subscription the device lingers unavailable until the
    /// 60-second <c>ConnectedPoll</c> notices the link died across the suspend.</summary>
    [Fact]
    public void Start_SubscribesToPowerModeChanged()
    {
        Assert.Matches(new Regex(@"SystemEvents\.PowerModeChanged\s*\+=\s*OnPowerModeChanged\s*;"), ServiceSource());
    }

    /// <summary>Every other <see cref="Microsoft.Win32.PowerModes"/> value — Suspend, StatusChange — is
    /// not a dead link, and calling <c>OnPowerResume</c> on those would force a reconnect for nothing.</summary>
    [Fact]
    public void PowerModeHandler_ActsOnResumeOnly()
    {
        var src = ServiceSource();

        Assert.Matches(new Regex(@"if\s*\(\s*e\.Mode\s*==\s*PowerModes\.Resume\s*\)\s*_connection\?\.OnPowerResume\s*\(\s*\)\s*;"), src);
    }

    /// <summary>A handler left attached keeps <c>MqttService</c> alive for the process's life —
    /// <c>SystemEvents</c> holds a static event, so the unsubscribe has to sit alongside the other five.</summary>
    [Fact]
    public void Dispose_UnsubscribesFromPowerModeChanged()
    {
        Assert.Matches(new Regex(@"SystemEvents\.PowerModeChanged\s*-=\s*OnPowerModeChanged\s*;"), ServiceSource());
    }
}
