using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Issue #76 — the rule the installer's <c>[Code]</c> section exists to hold: <b>a silent install never elevates.</b>
///
/// <para>The installer is per-user and needs no admin, but three of its steps do — closing the running
/// (elevated) app, registering the RL HIGHEST logon task, and launching the app. Each elevates through
/// <c>ShellExec('runas', …)</c>, which raises a UAC prompt. <c>/SILENT</c> and <c>/SUPPRESSMSGBOXES</c>
/// suppress Inno's own dialogs; neither suppresses UAC. The app's own installer schedules a
/// <c>winget upgrade --silent</c> at every sign-in, so an unguarded elevation would put an unexplained
/// consent dialog on the desktop and block the upgrade until somebody answered it.</para>
///
/// <para>These read Pascal script as text, which no unit test can execute. That is the whole point:
/// <c>installer\HyperVManagerTray.iss</c> is compiled by ISCC and has no other automated reader, so a
/// guard removed during an edit would otherwise surface only as a prompt on a user's machine. The
/// assertions are deliberately shaped around the guard rather than the whole statement, so ordinary
/// edits to the surrounding code do not break them.</para>
/// </summary>
public class InstallerSilentInstallTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    private static string InstallerScript()
    {
        var path = Path.Combine(RepoRoot(), "installer", "HyperVManagerTray.iss");
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    /// <summary>Comment-free, whitespace-collapsed script text, so an assertion cannot be satisfied by
    /// a comment that merely mentions the guard.</summary>
    private static string Code()
    {
        var stripped = Regex.Replace(InstallerScript(), @"//[^\r\n]*", string.Empty);
        return Regex.Replace(stripped, @"\s+", " ");
    }

    /// <summary>
    /// THE test for the defect. <c>RegisterStartupTask</c> is the only step whose elevation is driven
    /// by a task tick rather than by the app already running, so it was the one left unguarded — and
    /// the one a background winget upgrade would hit.
    /// </summary>
    [Fact]
    public void TheStartupTaskIsRegisteredOnlyOnAnInteractiveInstall() =>
        Assert.Contains(
            "if (not WizardSilent()) and WizardIsTaskSelected('runstartup') then RegisterStartupTask();",
            Code());

    /// <summary>The neighbours whose guards this one now matches. Pinned together so the convention is
    /// the assertion, not one line of it.</summary>
    [Theory]
    [InlineData("if (not WizardSilent()) and AppIsRunning() then")]
    [InlineData("if not WizardSilent() then LaunchApp();")]
    [InlineData("if (not UninstallSilent()) and (AppIsRunning() or ScheduledTaskExists()) then")]
    public void TheOtherElevatingStepsStayGuardedToo(string guarded) =>
        Assert.Contains(guarded, Code());

    /// <summary>
    /// The counterpart that must NOT be guarded: the auto-update task is created without
    /// <c>/RL HIGHEST</c>, so it needs no admin and no prompt. Guarding it would silently drop the
    /// user's "auto update in background" choice on exactly the silent upgrades that renew it.
    /// </summary>
    [Fact]
    public void TheAutoUpdateTaskIsStillRegisteredOnASilentInstall()
    {
        var code = Code();

        Assert.Contains("if WizardIsTaskSelected('autoupdate') then RegisterAutoUpdateTask();", code);
        Assert.DoesNotContain("WizardSilent()) and WizardIsTaskSelected('autoupdate')", code);
    }

    /// <summary>
    /// Why the guard above is needed at all, stated as an assertion rather than a comment: this is the
    /// only <c>runas</c> reachable from <c>CurStepChanged</c>, and <c>RegisterAutoUpdateTask</c> plain
    /// <c>Exec</c>s <c>schtasks</c>. If a future step gains an elevation, the count moves and this test
    /// asks for the guard to be considered.
    /// </summary>
    [Fact]
    public void ExactlyThreeStepsElevate()
    {
        var elevations = Regex.Matches(Code(), @"ShellExec\('runas'");

        Assert.Equal(3, elevations.Count);   // PrepareToInstall, RegisterStartupTask, StopAppAndRemoveStartupTask
    }
}
