using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure state → UI decisions for the VM power controls, shared by the dashboard cards and the tray
/// VM-Power submenu (issue #30). WMI-free and side-effect-free, so it is unit-testable without a live
/// host. Centralises three things the audit flagged as scattered/duplicated:
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

    // ── Overlay-expiry deadlines (findings 1 & 7) ────────────────────────────────

    /// <summary>A graceful Shutdown emits only a "Running" phase and is normally cleared when the VM
    /// reaches Off; if the guest cancels/hangs the shutdown it never reaches Off, so after this long the
    /// overlay is retired so the card's buttons aren't disabled forever (finding 1).</summary>
    public static readonly TimeSpan ShutdownDeadline = TimeSpan.FromMinutes(2);

    /// <summary>A sticky "Failed: …" overlay ages out after this long so it doesn't linger forever
    /// (finding 7).</summary>
    public static readonly TimeSpan FailedOverlayLifetime = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Whether an in-flight/failed power-op overlay should be retired based on its age. Covers the two
    /// overlays that otherwise never clear on their own: a hung/cancelled graceful Shutdown that stays
    /// in the Running phase (finding 1), and a sticky Failed overlay (finding 7). Other ops clear when
    /// the VM reaches their target state, so they are never expired by age here.
    /// </summary>
    public static bool IsOverlayExpired(VmOpKind kind, VmOpPhase phase, TimeSpan age) =>
        (phase == VmOpPhase.Failed && age >= FailedOverlayLifetime) ||
        (kind == VmOpKind.Shutdown && phase == VmOpPhase.Running && age >= ShutdownDeadline);
}
