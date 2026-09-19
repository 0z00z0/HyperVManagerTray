using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Guards how <c>Services\StartupManager.cs</c> drives ZeroZero.Startup's logon task — the call sites no
/// other test can reach.
///
/// <para>The file registers, deletes and repairs a real scheduled task, so it is deliberately not linked
/// into this runtime-free test assembly. The task's definition, the repair decision and the stale
/// install-path check are the component's, and are proved by its own suite against the real scheduler;
/// what is pinned here is that this app reaches them the right way. Same instrument, and the same
/// limits, as <see cref="MqttServiceSourceTests"/> and <see cref="StartupVersionLogSourceTests"/>.</para>
/// </summary>
public class StartupManagerSourceTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    private static string ReadRepoFile(params string[] parts)
    {
        var path = Path.Combine([RepoRoot(), .. parts]);
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    /// <summary>Comments stripped: the surrounding prose names the members being asserted.</summary>
    private static string Code() =>
        Regex.Replace(ReadRepoFile("Services", "StartupManager.cs"), @"//[^\n]*", "");

    /// <summary>From a member's signature to the start of the next one.</summary>
    private static string Body(string startAnchor, string endAnchor)
    {
        var code = Code();

        int start = code.IndexOf(startAnchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{startAnchor}' is gone — this test anchors on it; fix the anchor, don't skip it.");

        int end = code.IndexOf(endAnchor, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find '{endAnchor}' after '{startAnchor}' — fix this test, don't skip it.");

        return code[start..end];
    }

    /// <summary>Issue #71: existence alone is not enough. A task disabled through Task Scheduler still
    /// exists, so a read that stops at existence reports On for a task the scheduler will never fire. The
    /// read goes through the decision helper, so the contract <see cref="StartupTaskStateTests"/> pins
    /// governs this call site.</summary>
    [Fact]
    public void IsEnabled_ReadsTheEnabledFlagThroughStartupTaskState()
    {
        var body = Body("public bool IsEnabled", "public void Enable(");

        Assert.Contains("StartupTaskState.IsEnabled(", body, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"return\s+task\.IsEnabled\s*;"), body);
        Assert.DoesNotMatch(new Regex(@"is\s+not\s+null"), body);
    }

    /// <summary>The toggle creates and removes the task. The component's own Enable() and Disable() only
    /// flip the flag of a task that is already there, and throw when there is none.</summary>
    [Fact]
    public void TheToggle_RegistersAndDeletesTheTask()
    {
        Assert.Contains("task.Register();", Body("public void Enable(", "public void Disable("), StringComparison.Ordinal);
        Assert.Contains("task.Delete();", Body("public void Disable(", "public void TryRepair("), StringComparison.Ordinal);
    }

    /// <summary>The repair runs against the running executable, which is what lets it notice a task that
    /// still starts an older install path — and only from an installed copy, so a build started from its
    /// output folder never takes the logon task over. No demand start: the component would start the
    /// running app's own task.</summary>
    [Fact]
    public void TryRepair_RunsFromAnInstalledCopyAgainstTheRunningExecutable()
    {
        var body = Body("public void TryRepair(", "private static bool IsInstalledCopy(");

        int gate   = body.IndexOf("IsInstalledCopy(Environment.ProcessPath)", StringComparison.Ordinal);
        int repair = body.IndexOf(".Repair()", StringComparison.Ordinal);
        Assert.True(gate >= 0 && repair > gate, "TryRepair must check for an installed copy before it repairs.");
        Assert.Matches(new Regex(@"using\s+var\s+task\s*=\s*Open\(\)\s*;"), body);

        Assert.DoesNotContain("VerifyByDemandStart", Code(), StringComparison.Ordinal);
    }

    /// <summary>The task's name is its identity: the installer registers and removes it under the same
    /// name, and a change here would leave every existing user's task behind.</summary>
    [Fact]
    public void TaskName_MatchesTheInstallersAndNeverChanges()
    {
        var installer = ReadRepoFile("installer", "HyperVManagerTray.iss");

        Assert.Contains("internal const string TaskName = \"HyperVManagerTray\";", Code(), StringComparison.Ordinal);
        Assert.Matches(new Regex(@"#define\s+TaskName\s+""HyperVManagerTray"""), installer);
    }
}
