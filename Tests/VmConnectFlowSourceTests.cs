using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Guards the CALL SITE of issue #45, which no other test can reach.
///
/// <para><b>Why a source test, which is not otherwise done in this repo.</b> The bug was one discarded
/// <c>bool</c> inside <c>DashboardWindow.ConnectAsync</c> — a WinUI <c>Window</c> code-behind, which the
/// test project deliberately cannot instantiate (it links pure files individually and has no Windows App
/// SDK runtime; see the csproj). <see cref="VmConnectFlowTests"/> proves the extracted logic is right,
/// but it links <c>VmConnectFlow</c> directly and would keep passing untouched if someone deleted the
/// call to it and wrote <c>await _hyperV.ApplySwitchAsync(...);</c> back into the dashboard. That test
/// alone would therefore be exactly the vacuous green this issue is about: the app broken, the suite
/// silent. This asserts the wiring the other file assumes.</para>
///
/// <para>It is a coarse instrument — it reads text, so it cannot see semantics, and a determined rewrite
/// gets around it. It is aimed at the realistic regression (the four-line shortcut reappearing during an
/// unrelated edit), not at an adversary. If this fails, do not delete it: the question it asks is
/// "does the dashboard consume the bind outcome?", and the answer must be yes.</para>
/// </summary>
public class VmConnectFlowSourceTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    private static string DashboardSource()
    {
        // Located from THIS file's compile-time path, not the test assembly's bin directory: the source
        // isn't copied to the output, and a path relative to bin breaks on any config/TFM change.
        var path = Path.Combine(RepoRoot(), "UI", "DashboardWindow.xaml.cs");
        Assert.True(File.Exists(path), $"DashboardWindow.xaml.cs not found at '{path}' — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The dashboard must not call <c>ApplySwitchAsync</c> directly. It routes through
    /// <see cref="HyperVManagerTray.Helpers.VmConnectFlow"/>, which consumes the result; the ONE permitted
    /// mention is the lambda handed to the flow (<c>sw => _hyperV.ApplySwitchAsync(...)</c>), whose value
    /// is returned, not dropped.
    ///
    /// <para>The check is simply that the dashboard never <c>await</c>s it. The correct wiring hands the
    /// flow an un-awaited lambda (<c>sw => _hyperV.ApplySwitchAsync(...)</c>) and lets the flow await and
    /// consume it, so any <c>await</c> here means the dashboard is doing the call itself again — and the
    /// only reason to await it in code-behind is to drop the result on the floor.</para>
    ///
    /// <para><b>This test was itself wrong first, which is worth recording.</b> It originally anchored
    /// the pattern to a statement start (<c>^\s*await</c>). The real bug is
    /// <c>if (!string.IsNullOrEmpty(sw)) await _hyperV.ApplySwitchAsync(...);</c> — the <c>await</c> sits
    /// mid-line after the <c>if</c>, so the regex written specifically to catch this bug did not catch
    /// this bug. The mutation check found that; without it the test would have shipped green and useless.
    /// Hence the blunter rule below: no await, anywhere, no cleverness about position.</para>
    /// </summary>
    [Fact]
    public void ConnectAsync_DoesNotDiscardTheSwitchApplyResult()
    {
        var src = DashboardSource();

        var awaited = new Regex(@"await\s+_hyperV\.ApplySwitchAsync\s*\(");
        Assert.False(awaited.IsMatch(src),
            "DashboardWindow awaits ApplySwitchAsync as a statement, discarding the bool that says whether "
          + "the VM's adapter is actually on the switch (issues #37/#45). Route it through VmConnectFlow, "
          + "which consumes the result and reports a failed bind to the user.");
    }

    /// <summary>
    /// The positive half: the connect path is actually wired to the tested flow. Without this, the test
    /// above passes trivially if <c>ApplySwitchAsync</c> is simply not called at all — a "fix" that
    /// silently stops binding the VM to its switch, which would be a worse bug than the one being fixed
    /// and would not fail a single other test.
    /// </summary>
    [Fact]
    public void ConnectAsync_RoutesThroughVmConnectFlow()
    {
        var src = DashboardSource();
        Assert.Matches(new Regex(@"VmConnectFlow\.RunAsync\s*\("), src);
        Assert.Contains("_hyperV.ApplySwitchAsync", src);   // still binds — the flow is given the real call
    }

    /// <summary>
    /// <c>ConnectAsync</c>'s body, isolated from the rest of the file: <c>_monitor.LastApplied</c> is read
    /// legitimately elsewhere in the dashboard (the HOST NETWORK card, the status line), so a whole-file
    /// count would be meaningless.
    ///
    /// <para><b>Comments are stripped</b> — the method's own comment explains why <c>LastApplied</c> must
    /// be read once, and a count that included prose would fail on the very code it is meant to bless.
    /// (It did, first run.) These tests assert what the code DOES; a line-comment cannot do anything.</para>
    /// </summary>
    private static string ConnectAsyncBody()
    {
        var src   = DashboardSource();
        var start = src.IndexOf("private async Task ConnectAsync(VmTarget vm)", StringComparison.Ordinal);
        Assert.True(start >= 0, "ConnectAsync not found in DashboardWindow.xaml.cs — fix this test, don't skip it.");

        var end = src.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        Assert.True(end > start, "Could not find the end of ConnectAsync — fix this test, don't skip it.");

        return Regex.Replace(src[start..end], @"//[^\n]*", "");
    }

    /// <summary>
    /// The log must name the switch the bind actually targeted (code review, 2026-07-16). The Failed-path
    /// log line re-read <c>_monitor.LastApplied?.VirtualSwitch</c> instead of the value passed to the flow.
    /// <c>LastApplied</c> is a plain non-volatile field written from <c>NetworkMonitor</c>'s own thread, and
    /// this method awaits a WMI round-trip in between — so a monitor pass landing during that await made
    /// the log name a DIFFERENT switch than the balloon the user had just read, in the very file the
    /// balloon tells them to consult.
    ///
    /// <para>Reading it exactly once is the fix and the invariant: capture, then use the capture. A second
    /// read is a second point in time, which is the whole bug.</para>
    /// </summary>
    [Fact]
    public void ConnectAsync_ReadsTheAppliedSwitchOnce()
    {
        var body  = ConnectAsyncBody();
        var reads = Regex.Matches(body, @"_monitor\.LastApplied").Count;

        Assert.True(reads == 1,
            $"ConnectAsync reads _monitor.LastApplied {reads} times; it must read it ONCE into a local and "
          + "use that local everywhere, including the log. It is written by NetworkMonitor's thread while "
          + "this method awaits, so a second read can name a different switch than the one the connect "
          + "bound against — and than the balloon the user just read.");
    }

    /// <summary>
    /// The report channel handed to <see cref="HyperVManagerTray.Helpers.VmConnectFlow"/> must actually
    /// wait for the balloon to be shown, not merely enqueue it — the flow awaits <c>warn</c> precisely so
    /// the report precedes vmconnect taking the foreground, and <c>App.ShowBalloon</c>'s whole body is
    /// <c>_ui.TryEnqueue(…)</c>. A plain <c>_notify(...)</c> lambda returning a completed task satisfies
    /// the signature while restoring the bug, and no test on the pure flow can see that.
    /// </summary>
    [Fact]
    public void ConnectAsync_WaitsForTheBalloonBeforeLaunching()
    {
        var body = ConnectAsyncBody();

        Assert.Contains("_notify(", body);
        Assert.Matches(new Regex(@"await\s+Task\.Yield\s*\(\s*\)"), body);
    }
}
