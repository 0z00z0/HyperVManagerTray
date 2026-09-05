using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Guards the one call site issue #71 is actually about, which no other test can reach.
///
/// <para><c>Services\StartupManager.cs</c> registers and deletes a real scheduled task, so it is
/// deliberately not linked into this runtime-free test assembly (see the csproj comment above the
/// <c>StartupTaskDefinition.cs</c>/<c>StartupTaskRepair.cs</c> links). <see cref="StartupTaskStateTests"/>
/// covers the decision helper <c>StartupManager.IsEnabled</c> calls, but every one of those tests feeds
/// the helper a hand-written <c>bool?</c> — none of them touches the scheduler read itself. Reverting
/// that read to the old existence-only check (<c>task is not null</c>) reproduces issue #71 exactly and
/// leaves every other test in the suite green; this is the same instrument, and the same limits, as
/// <see cref="MqttServiceSourceTests"/> and <see cref="StartupVersionLogSourceTests"/>.</para>
/// </summary>
public class StartupManagerSourceTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    private static string Source()
    {
        var path = Path.Combine(RepoRoot(), "Services", "StartupManager.cs");
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    /// <summary>Comments stripped: the surrounding prose names the flag being asserted.</summary>
    private static string Code() => Regex.Replace(Source(), @"//[^\n]*", "");

    /// <summary>From the property's signature up to the next member, by indentation — same bounded-window
    /// approach as <see cref="StartupVersionLogSourceTests"/>, since the body here is a lambda rather than
    /// a plain method and does not close on a single brace at method indent.</summary>
    private static string IsEnabledBody(string code)
    {
        int start = code.IndexOf("public bool IsEnabled", StringComparison.Ordinal);
        Assert.True(start >= 0, "StartupManager.IsEnabled is gone — this test anchors on it; fix the anchor, don't skip it.");

        int end = code.IndexOf("public void Enable(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not find the end of IsEnabled — fix this test, don't skip it.");

        return code[start..end];
    }

    /// <summary>THE test for issue #71: existence alone is not enough. A task disabled through Task
    /// Scheduler still exists, so a body that stops at <c>task is not null</c> — what this property read
    /// before the fix — reports On for a task the scheduler will never fire.</summary>
    [Fact]
    public void IsEnabled_ReadsTheEnabledFlagNotMereExistence()
    {
        var body = IsEnabledBody(Code());

        Assert.False(Regex.IsMatch(body, @"return\s+task\s+is\s+not\s+null"),
            "StartupManager.IsEnabled has regressed to an existence-only check (issue #71). A task "
          + "disabled through Task Scheduler still exists, so this reports On for a task that will never "
          + "start the app.");

        Assert.Matches(new Regex(@"task\s*\?\.\s*Enabled"), body);
    }

    /// <summary>The read must go through the decision helper, not inline its own comparison — so the
    /// three-valued contract <see cref="StartupTaskStateTests"/> pins actually governs this call site.</summary>
    [Fact]
    public void IsEnabled_RoutesThroughStartupTaskState()
    {
        var body = IsEnabledBody(Code());

        Assert.Contains("StartupTaskState.IsEnabled(", body, StringComparison.Ordinal);
    }
}
