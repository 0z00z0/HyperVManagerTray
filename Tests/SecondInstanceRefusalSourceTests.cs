using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Guards the refusal path in <c>App.OnLaunched</c>: a launch blocked by the single-instance lock
/// must leave a line in the crash log, that line must say which refusal it was, and the relaunch
/// hook must still be armed only after the lock is taken.
///
/// <para>The lock is taken before the LoggerFactory exists — that is built after the config read,
/// far below — so the crash log is the only sink reachable at that point, and a refusal that skips
/// it leaves no trace anywhere on the machine. <c>App.xaml.cs</c> is WinUI <c>Application</c>
/// code-behind, which this runtime-free test assembly cannot instantiate, so the wiring is asserted
/// over the source text — the same instrument, and the same limits, as
/// <see cref="StartupVersionLogSourceTests"/>.</para>
/// </summary>
public class SecondInstanceRefusalSourceTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    /// <summary>
    /// Located from THIS file's compile-time path, not the test assembly's bin directory: the source
    /// isn't copied to the output, and a path relative to bin breaks on any config/TFM change.
    /// </summary>
    private static string AppSource()
    {
        var path = Path.Combine(RepoRoot(), "App.xaml.cs");
        Assert.True(File.Exists(path), $"App.xaml.cs not found at '{path}' — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    /// <summary>Comments stripped: the surrounding prose names the helper it explains.</summary>
    private static string AppCode() => Regex.Replace(AppSource(), @"//[^\n]*", "");

    /// <summary>Where the lock is taken. Everything this class asserts is positioned against it.</summary>
    private static int AcquisitionIndex(string code)
    {
        var acquire = code.IndexOf("AppLifecycle.AcquireLock()", StringComparison.Ordinal);
        Assert.True(acquire >= 0,
            "AppLifecycle.AcquireLock() is gone from App.xaml.cs — this test anchors on it; fix the anchor, don't skip it.");
        return acquire;
    }

    /// <summary>The refusal branch: everything from the failed acquisition to the exit it ends in.</summary>
    private static string RefusalBranch()
    {
        var code    = AppCode();
        var acquire = AcquisitionIndex(code);

        var exit = code.IndexOf("Exit();", acquire, StringComparison.Ordinal);
        Assert.True(exit > acquire,
            "The refusal branch no longer ends in an Exit() call — this test anchors on it; fix the anchor, don't skip it.");

        return code[acquire..exit];
    }

    [Fact]
    public void RefusedLaunch_WritesToTheCrashLogBeforeExiting()
    {
        Assert.True(Regex.IsMatch(RefusalBranch(), @"AppInfo\.AppendCrashLogLine\("),
            "A launch refused by the single-instance lock no longer writes to the crash log. The lock is "
          + "taken before the LoggerFactory exists, so the crash log is the only sink reachable there — "
          + "without it a second launch exits leaving no trace anywhere, which is exactly the defect this "
          + "line closes.");
    }

    /// <summary>
    /// Through the shared helper, not a direct file write: the helper swallows its own failures, so a
    /// refusal cannot be turned into a crash by the very line meant to record it.
    /// </summary>
    [Fact]
    public void RefusedLaunch_DoesNotOpenTheCrashLogItself()
    {
        Assert.False(Regex.IsMatch(RefusalBranch(), @"File\.(Append|Write)"),
            "The refusal path writes to a file directly instead of going through AppInfo.AppendCrashLogLine. "
          + "The helper never throws; a raw write on this path can take down the launch it was added to record.");
    }

    /// <summary>
    /// The lock reports four outcomes, and the two refusals are different facts: another copy of the
    /// app is running, versus a mutex name this process may not open at all. A hard-coded sentence
    /// collapses them back to the boolean the component was taken to replace.
    /// </summary>
    [Fact]
    public void RefusedLaunch_SaysWhichRefusalItWas()
    {
        Assert.True(Regex.IsMatch(RefusalBranch(), @"SingleInstanceLog\.Message\("),
            "The refusal line no longer comes from SingleInstanceLog.Message, so the crash log cannot say "
          + "which refusal happened. A name this process may not open reads as an ordinary second launch, "
          + "and a mutex name the app can never take stays hidden.");
    }

    /// <summary>
    /// The refusal path must reach no exit hook. Armed ahead of the lock, a refused launch becomes an
    /// unmarked clean exit: the hook starts a third instance, which is refused in turn, and so on
    /// until the relaunch limiter stops it.
    /// </summary>
    [Fact]
    public void TheRelaunchHook_IsArmedOnlyAfterTheLockIsTaken()
    {
        var code = AppCode();
        var arm  = code.IndexOf("AppLifecycle.Arm()", StringComparison.Ordinal);
        Assert.True(arm >= 0,
            "AppLifecycle.Arm() is gone from App.xaml.cs — nothing brings the tray back after a teardown "
          + "nobody asked for; fix the anchor if it merely moved, don't skip it.");

        Assert.True(arm > AcquisitionIndex(code),
            "The relaunch hook is armed before the single-instance lock is taken. A refused launch then "
          + "exits into its own hook and relaunches itself into the same refusal, over and over, until the "
          + "limiter stops it.");
    }
}
