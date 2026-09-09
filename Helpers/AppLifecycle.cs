using Microsoft.Win32;
using ZeroZero.Lifecycle;
using ZeroZero.Primitives;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// This app's wiring over <c>ZeroZero.Lifecycle</c>: the single-instance lock, the relaunch on a
/// clean exit nobody asked for, and the one fact about that teardown the component does not record.
///
/// Confirmed 2026-07-05: a GPU driver fault during a power-source change (WER LiveKernelEvent
/// Kernel_141) tore down the WinUI/Mica compositor and terminated the tray as a CLEAN exit — no
/// exception, no WER app record, no crash.log, no minidump. That teardown is what the relaunch
/// exists for, and <see cref="OnProcessExit"/> is what names it in the crash log.
/// </summary>
internal static class AppLifecycle
{
    // Character for character what earlier builds took: a new name lets an old build and a new one
    // run side by side through an upgrade.
    private const string SingleInstanceMutexName = @"Local\HyperVManagerTray.SingleInstance";

    private static readonly DateTime ProcessStartUtc = DateTime.UtcNow;

    private static ProcessLifecycle? _lifecycle;
    private static volatile bool _deliberateExit;
    private static volatile bool _sessionEnding;

    /// <summary>True when a previous instance's exit hook started this process — the display
    /// subsystem may still be recovering, so the caller waits a beat before creating windows.</summary>
    public static bool IsRelaunch { get; } =
        Relaunch.WasRelaunched(Environment.GetCommandLineArgs().Skip(1));

    /// <summary>
    /// Takes the lock on the calling thread, which must be one that lives as long as the process: a
    /// thread ending while it owns the mutex abandons it, and the next instance takes an abandoned
    /// mutex as its own. No wait on an ordinary launch; a relaunch gives the instance that spawned it
    /// the settle delay, because the mutex is released only once that instance's own exit handlers
    /// have run.
    /// </summary>
    public static SingleInstanceOutcome AcquireLock() => SingleInstanceLock.Acquire(
        SingleInstanceMutexName, IsRelaunch ? Relaunch.SettleDelay : TimeSpan.Zero);

    /// <summary>
    /// Arms the relaunch and the teardown note. Only ever after the lock is taken: a refused launch
    /// that had armed this would relaunch itself into the same refusal until the limiter stopped it.
    /// </summary>
    public static void Arm()
    {
        if (_lifecycle is not null) return;

        // Registered before the component's own hook so the crash log reads teardown, then decision.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        SystemEvents.SessionEnding += (_, _) => _sessionEnding = true;

        _lifecycle = new ProcessLifecycle(new ProcessLifecycleOptions
        {
            DataDirectory = AppInfo.DataDir,
            Log = new CrashLogSink(),
        });
        _lifecycle.Arm();
    }

    /// <summary>Marks the exit about to happen as asked for, so nothing relaunches. Call right before
    /// every intentional Exit().</summary>
    public static void MarkDeliberateExit()
    {
        _deliberateExit = true;
        _lifecycle?.MarkDeliberateExit();
    }

    /// <summary>
    /// Runs on every CLEAN teardown (not on a hard kill, which should stay dead). An exit nobody asked
    /// for is the silent compositor loss, and how long the process had been up is what separates that
    /// from a startup that never got going — neither is recorded by the component, whose own line says
    /// only what it decided to do next.
    /// </summary>
    private static void OnProcessExit(object? sender, EventArgs e)
    {
        if (_deliberateExit || _sessionEnding) return;

        var uptime = DateTime.UtcNow - ProcessStartUtc;
        AppInfo.AppendCrashLogLine("LIFECYCLE",
            $"Unexpected silent teardown after {uptime:hh\\:mm\\:ss} (likely GPU/compositor reset).");
    }

    /// <summary>
    /// Where the component says what it did. The crash log, not the file logger: these lines are
    /// written from the exit hook, by which point the logger factory is disposed, and the crash-log
    /// append never throws.
    /// </summary>
    private sealed class CrashLogSink : ILogSink
    {
        public void Info(string message) => AppInfo.AppendCrashLogLine("LIFECYCLE", message);

        public void Error(string source, Exception? ex) =>
            AppInfo.AppendCrashLogLine("LIFECYCLE", $"{source} failed: {ex?.Message ?? "no detail given"}");
    }
}
