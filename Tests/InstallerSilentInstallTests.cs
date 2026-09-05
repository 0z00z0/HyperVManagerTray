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
/// suppress Inno's own dialogs; neither suppresses UAC. A silent install has nobody at the keyboard,
/// so an unguarded elevation puts an unexplained consent dialog on the desktop and blocks the install
/// until somebody answers it.</para>
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
    /// the one an unattended install would hit.
    /// </summary>
    [Fact]
    public void TheStartupTaskIsRegisteredOnlyOnAnInteractiveInstall() =>
        Assert.Contains(
            "if (not WizardSilent()) and WizardIsTaskSelected('runstartup') then RegisterStartupTask();",
            Code());

    /// <summary>The neighbours whose guards this one now matches. Pinned together so the convention is
    /// the assertion, not one line of it.</summary>
    [Theory]
    [InlineData("if not WizardSilent() then LaunchApp();")]
    [InlineData("if (not UninstallSilent()) and (AppIsRunning() or ScheduledTaskExists()) then")]
    public void TheOtherElevatingStepsStayGuardedToo(string guarded) =>
        Assert.Contains(guarded, Code());

    /// <summary>
    /// Issue #139-equivalent — <c>PrepareToInstall</c>'s process-close used to be a blind
    /// single-shot <c>taskkill</c> that fired regardless of whether it succeeded, and was skipped
    /// entirely on a silent install (which then proceeded even with the app running, risking a
    /// locked-file failure or a silently stale copy). It is now a retry/cancel wait loop
    /// (<c>CloseRunningApp</c>), and the silent path is a DELIBERATE divergence from that old
    /// behaviour: a silent run can answer neither a message box nor a UAC prompt, so it aborts
    /// Setup immediately instead of proceeding quietly.
    /// </summary>
    [Fact]
    public void ASilentRunAbortsImmediatelyInsteadOfSkippingTheCheck()
    {
        var code = Code();

        Assert.Contains("if not ImageIsRunning(ImageName) then Exit;", code);
        Assert.Contains("if WizardSilent() then begin Result := TerminalMessage; Exit; end;", code);
    }

    /// <summary>
    /// The retry loop must not raise a second UAC prompt for an app the user already closed by
    /// hand between the message box appearing and Retry being pressed — the kill is gated on a
    /// fresh presence check taken immediately beforehand, not on the poll result that triggered
    /// the message box.
    /// </summary>
    [Fact]
    public void TheKillRechecksPresenceImmediatelyBeforeElevating()
    {
        var code = Code();

        Assert.Contains(
            "if ImageIsRunning(ImageName) then ShellExec('runas', ExpandConstant('{cmd}'), " +
            "'/C taskkill /IM \"' + ImageName + '\" /F',",
            code);
    }

    /// <summary>
    /// The wait loop resolves the moment the process is gone rather than always burning the full
    /// ~2 s budget — an early-exit-on-absence loop, not one requiring 10 consecutive
    /// present-results before declaring it gone.
    /// </summary>
    [Fact]
    public void TheWaitLoopExitsAsSoonAsTheProcessIsGone()
    {
        var code = Code();

        Assert.Contains("for I := 1 to 10 do begin if not ImageIsRunning(ImageName) then " +
                         "begin Result := False; Exit; end;", code);
    }

    /// <summary>Both message strings carry the product name (via the DisplayName parameter,
    /// called with <c>{#AppName}</c>) and this app's own established "tray icon" phrase, not a
    /// generic placeholder or ChargeKeeper's "notification area" wording.</summary>
    [Fact]
    public void TheMessagesNameTheProductAndItsOwnTrayIconPhrase()
    {
        var code = Code();

        Assert.Contains("Result := CloseRunningApp('{#AppName}', '{#AppExe}');", code);
        Assert.Contains(
            "TerminalMessage := DisplayName + ' is still running, so its files cannot be replaced. Exit it ' " +
            "+ 'from the tray icon, then run this installer again.';",
            code);
        Assert.Contains(
            "if MsgBox(DisplayName + ' is still running, so its files cannot be replaced.' " +
            "+ #13#10#13#10 + 'Exit it from the tray icon, then choose Retry.',",
            code);
        Assert.DoesNotContain("notification area", InstallerScript(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The counterpart that must NOT be guarded: the background-update logon task left behind by older
    /// installs is deleted with a plain <c>schtasks /Delete</c>, needing no admin and no prompt. The
    /// deletion is unconditional, so an upgrade clears the dead task whether or not anyone ever ticked
    /// the option that created it, and a silent upgrade clears it too. Nothing creates the task any
    /// more — the installer must never register one again.
    /// </summary>
    [Fact]
    public void TheDeadBackgroundUpdateTaskIsRemovedOnEveryInstall()
    {
        var code = Code();

        Assert.Contains("RegisterStartupTask(); RemoveAutoUpdateTask();", code);
        Assert.DoesNotContain("WizardIsTaskSelected('autoupdate')", code);
        Assert.DoesNotContain("RegisterAutoUpdateTask", code);
    }

    /// <summary>
    /// Why the guard above is needed at all, stated as an assertion rather than a comment: this is the
    /// only <c>runas</c> reachable from <c>CurStepChanged</c>, and <c>RemoveAutoUpdateTask</c> plain
    /// <c>Exec</c>s <c>schtasks</c>. If a future step gains an elevation, the count moves and this test
    /// asks for the guard to be considered.
    /// </summary>
    [Fact]
    public void ExactlyThreeStepsElevate()
    {
        var elevations = Regex.Matches(Code(), @"ShellExec\('runas'");

        Assert.Equal(3, elevations.Count);   // CloseRunningApp (via PrepareToInstall), RegisterStartupTask, StopAppAndRemoveStartupTask
    }
}
