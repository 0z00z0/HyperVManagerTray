using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure state → UI decisions for the VM power controls (issue #30). WMI-free and side-effect-free, so it
/// is unit-testable without a live host.
///
/// <para>The dashboard cards are now its ONLY consumer: issue #34 removed every power verb from the tray,
/// on the grounds that a native Win32 menu cannot show progress or report an outcome, while the dashboard
/// — one left-click away — does both. What follows therefore describes the dashboard alone; the "tray menu
/// no longer offers every verb" framing below is the history that produced these rules, not a live second
/// caller.</para>
///
/// <para>Centralises three things the audit flagged as scattered/duplicated:</para>
/// <list type="bullet">
///   <item>which coarse <see cref="Shape"/> a VM state maps to (drives the card layout, including a
///   distinct <see cref="Shape.Transition"/> that renders no power buttons — finding 3);</item>
///   <item>which power verbs are valid to OFFER in that state, so the tray menu no longer offers every
///   verb regardless of state (finding 2);</item>
///   <item>the deadlines that retire a stuck/stale power-op overlay (findings 1 &amp; 7).</item>
/// </list>
/// </summary>
public static class VmStateUi
{
    /// <summary>Coarse card-layout / power-control category derived from a VM's state name.</summary>
    public enum Shape
    {
        /// <summary>No status yet, or an unrecognised/"Unknown" read (still loading).</summary>
        None,
        Off,
        Running,
        Paused,
        Saved,
        /// <summary>A transient state (Starting/Stopping/Saving/Pausing/Resuming/Snapshotting) — a
        /// power request here would just return 0x8007, so no actionable buttons are shown (finding 3).</summary>
        Transition,
    }

    // The transient EnabledState verbs WmiVmMapper.MapState can produce (root\virtualization\v2). During
    // any of these a RequestStateChange is rejected with 0x8007 "invalid state for this operation".
    private static readonly string[] TransitionStates =
        ["Starting", "Stopping", "Saving", "Pausing", "Resuming", "Snapshotting"];

    /// <summary>True while the VM is mid-transition (see <see cref="TransitionStates"/>).</summary>
    public static bool IsTransition(string? state) =>
        state is not null &&
        TransitionStates.Any(t => state.Equals(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps a VM state name (as produced by <see cref="WmiVmMapper.MapState"/>) to its coarse
    /// <see cref="Shape"/>. A null/empty/"Unknown" state is <see cref="Shape.None"/> (still loading).</summary>
    public static Shape ClassifyShape(string? state)
    {
        if (string.IsNullOrEmpty(state)) return Shape.None;
        if (state.Equals("Running", StringComparison.OrdinalIgnoreCase)) return Shape.Running;
        if (state.Equals("Paused",  StringComparison.OrdinalIgnoreCase)) return Shape.Paused;
        if (state.Equals("Saved",   StringComparison.OrdinalIgnoreCase)) return Shape.Saved;
        if (state.Equals("Off",     StringComparison.OrdinalIgnoreCase)) return Shape.Off;
        if (IsTransition(state)) return Shape.Transition;
        return Shape.None;   // "Unknown" or any unmapped value
    }

    /// <summary>
    /// The power verbs that make sense to OFFER for a given state (issue #30, findings 2 &amp; 3). A
    /// transitional or unknown state offers none — requesting a verb then would just fail with 0x8007.
    /// </summary>
    public static IReadOnlyList<VmOpKind> AllowedVerbs(string? state) => ClassifyShape(state) switch
    {
        Shape.Running => [VmOpKind.Shutdown, VmOpKind.Pause, VmOpKind.Save],
        Shape.Paused  => [VmOpKind.Resume, VmOpKind.Save],
        Shape.Saved   => [VmOpKind.Start],
        Shape.Off     => [VmOpKind.Start],
        _             => [],   // None / Transition
    };

    /// <summary>
    /// Whether a "Connect" (attach vmconnect) action makes sense for this state — only a Running VM can
    /// be attached to. Centralised here (issue #30, cleanup 9) so the dashboard cards and both tray
    /// VM-power variants decide Connect-eligibility from one place instead of each hard-coding
    /// "shape == Running".
    /// </summary>
    public static bool CanConnect(string? state) => ClassifyShape(state) == Shape.Running;

    // ── Overlay-expiry deadlines (findings 1 & 7) ────────────────────────────────

    /// <summary>Age at which a Running-phase overlay retires; long because install-on-shutdown can take many minutes.</summary>
    public static readonly TimeSpan RunningDeadline = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether an in-flight power-op overlay should be retired based on its age: any operation still in
    /// the Running phase after <see cref="RunningDeadline"/> (finding 1).
    ///
    /// <para><b>A failure never ages out.</b> A start is accepted as a job and can fail more than a
    /// minute later, by which time an age-based overlay would already have retired the progress it
    /// replaced — and the reason that matters, the one Hyper-V supplies with the failed job, is the one
    /// that arrives last. A failure now stays on the card until it is dismissed or until the machine
    /// reaches the state that makes it irrelevant.</para>
    /// </summary>
    public static bool IsOverlayExpired(VmOpKind kind, VmOpPhase phase, TimeSpan age) =>
        phase == VmOpPhase.Running && age >= RunningDeadline;

    /// <summary>The state a power action leaves the machine in; reaching it ends an operation that reports no outcome, such as a guest shutdown.</summary>
    public static string? SettledState(VmOpKind kind) => kind switch
    {
        VmOpKind.Start or VmOpKind.Resume => "Running",
        VmOpKind.Pause                     => "Paused",
        VmOpKind.Save                      => "Saved",
        VmOpKind.Shutdown                  => "Off",
        _                                  => null,
    };

    public static bool ReachedTarget(VmOpKind kind, VmStatus? s) =>
        SettledState(kind) is { } target && s?.State.Equals(target, StringComparison.OrdinalIgnoreCase) == true;
}
