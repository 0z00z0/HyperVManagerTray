using System.Globalization;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure elapsed-time → log-line composition for the interactive-path instrumentation (issue #54): the
/// tray right-click handler, the balloon dispatcher hop, and the startup milestones. Clock-free and
/// side-effect-free — every caller measures with a <see cref="System.Diagnostics.Stopwatch"/> and hands
/// the numbers here — so the wording of every line the diagnosis will be read off is unit-testable.
///
/// <para><b>Why this exists at all.</b> Issue #52 traced the warm right-click path by reading and found
/// it clean; Espen nevertheless experiences the tray as slow. The two cannot be reconciled because
/// ui.log timestamps menu <i>clicks</i> and never menu <i>opens</i> — the app is blind by construction,
/// so #52's page-in hypothesis is unfalsifiable from a normal day's use. These lines end that. They are
/// instrumentation, not a fix: nothing here changes what the app does, only what it says it did.</para>
///
/// <para><b>The rule this file exists to hold — a measurement names its own boundaries.</b> This is
/// <c>docs/DISPLAY-VOCABULARY.md</c> corollary 3 ("a report states only what was verified") applied to
/// numbers, and it is load-bearing here rather than stylistic. A latency figure is read as the answer to
/// "why does this feel slow", so a figure whose boundaries are unstated will be read as the whole
/// user-perceived delay — and every figure below is a strict subset of that delay. The single most
/// dangerous line this codebase could ship is a confident <c>"menu opened in 1.3 ms"</c>: it is true of
/// the rebuild, it is false of the menu, it contradicts the user's own experience, and it would be used
/// to close #52 as unreproducible. So each line names what it excludes, in the line itself and not only
/// in a comment — the reader has the log, not the source. See <see cref="RightClickLine"/> in
/// particular, which is the one a reader is most likely to over-read.</para>
///
/// <para><b>What no line here can measure</b>, because the app is not on either side of the boundary:
/// the gap between the physical click and our handler (the shell's dispatch plus any page-in of the code
/// that runs before us), and the paint of the native Win32 menu, which H.NotifyIcon builds and tracks
/// <i>after</i> <c>RightClickCommand</c> returns with no callback we can hook. Those are precisely where
/// #52 expects the time to be. The instrumentation is therefore designed to make the page-in hypothesis
/// falsifiable by CORRELATION — working set and idle gap on consecutive right-clicks (see
/// <see cref="RightClickLine"/>) — rather than by a direct measurement that cannot be taken from inside
/// the process.</para>
/// </summary>
public static class LatencyLog
{
    /// <summary>The tray right-click line (issue #54, items 1 and 4) — the one whose boundaries matter most.</summary>
    /// <param name="rebuildMs">
    /// How long <c>TrayMenu.RefreshState()</c> took: the app-side rebuild of the override and Manage VMs
    /// submenus from in-memory state. This is the ONLY duration on this line, and it is not the menu's.
    /// </param>
    /// <param name="workingSetBytes">
    /// <c>Environment.WorkingSet</c> read at handler entry, before any of our work — i.e. how much of the
    /// process was resident at the moment the click arrived. This is the #52 evidence: a right-click that
    /// arrives at ~15 MB followed, seconds later, by one at ~150 MB is the working-set trim and the
    /// page-in storm caught in the act, with no profiler.
    /// </param>
    /// <param name="sincePrevious">
    /// Time since the previous right-click handler entry, or null for the first of the session. The
    /// covariate #52 turns on: the page-in theory predicts the high-cost open is the one after a long
    /// idle gap, and that the next open moments later is cheap. Tracked unconditionally by the caller, so
    /// this is the gap since the previous right-click and not merely since the previous logged one.
    /// </param>
    public static string RightClickLine(double rebuildMs, long workingSetBytes, TimeSpan? sincePrevious)
    {
        var gap = sincePrevious is { } s ? FormatGap(s) : "first this session";
        return $"Tray: right-click reached the UI thread ({gap} since previous); menu rebuild "
             + $"{FormatMs(rebuildMs)}; working set {FormatMb(workingSetBytes)} at entry. "
             + "NOT menu-open latency — excludes the click→handler gap and the native menu build/paint "
             + "that H.NotifyIcon does after this returns.";
    }

    /// <summary>
    /// The balloon line (issue #54, item 2). Splits the one delay the app owns from the one it does not.
    /// </summary>
    /// <param name="title">The balloon's title — an identifier, never the message body (no PII in logs).</param>
    /// <param name="queueMs">
    /// From the <c>ShowBalloon</c> call to the enqueued body starting on the UI thread: the
    /// <c>DispatcherQueue</c> delay, and the app-side suspect for a toast that feels late.
    /// </param>
    /// <param name="handoffMs">
    /// How long <c>ShowNotification</c> (→ <c>Shell_NotifyIcon</c> <c>NIF_INFO</c>) took to return. That
    /// is the handoff to the shell, NOT the balloon appearing: Windows renders legacy balloons on its own
    /// schedule (commonly ~1–2 s — issue #53), and none of that time is visible from in here.
    /// </param>
    public static string BalloonLine(string title, double queueMs, double handoffMs) =>
        $"Balloon '{title}': {FormatMs(queueMs)} in the dispatcher queue, then Shell_NotifyIcon returned "
      + $"in {FormatMs(handoffMs)}. Excludes the OS balloon render — 'shown' here means handed to the "
      + "shell, not on screen.";

    /// <summary>
    /// A startup milestone (issue #54, item 3), as elapsed since the OS process start — so ui.log answers
    /// "how long until the tray icon?" outright, instead of it being inferred by subtracting
    /// second-resolution timestamps across two log files the way #52 had to.
    /// </summary>
    /// <param name="milestone">What was reached, phrased as an observed fact.</param>
    /// <param name="sinceProcessStartMs">
    /// Elapsed from <c>Process.StartTime</c> — the OS's own figure, so it INCLUDES the runtime and WinUI
    /// framework startup that precedes any code of ours. That share (~2 s on this machine per #52) is the
    /// point: it is most of the number and it is not ours to fix.
    /// </param>
    /// <param name="autoRelaunch">
    /// True when this process is a <c>SelfHealWatchdog</c> relaunch, which deliberately sleeps 5 s before
    /// creating any UI. Said in the line because it otherwise silently inflates every milestone after it
    /// by 5 s, and a reader comparing a relaunch's numbers against a normal boot's would conclude the app
    /// had regressed by exactly the amount of a delay it was designed to take.
    /// </param>
    public static string StartupLine(string milestone, double sinceProcessStartMs, bool autoRelaunch) =>
        $"Startup: {milestone} at {FormatMs(sinceProcessStartMs)} since process start"
      + (autoRelaunch ? " (self-heal relaunch — includes its deliberate 5 s settle delay)" : "")
      + ".";

    /// <summary>
    /// Milliseconds, at a precision that does not overstate what was measured: sub-10 ms figures keep one
    /// decimal (the rebuild is genuinely fractional), anything larger is whole milliseconds — a menu
    /// rebuild reported as "1234.7 ms" would imply a resolution the surrounding noise does not support.
    /// Invariant culture: <c>InvariantGlobalization</c> is on, and a log parsed by tooling must not
    /// acquire a decimal comma from a locale.
    /// </summary>
    public static string FormatMs(double ms) =>
        ms < 10
            ? ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms"
            : Math.Round(ms).ToString("0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>Whole megabytes — the working-set signal #52 needs is a ~10× step, not a byte count.</summary>
    public static string FormatMb(long bytes) =>
        (bytes / (1024 * 1024)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// An idle gap at human scale, one unit deep. Whether the previous right-click was 4 hours or 4.2
    /// hours ago does not change what #52 asks of it — only which side of "long enough to be trimmed" it
    /// falls on — so this rounds hard and stays readable.
    /// </summary>
    public static string FormatGap(TimeSpan gap) => gap switch
    {
        { TotalMilliseconds: < 1000 } => FormatMs(gap.TotalMilliseconds),
        { TotalSeconds: < 90 }        => gap.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s",
        { TotalMinutes: < 90 }        => gap.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min",
        _                             => gap.TotalHours.ToString("0.0", CultureInfo.InvariantCulture) + " h",
    };
}
