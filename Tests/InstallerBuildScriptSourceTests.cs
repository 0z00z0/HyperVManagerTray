using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The checks <c>installer\build-installer.ps1</c> makes before it builds, read as text. The script
/// publishes, signs and compiles an installer, so no unit test can run it; a check dropped or moved
/// during an edit would otherwise surface only in an installer that reached another machine.
/// </summary>
public class InstallerBuildScriptSourceTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    /// <summary>The script with its comments removed, so a comment naming a check cannot satisfy it.</summary>
    private static string Script()
    {
        var path = Path.Combine(RepoRoot(), "installer", "build-installer.ps1");
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        var text = Regex.Replace(File.ReadAllText(path), @"<#.*?#>", "", RegexOptions.Singleline);
        return Regex.Replace(text, @"(?m)^\s*#[^\r\n]*", "");
    }

    private static int IndexOf(string code, string what)
    {
        int i = code.IndexOf(what, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{what}' is gone from build-installer.ps1 — fix this test's anchor, don't skip it.");
        return i;
    }

    /// <summary>Issue #77: an installer built before its changes are committed ships code no commit
    /// holds. The refusal has to come before the version bump the script writes and before the publish
    /// compiles the tree.</summary>
    [Fact]
    public void AnUncommittedTreeIsRefusedBeforeAnythingIsBuilt()
    {
        var code = Script();

        int load    = IndexOf(code, "BuildChecks.ps1");
        int refuse  = IndexOf(code, "Assert-CommittedTree -Root $root -ProjectFile \"HyperVManagerTray.csproj\"");
        int bump    = IndexOf(code, "Set-Content $proj");
        int publish = IndexOf(code, "dotnet publish");

        Assert.True(load < refuse, "BuildChecks.ps1 must be loaded before Assert-CommittedTree is called.");
        Assert.True(refuse < bump, "The uncommitted-tree refusal must come before the version bump.");
        Assert.True(refuse < publish, "The uncommitted-tree refusal must come before dotnet publish.");
    }

    /// <summary>The release workflow compiles with a pinned Inno Setup 7. Taking the first compiler on
    /// the command path let a hand build use 6.x without saying so; the compiler must come from the
    /// version-checked lookup, and before the publish.</summary>
    [Fact]
    public void TheCompilerIsVersionCheckedForInnoSetup7()
    {
        var code = Script();

        int find    = IndexOf(code, "Find-InnoSetupCompiler -RequiredMajor 7");
        int publish = IndexOf(code, "dotnet publish");

        Assert.True(find < publish, "The Inno Setup lookup must come before dotnet publish.");
        Assert.DoesNotContain("Get-Command iscc.exe", code, StringComparison.OrdinalIgnoreCase);
    }
}
