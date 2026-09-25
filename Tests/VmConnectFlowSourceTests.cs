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
    /// Every <c>ApplySwitchAsync</c> call in the dashboard must hand its outcome somewhere. The value is
    /// the whole point of the call — it says whether the VM's adapter really is on the switch — and issue
    /// #45 was that value awaited in statement position and dropped.
    ///
    /// <para>The permitted shapes are the ones that keep the value: the call awaited into a local or
    /// returned (the dashboard's own <c>MoveForConnectAsync</c>, which reads the outcome, asks the monitor
    /// to renew the guest address after a move, and hands
    /// <see cref="HyperVManagerTray.Helpers.VmConnectFlow"/> a bool), or the un-awaited lambda
    /// <c>sw => _hyperV.ApplySwitchAsync(...)</c> whose task the flow awaits and consumes. The forbidden
    /// shape is an <c>await</c> whose result binds to nothing, wherever on the line it sits — the bug was
    /// <c>if (!string.IsNullOrEmpty(sw)) await _hyperV.ApplySwitchAsync(...);</c>, so position proves
    /// nothing and only the discard does.</para>
    ///
    /// <para>It is a coarse instrument, and <see cref="Discards"/> is asserted against the historical
    /// discard forms below so a detector that can no longer fire cannot pass for a guard.</para>
    /// </summary>
    [Fact]
    public void Dashboard_NeverDiscardsTheSwitchApplyResult()
    {
        Assert.False(Discards(DashboardSource()),
            "DashboardWindow awaits ApplySwitchAsync without keeping its result, discarding the outcome "
          + "that says whether the VM's adapter is actually on the switch (issues #37/#45). Await it into "
          + "a local and consume it, as MoveForConnectAsync does, so VmConnectFlow can report a failed "
          + "bind to the user.");

        // The guard proved able to fail: both shapes the app has actually shipped are caught.
        Assert.True(Discards("        await _hyperV.ApplySwitchAsync(vm.Ref, vm.NicId, sw);"));
        Assert.True(Discards("        if (!string.IsNullOrEmpty(sw)) await _hyperV.ApplySwitchAsync(vm.Ref, vm.NicId, sw);"));
        Assert.False(Discards("        var outcome = await _hyperV.ApplySwitchAsync(vm.Ref, vm.NicId, sw);"));
        Assert.False(Discards("        return await _hyperV.ApplySwitchAsync(vm.Ref, vm.NicId, sw) != SwitchMoveOutcome.Failed;"));
    }

    /// <summary>
    /// True where the text awaits <c>ApplySwitchAsync</c> and binds the result to nothing. What precedes
    /// the <c>await</c> on its own line decides it: an assignment, a lambda arrow, a <c>return</c>, or an
    /// argument position keeps the value; anything else drops it.
    /// </summary>
    private static bool Discards(string text) =>
        Regex.Matches(text, @"(?m)^(?<before>[^\n]*?)await\s+_hyperV\.ApplySwitchAsync\s*\(")
             .Select(m => m.Groups["before"].Value.TrimEnd())
             .Any(before => !(before.EndsWith('=') || before.EndsWith("=>", StringComparison.Ordinal)
                            || before.EndsWith('(') || before.EndsWith(',')
                            || before.EndsWith("return", StringComparison.Ordinal)));

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
