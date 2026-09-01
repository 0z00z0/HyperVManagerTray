using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Guards the CALL SITE of issue #93 in <c>App.OnLaunched</c>, which no other test can reach.
///
/// <para><see cref="AppInfoTests"/> proves the line's text is right, but it links
/// <c>Helpers\AppInfo.cs</c> directly and would stay green if the call in <c>App.xaml.cs</c> were
/// deleted, moved after the work that can crash, or dropped to a level the default <c>logLevel</c>
/// filters out. <c>App.xaml.cs</c> is WinUI <c>Application</c> code-behind, which this deliberately
/// runtime-free test assembly cannot instantiate, so the wiring is asserted over the source text —
/// the same instrument, and the same limits, as <see cref="VmConnectFlowSourceTests"/>.</para>
///
/// <para>The properties asserted are the three the issue asks for: the line is written, it is written
/// at Warning — above the Information level the update-check line rode on, so a raised <c>logLevel</c>
/// keeps it as far as Warning — and it is written before anything else that can fail.</para>
/// </summary>
public class StartupVersionLogSourceTests
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

    /// <summary>Comments stripped: the surrounding prose names the level and the helper it explains.</summary>
    private static string AppCode() => Regex.Replace(AppSource(), @"//[^\n]*", "");

    /// <summary>The one call, at Warning. Information and below are filtered by a raised logLevel.</summary>
    private static readonly Regex VersionWrite =
        new(@"CreateLogger\(""startup""\)\s*\.\s*LogWarning\([^)]*AppInfo\.StartupVersionLine");

    [Fact]
    public void OnLaunched_WritesTheVersionLineAtWarning()
    {
        Assert.True(VersionWrite.IsMatch(AppCode()),
            "App.OnLaunched no longer logs AppInfo.StartupVersionLine at Warning through the \"startup\" "
          + "category (issue #93). Without it a log is attributable to a build only when the update check "
          + "completed, which is exactly the gap this line closes — and Warning, not Information, is what "
          + "keeps it through a logLevel raised as far as Warning.");
    }

    /// <summary>
    /// It must be the FIRST log write of the run. <c>LogStartupMilestone("OnLaunched entered", …)</c> was
    /// the first before this change; everything from the crash-dump registration onwards can throw, and a
    /// version line written after the throw is a version line the crash log never carries.
    /// </summary>
    [Fact]
    public void OnLaunched_WritesTheVersionLineBeforeAnyOtherLogging()
    {
        var code    = AppCode();
        var version = VersionWrite.Match(code);
        Assert.True(version.Success, "The version write is missing — see OnLaunched_WritesTheVersionLineAtWarning.");

        var milestone = code.IndexOf(@"LogStartupMilestone(""OnLaunched entered""", StringComparison.Ordinal);
        Assert.True(milestone >= 0,
            "The 'OnLaunched entered' milestone is gone — this test anchors on it; fix the anchor, don't skip it.");

        Assert.True(version.Index < milestone,
            "The version line is written after the first startup milestone (issue #93). It must be the first "
          + "log write of the run, so a crash seconds in still leaves a log that names its build.");
    }

    /// <summary>
    /// Unconditional, per the issue: not behind the update check, which is the conditional path the
    /// version currently depends on and the reason a timed-out check leaves a run unattributable.
    /// </summary>
    [Fact]
    public void OnLaunched_WritesTheVersionLineBeforeTheUpdateCheck()
    {
        var code    = AppCode();
        var version = VersionWrite.Match(code);
        Assert.True(version.Success, "The version write is missing — see OnLaunched_WritesTheVersionLineAtWarning.");

        var updateCheck = code.IndexOf("CheckForUpdatesOnStartupAsync", StringComparison.Ordinal);
        Assert.True(updateCheck >= 0,
            "CheckForUpdatesOnStartupAsync is gone — this test anchors on it; fix the anchor, don't skip it.");

        Assert.True(version.Index < updateCheck,
            "The version line no longer precedes the update check (issue #93). The whole point is that it "
          + "does not depend on the check, the network, or GitHub answering.");
    }
}
