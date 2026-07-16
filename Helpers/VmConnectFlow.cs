namespace HyperVManagerTray.Helpers;

/// <summary>
/// The Connect / Start &amp; Connect sequence (issue #45): bind the VM's network adapter to the
/// currently-applied virtual switch, report what actually happened, then launch vmconnect. Pure —
/// every side effect arrives as a delegate — so the ordering rule below is unit-testable without a
/// WinUI host or a live Hyper-V host, the same reason <see cref="NetworkStatusUi"/>,
/// <see cref="VmStateUi"/> and <see cref="AdapterNameRules"/> are pure.
///
/// <para><b>Why this is a class and not four lines in the dashboard.</b> Those four lines were the
/// bug. <c>DashboardWindow.ConnectAsync</c> read
/// <c>await _hyperV.ApplySwitchAsync(vm.Name, vm.NicName, sw);</c> — statement position, result
/// discarded — and then launched vmconnect unconditionally. <c>ApplySwitchAsync</c> returns whether
/// the adapter is CONFIRMED on the switch (issue #37); discarding it is exactly the defect #37 fixed
/// on every other surface, and it was the last such call site in the app. It survived because a
/// discarded <c>bool</c> is invisible at a glance and nothing could test a WinUI code-behind method.
/// Now the decision lives somewhere a test can reach, and <c>ConnectAsync</c> is thin enough that
/// re-introducing the discard means deleting a call to this class rather than dropping a character.
/// <c>VmConnectFlowSourceTests</c> guards that call site directly.</para>
///
/// <para><b>The judgement call, made deliberately: a failed bind REPORTS but still connects.</b>
/// Precedent existed both ways in the method this came from — <c>StartAndConnectAsync</c> bails when
/// the VM failed to start (issue #30, finding 6), but a plain start TIMEOUT still connects, because
/// vmconnect tolerates a VM mid-boot. A failed bind is the timeout case, not the failed-start case:
/// a VM that did not start has no console to attach to, so connecting is meaningless, whereas a VM on
/// the wrong network has a perfectly good console — and the console is quite often how the user
/// intends to FIX the network. Refusing to open it would strand them without the one tool that helps.
/// So the rule is: never pretend the bind worked, and never silently swallow it, but do not veto what
/// the user asked for. The warning states both halves — the failure and the fact that we are opening
/// the console anyway (<see cref="NetworkStatusUi.ConnectBindFailedMessage"/>).</para>
///
/// <para><b>Ordering matters.</b> The warning is raised BEFORE vmconnect launches. vmconnect takes
/// foreground focus; a balloon raised after it would appear behind/alongside a window that has just
/// grabbed the user's attention, which is how a report becomes a report nobody reads.</para>
///
/// <para><b>Which is why <paramref name="warn"/> is awaited, not called (code review, 2026-07-16).</b>
/// This paragraph used to be false. <c>warn</c> was an <c>Action</c> bound to <c>App.ShowBalloon</c>,
/// whose entire body is <c>_ui.TryEnqueue(…)</c> — so it QUEUED the balloon for a later dispatcher turn
/// and returned immediately, while <c>launchVmConnect</c> is <c>Process.Start(UseShellExecute: true)</c>
/// and ran synchronously on the same turn. vmconnect started first, every time; the ordering this class
/// documents as its reason for existing was the one thing it did not do. The tests passed only because
/// they hand it synchronous delegates, so they asserted an order production never produced — a green
/// test for a false claim. Making <c>warn</c> a <see cref="Func{T, TResult}"/> that is awaited puts the
/// report genuinely ahead of the launch and gives the tests something real to assert.</para>
/// </summary>
public static class VmConnectFlow
{
    /// <summary>What the bind half of a connect attempt did — the input to the report decision.</summary>
    public enum BindStep
    {
        /// <summary>No switch has been applied yet (<c>NetworkMonitor.LastApplied</c> is null, or names
        /// no switch), so no bind was attempted and NOTHING is claimed about the VM's network. Distinct
        /// from <see cref="Confirmed"/> on purpose: this is "we did not try", not "we tried and it is
        /// fine", and it must not be reported as either.</summary>
        NotAttempted,

