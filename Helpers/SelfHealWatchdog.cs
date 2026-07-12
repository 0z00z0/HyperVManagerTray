using Microsoft.Win32;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Keeps exactly one HyperVManagerTray process alive and relaunches it if it dies unexpectedly.
///
/// Confirmed 2026-07-05: a GPU driver fault during a power-source change (WER LiveKernelEvent
/// Kernel_141) tore down the WinUI/Mica compositor and terminated the tray as a CLEAN exit — no
/// exception, no WER app record, no crash.log, no minidump. <see cref="Install"/> arms a relaunch
/// for exactly that case: any <see cref="AppDomain.ProcessExit"/> that isn't a call to
/// <see cref="MarkLegitimateExit"/> (tray Exit, a startup give-up) or a logoff/shutdown is treated
/// as that silent teardown and triggers a fresh instance.
/// </summary>
internal static class SelfHealWatchdog
{
    private const string SingleInstanceMutexName = @"Local\HyperVManagerTray.SingleInstance";
    private const string AutoRelaunchArg = "--auto-relaunch";

    // Worst-case time to ride out a self-heal relaunch racing the still-terminating prior instance
    // (same rationale as the retry in Services/FileLogger.cs's OpenWriter: an old-build instance can
    // briefly still hold the resource while a new one starts).
    private const int MutexAcquireTimeoutMs = 3000;

    private static volatile bool _legitimateExit;
    private static readonly DateTime ProcessStartUtc = DateTime.UtcNow;
    private static Mutex? _singleInstanceMutex;

    /// <summary>True if this process was launched by a self-heal relaunch — the display subsystem
    /// may still be recovering, so the caller should wait a beat before creating windows.</summary>
    public static bool IsAutoRelaunch => Environment.GetCommandLineArgs().Contains(AutoRelaunchArg);

    /// <summary>Wires the self-heal relaunch handler and the logoff/shutdown exemption. Call once, early in startup.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        SystemEvents.SessionEnding += (_, _) => _legitimateExit = true;
    }

    /// <summary>Marks the current exit as deliberate so <see cref="OnProcessExit"/> does not relaunch. Call right before every intentional Exit().</summary>
    public static void MarkLegitimateExit() => _legitimateExit = true;

    /// <summary>
    /// Acquires the process-wide single-instance mutex, waiting up to <see cref="MutexAcquireTimeoutMs"/>
    /// to ride out the window where a self-heal relaunch overlaps the still-terminating old instance.
    /// Returns false only if another instance genuinely holds it.
    /// </summary>
    public static bool AcquireLock()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
        try
        {
            return _singleInstanceMutex.WaitOne(MutexAcquireTimeoutMs);
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died abnormally (exactly our GPU-teardown crash) — we now hold it.
            return true;
        }
    }

    /// <summary>
    /// Fires on every CLEAN process teardown (NOT on a hard kill / taskkill, which is what we want —
    /// a Task Manager kill should stay dead). If the exit wasn't marked legitimate, it is the silent
    /// compositor-loss teardown — relaunch a fresh instance (rate-limited so a persistent fault can't
    /// spin a relaunch loop). The dying process is already elevated, so the child inherits elevation
    /// without a UAC prompt.
    /// </summary>
    private static void OnProcessExit(object? sender, EventArgs e)
    {
        if (_legitimateExit) return;

        var uptime = DateTime.UtcNow - ProcessStartUtc;
        if (!TryRecordRelaunch())
        {
            AppInfo.AppendCrashLogLine("LIFECYCLE", $"Unexpected teardown after {uptime:hh\\:mm\\:ss}; 3 relaunches within 10 min — giving up.");
            return;
        }

        AppInfo.AppendCrashLogLine("LIFECYCLE", $"Unexpected silent teardown after {uptime:hh\\:mm\\:ss} (likely GPU/compositor reset) — relaunching.");
        try
        {
            if (Environment.ProcessPath is { } exe)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(exe, AutoRelaunchArg) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            AppInfo.AppendCrashLogLine("LIFECYCLE", $"Relaunch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sliding-window rate limiter for the self-heal relaunch: false once 3 relaunches have happened
    /// in the last 10 minutes. Timestamps persist in a file because each check runs in a NEW process,
    /// so in-memory state can't span the relaunch chain it is limiting.
    /// </summary>
    private static bool TryRecordRelaunch()
    {
        try
        {
            var path   = Path.Combine(AppInfo.DataDir, "relaunch-history.txt");
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
            var recent = new List<long>();
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                    if (long.TryParse(line, out var ts) && ts >= cutoff) recent.Add(ts);

            if (recent.Count >= 3) return false;

            recent.Add(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllLines(path, recent.Select(t => t.ToString()));
            return true;
        }
        catch
        {
            return true;   // if the bookkeeping itself fails, err on the side of bringing the tray back
        }
    }
}
