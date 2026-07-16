using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The Connect / Start &amp; Connect sequence (issue #45). The defect these pin: <c>ConnectAsync</c> called
/// <c>ApplySwitchAsync</c> in statement position — discarding the bool that says whether the VM's adapter
/// is actually on the switch (issue #37) — and launched vmconnect regardless. It was the last discarded
/// call site in the app.
///
/// <para>The rule under test is the one #37 established everywhere else: <b>a status surface must never
/// claim a state the app has not confirmed.</b> Here that means an unconfirmed bind must produce a
/// warning, and only a confirmed bind may pass silently. The deliberate second half — a failed bind still
/// connects — is pinned just as hard, because it is a decision, not an accident, and a future reader
/// changing it should have to change a test that says so.</para>
/// </summary>
public class VmConnectFlowTests
{
    private const string Vm     = "vDev";
    private const string Switch = "Bridged";

    /// <summary>Records what the flow did to the outside world, so ordering can be asserted and not assumed.</summary>
    private sealed class Recorder
    {
        public readonly List<string> Events = [];
        public readonly List<string> Warnings = [];
        public string? BoundTo;

        public Func<string, Task<bool>> Apply(bool result) => sw =>
        {
            BoundTo = sw;
            Events.Add("apply");
            return Task.FromResult(result);
        };

        public Func<string, Task<bool>> ApplyThrows(Exception ex) => _ =>
        {
            Events.Add("apply");
            return Task.FromException<bool>(ex);
        };

        /// <summary>True once the report has actually reached the user, as opposed to merely being queued.</summary>
        public bool WarnShown;

        /// <summary>
        /// Whether the report had been SHOWN at the instant vmconnect launched. Sampled inside
        /// <see cref="Launch"/>, synchronously, which is what makes the ordering assertion deterministic:
        /// it is a fact about the moment of launch, not a comparison of two list positions that a
        /// still-pending continuation may or may not have reached by the time the test looks.
        /// </summary>
        public bool? LaunchedAfterReport;

        /// <summary>
        /// A report channel that does NOT finish synchronously — which is the only kind production has.
        /// <c>App.ShowBalloon</c>'s entire body is <c>_ui.TryEnqueue(…)</c>, so the balloon appears on a
        /// later dispatcher turn; a delegate that recorded "warn" and returned would model a channel the
        /// app does not own, and that is exactly how these tests came to assert an ordering production
        /// never produced (code review, 2026-07-16). "warn-queued" is the call; "warn-shown" is the
        /// report actually reaching the user, and the flow must not launch vmconnect between the two.
        /// </summary>
        public Func<string, Task> Warn => async m =>
        {
            Events.Add("warn-queued");
            await Task.Yield();
            WarnShown = true;
            Events.Add("warn-shown");
            Warnings.Add(m);
        };

        public Action Launch => () =>
        {
            LaunchedAfterReport = WarnShown;
            Events.Add("launch");
        };
    }

    // ── The bug itself ───────────────────────────────────────────────────────────

    /// <summary>
    /// THE regression test for issue #45. A bind that returns false must not pass silently: the user gets
    /// a warning naming the VM and the switch. Before the fix this produced no warning of any kind — the
    /// bool went nowhere and vmconnect opened onto a VM stranded on the wrong network while every surface
    /// said everything was fine.
    ///
    /// <para>This is the test the mutation check targets: re-introduce
    /// <c>await _hyperV.ApplySwitchAsync(...);</c> as a discard and this fails, because a discarded result
    /// cannot produce a warning.</para>
    /// </summary>
    [Fact]
    public async Task RunAsync_FailedBindWarnsTheUser()
    {
        var r = new Recorder();
        var result = await VmConnectFlow.RunAsync(Vm, Switch, r.Apply(false), r.Warn, r.Launch);

        Assert.Equal(VmConnectFlow.BindStep.Failed, result.Bind);
        var warning = Assert.Single(r.Warnings);
        Assert.Contains(Vm, warning);
        Assert.Contains(Switch, warning);
        Assert.Equal(warning, result.Warning);
    }

    /// <summary>
    /// The other half of the same defect, stated as an invariant rather than a case: <b>only a confirmed
    /// bind may be silent.</b> Enumerating <see cref="VmConnectFlow.BindStep"/> means a future member
    /// cannot be added that quietly reads as success — it lands here and fails until someone decides what
    /// it reports, the same technique as
    /// <c>NetworkStatusUiTests.IconFor_OnlyAppliedEverRendersAsSuccess</c>.
    ///
    /// <para><see cref="VmConnectFlow.BindStep.NotAttempted"/> is silent too, and legitimately: no switch
    /// has been applied, so the app claims nothing about the network. Silence there is the absence of a
    /// claim, not the assertion of a good one.</para>
    /// </summary>
    [Theory]
    [InlineData(VmConnectFlow.BindStep.Confirmed,    true)]
    [InlineData(VmConnectFlow.BindStep.NotAttempted, true)]
    [InlineData(VmConnectFlow.BindStep.Failed,       false)]
    public async Task RunAsync_OnlyAnUnclaimedOrConfirmedBindIsSilent(VmConnectFlow.BindStep step, bool expectSilent)
    {
        var r = new Recorder();
        var result = await VmConnectFlow.RunAsync(
            Vm,
            step == VmConnectFlow.BindStep.NotAttempted ? null : Switch,
            r.Apply(step == VmConnectFlow.BindStep.Confirmed),
            r.Warn, r.Launch);

        Assert.Equal(step, result.Bind);
        Assert.Equal(expectSilent, result.Warning is null);
        Assert.Equal(expectSilent, r.Warnings.Count == 0);
    }

    // ── The deliberate decision: report, but still connect ────────────────────────

    /// <summary>
    /// The judgement call, pinned. A failed bind does NOT veto the console: the VM has a working console
    /// even when it is on the wrong network, and opening it is often how the user fixes the network. This
    /// is the start-TIMEOUT precedent (connect anyway), not the failed-START precedent (bail — issue #30,
    /// finding 6), because a VM that never started has no console to attach to at all.
    /// </summary>
    [Fact]
    public async Task RunAsync_FailedBindStillConnects()
    {
        var r = new Recorder();
        var result = await VmConnectFlow.RunAsync(Vm, Switch, r.Apply(false), r.Warn, r.Launch);

        Assert.True(result.Launched);
        Assert.Contains("launch", r.Events);
    }

    /// <summary>
    /// The warning must land BEFORE vmconnect does. vmconnect takes foreground focus, so a balloon raised
    /// after it competes with a window that has just grabbed the user's attention — the report would
    /// technically fire and still not be read.
    ///
    /// <para><b>This test was green and wrong, which is the point of its current shape.</b> It asserted
    /// exactly this ordering while production did the opposite: <c>warn</c> was an <c>Action</c> over
    /// <c>App.ShowBalloon</c>, which only enqueues, whereas <c>launchVmConnect</c> starts a process
    /// synchronously — so vmconnect always won. The test passed because its own <c>warn</c> delegate was
    /// synchronous, i.e. it modelled a channel the app does not have. <see cref="Recorder.Warn"/> now
    /// completes on a later turn like the real one, and asserting "warn-shown" precedes "launch" is
    /// therefore a claim about the app rather than about the double.</para>
    /// </summary>
    [Fact]
    public async Task RunAsync_FailedBindWarnsBeforeLaunchingTheConsole()
    {
        var r = new Recorder();
        await VmConnectFlow.RunAsync(Vm, Switch, r.Apply(false), r.Warn, r.Launch);

        Assert.Equal(["apply", "warn-queued", "warn-shown", "launch"], r.Events);
    }

    /// <summary>
    /// The same rule as the invariant rather than the sequence: the console must not open until the report
    /// has actually been SHOWN. A fire-and-forget <c>_ = warn(message);</c> — the realistic regression, and
    /// the one that compiles without even a CS4014 warning — fails here.
    ///
    /// <para><b>Why the flag and not the event list.</b> The obvious version of this test compares the
    /// positions of "warn-shown" and "launch" in <see cref="Recorder.Events"/>, and it is worthless: under
    /// the fire-and-forget mutation "warn-shown" is never recorded at all, so <c>IndexOf</c> returns -1,
    /// -1 is less than everything, and the test PASSES on the exact bug it was written for. (It did. The
    /// mutation check caught it.) Sampling <see cref="Recorder.WarnShown"/> inside the launch delegate
    /// asks the question at the only moment it has an answer, and cannot be raced.</para>
    /// </summary>
    [Fact]
    public async Task RunAsync_DoesNotLaunchUntilTheReportIsActuallyShown()
    {
        var r = new Recorder();
        await VmConnectFlow.RunAsync(Vm, Switch, r.Apply(false), r.Warn, r.Launch);

        Assert.True(r.LaunchedAfterReport,
            "vmconnect launched before the failed-bind report reached the user. VmConnectFlow must AWAIT "
          + "warn, not call it: the app's report channel (App.ShowBalloon) only enqueues the balloon, so "
          + "an un-awaited call lets Process.Start take the foreground first — see the ordering note on "
          + "VmConnectFlow. Events were: " + string.Join(" → ", r.Events));
    }

    // ── The ordinary paths ───────────────────────────────────────────────────────

    /// <summary>A confirmed bind connects with no noise — the overwhelmingly common case.</summary>
    [Fact]
    public async Task RunAsync_ConfirmedBindConnectsSilently()
    {
        var r = new Recorder();
        var result = await VmConnectFlow.RunAsync(Vm, Switch, r.Apply(true), r.Warn, r.Launch);

        Assert.Equal(VmConnectFlow.BindStep.Confirmed, result.Bind);
        Assert.Equal(Switch, r.BoundTo);
        Assert.Equal(["apply", "launch"], r.Events);
    }

    /// <summary>
    /// No switch applied yet (<c>LastApplied</c> null — startup, before the first evaluation): bind
    /// nothing, claim nothing, connect. Asserting that apply is never CALLED matters — binding to a null
    /// or empty switch name would be a WMI error, and the old code guarded this with
    /// <c>!string.IsNullOrEmpty(sw)</c>. That guard is preserved here.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RunAsync_NoAppliedSwitchConnectsWithoutBinding(string? applied)
    {
        var r = new Recorder();
        var result = await VmConnectFlow.RunAsync(Vm, applied, r.Apply(false), r.Warn, r.Launch);

        Assert.Equal(VmConnectFlow.BindStep.NotAttempted, result.Bind);
        Assert.Equal(["launch"], r.Events);   // apply never ran
        Assert.Null(r.BoundTo);
    }

    // ── Failure of the mechanism itself ──────────────────────────────────────────

    /// <summary>
    /// A throw is a failed bind, not a crash. This runs from a fire-and-forget button handler
    /// (<c>_ = action()</c>), so an escaping exception would be unobserved: no report, no console, and on
    /// an older runtime a torn-down process. Unconfirmed is unconfirmed, however it got that way — so the
    /// user gets the same warning, and the exception is handed back for the caller to log rather than
    /// swallowed.
    /// </summary>
    [Fact]
    public async Task RunAsync_ThrowingBindIsTreatedAsAFailureAndSurfacesTheError()
    {
        var r  = new Recorder();
        var ex = new InvalidOperationException("WMI went away");
        var result = await VmConnectFlow.RunAsync(Vm, Switch, r.ApplyThrows(ex), r.Warn, r.Launch);

        Assert.Equal(VmConnectFlow.BindStep.Failed, result.Bind);
        Assert.Same(ex, result.Error);
        Assert.Single(r.Warnings);
        Assert.True(result.Launched);
    }

    /// <summary>A confirmed bind reports no error to log.</summary>
    [Fact]
    public async Task RunAsync_SuccessCarriesNoError()
    {
        var r = new Recorder();
        Assert.Null((await VmConnectFlow.RunAsync(Vm, Switch, r.Apply(true), r.Warn, r.Launch)).Error);
    }

    // ── Vocabulary ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The warning is <see cref="NetworkStatusUi"/>'s, not a second display vocabulary invented in the
    /// flow (the rule <c>ManagedVmActions.cs:33</c> and <c>SettingsWindow.cs:112</c> state, and the one
    /// issue #37 spent its effort restoring). Pinned by identity against the helper, so a hand-rolled
    /// string in the flow fails here.
    /// </summary>
    [Fact]
    public async Task RunAsync_WarningComesFromNetworkStatusUi()
    {
        var r = new Recorder();
        await VmConnectFlow.RunAsync(Vm, Switch, r.Apply(false), r.Warn, r.Launch);

        Assert.Equal(NetworkStatusUi.ConnectBindFailedMessage(Vm, Switch), r.Warnings[0]);
    }

    /// <summary>
    /// The message names the VM, the switch, the log, and — crucially — the fact that the console is
    /// opening anyway. Reporting only the failure would be a lie by omission: vmconnect appears
    /// regardless, and a user reading "could not connect" while a console window opens would reasonably
    /// conclude it had recovered.
    /// </summary>
    [Fact]
    public void ConnectBindFailedMessage_ReportsTheFailureAndTheConnectAnywayDecision()
    {
        var msg = NetworkStatusUi.ConnectBindFailedMessage(Vm, Switch);

        Assert.Contains($"'{Vm}'", msg);
        Assert.Contains($"'{Switch}'", msg);
        Assert.Contains("switcher.log", msg);
        Assert.Contains("anyway", msg);
        // "virtual switch", never a bare "switch" beside "network" (issue #42's pinned vocabulary).
        Assert.Contains("virtual switch", msg);
    }

    /// <summary>
    /// The two places that report "these VMs are not on that switch" must say it with ONE sentence, not
    /// two literals that happen to match today.
    ///
    /// <para><b>Why this is asserted rather than commented.</b> <see cref="NetworkStatusUi"/> committed to
    /// this in prose — "deliberately the same sentence … because it is the same fact" — while holding it
    /// as two separate string literals, so the comment asserted an invariant nothing enforced. Fixing the
    /// bare-"switch" vocabulary in one, or repointing "switcher.log" after the #20/#21 log split, would
    /// have changed one and silently left the other reading differently about the same fact. The two are
    /// now built from one private helper; this pins that from the outside, where a re-duplication is
    /// visible.</para>
    /// </summary>
    [Fact]
    public void ConnectBindFailedMessage_SharesItsSentenceWithTheApplyPassReport()
    {
        // The same fact from the automatic side: one VM, same switch, VM-connect failed.
        var applyReport = NetworkStatusUi.FailureMessage(
            new MatchResult("Office LAN", Switch, [Vm])
            {
                ApplyStatus = NetworkStatusUi.SwitchApplyStatus.VmConnectFailed,
                FailedVms   = [Vm],
            });

        Assert.NotNull(applyReport);
        Assert.StartsWith(applyReport, NetworkStatusUi.ConnectBindFailedMessage(Vm, Switch), StringComparison.Ordinal);
    }
}