        /// <summary>The VM's adapter is confirmed on the applied switch.</summary>
        Confirmed,

        /// <summary>The bind was attempted and did not confirm — <c>ApplySwitchAsync</c> returned false,
        /// or threw. The VM is NOT on the intended network.</summary>
        Failed,
    }

    /// <summary>
    /// The outcome of a connect attempt, returned for the caller to log and for tests to assert on.
    /// <paramref name="Warning"/> is null when there was nothing to report.
    /// </summary>
    /// <param name="Bind">What the bind half did.</param>
    /// <param name="Warning">The message shown to the user, or null if none was.</param>
    /// <param name="Launched">Whether vmconnect was launched. True in every non-throwing path — see
    /// the connect-anyway rule on <see cref="VmConnectFlow"/>.</param>
    /// <param name="Error">The exception <c>ApplySwitchAsync</c> threw, if it did. Surfaced rather than
    /// logged here so this stays free of the app's logging statics (and thus linkable into the tests).</param>
    public readonly record struct Result(BindStep Bind, string? Warning, bool Launched, Exception? Error);

    /// <summary>
    /// Runs the sequence. <paramref name="appliedSwitch"/> is <c>NetworkMonitor.LastApplied?.VirtualSwitch</c>;
    /// <paramref name="applySwitchAsync"/> is <c>HyperVManager.ApplySwitchAsync</c> curried with the VM and
    /// its adapter name; <paramref name="launchVmConnect"/> starts vmconnect.exe.
    ///
    /// <para><paramref name="warn"/> shows the message to the user, and its task must not complete until
    /// the report is actually on screen — see the ordering note on the class. A caller whose report
    /// channel merely queues (as <c>App.ShowBalloon</c>'s does) is responsible for waiting for that queue
    /// to drain; returning <c>Task.CompletedTask</c> from a fire-and-forget notify silently restores the
    /// bug this signature exists to prevent.</para>
    /// </summary>
    public static async Task<Result> RunAsync(
        string  vmName,
        string? appliedSwitch,
        Func<string, Task<bool>> applySwitchAsync,
        Func<string, Task>       warn,
        Action                   launchVmConnect)
    {
        ArgumentNullException.ThrowIfNull(applySwitchAsync);
        ArgumentNullException.ThrowIfNull(warn);
        ArgumentNullException.ThrowIfNull(launchVmConnect);

        // Nothing has been applied yet — there is no switch to bind to, so connect without claiming
        // anything about the network.
        if (string.IsNullOrEmpty(appliedSwitch))
        {
            launchVmConnect();
            return new Result(BindStep.NotAttempted, Warning: null, Launched: true, Error: null);
        }

        bool       confirmed;
        Exception? error = null;
        try
        {
            // The whole point of this class: the result is CONSUMED. If this ever returns to statement
            // position, VmConnectFlowSourceTests and the Failed-path tests below both fail.
            confirmed = await applySwitchAsync(appliedSwitch);
        }
        catch (Exception ex)
        {
            // ApplySwitchAsync already catches its own WMI errors and returns false, so this is the
            // belt-and-braces arm — but it is reached from a fire-and-forget button handler, where an
            // escaping exception would be unobserved and the user would get no report at all. An
            // unconfirmed bind is a failed bind, whichever way it became unconfirmed.
            confirmed = false;
            error     = ex;
        }

        if (confirmed)
        {
            launchVmConnect();
            return new Result(BindStep.Confirmed, Warning: null, Launched: true, Error: null);
        }

        // Report BEFORE launching — see the ordering note on the class. Awaited, so the report is on
        // screen before vmconnect takes the foreground, rather than merely having been queued.
        var message = NetworkStatusUi.ConnectBindFailedMessage(vmName, appliedSwitch);
        await warn(message);
        launchVmConnect();
        return new Result(BindStep.Failed, message, Launched: true, Error: error);
    }
}
