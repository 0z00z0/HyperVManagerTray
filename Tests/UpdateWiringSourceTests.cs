using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;
using ZeroZero.Update.Win32;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Guards the update wiring that no other test can reach: the WinUI <c>Application</c> code-behind,
/// the class that owns the HTTP clients and the flow, and the one that shows the dialogs. This
/// deliberately runtime-free test assembly cannot instantiate any of them, so the wiring is asserted
/// over the source text — the same instrument, and the same limits, as
/// <see cref="StartupVersionLogSourceTests"/>.
///
/// <para>The properties asserted are the ones a mistake in would be silent and plausible: that the
/// silent start-up check can only ever raise a badge, that nothing schedules an unattended update,
/// that the running version is stated rather than inferred, that the wording stays in
/// <c>UpdateStatusUi</c>, and that no certificate thumbprint is ever written into the source.</para>
/// </summary>
public class UpdateWiringSourceTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    /// <summary>
    /// Located from THIS file's compile-time path, not the test assembly's bin directory: the source
    /// isn't copied to the output, and a path relative to bin breaks on any config/TFM change.
    /// </summary>
    private static string Source(params string[] parts)
    {
        var path = Path.Combine([RepoRoot(), .. parts]);
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    /// <summary>Comments stripped, so a property is asserted over code rather than over prose that
    /// happens to name it.</summary>
    private static string CodeOf(params string[] parts) =>
        Regex.Replace(Regex.Replace(Source(parts), @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    // ── The silent check may only raise a badge ─────────────────────────────────

    private static string StartupCheckBody()
    {
        var match = Regex.Match(CodeOf("App.xaml.cs"),
            @"private async Task CheckForUpdatesOnStartupAsync\(\)\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        Assert.True(match.Success,
            "App.CheckForUpdatesOnStartupAsync could not be located, so the rule that the silent check "
          + "only badges the tray is no longer being asserted. Fix this test's pattern, don't skip it.");
        return match.Groups["body"].Value;
    }

    /// <summary>
    /// The start-up check asks what the latest release is and does nothing else with the answer. An
    /// app that downloaded or ran an installer from here would be replacing itself while nobody was
    /// looking, which is the one behaviour this split exists to rule out.
    /// </summary>
    [Fact]
    public void TheStartupCheckOnlyChecks()
    {
        var body = StartupCheckBody();

        Assert.Contains("CheckAsync()", body, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "RunManualAsync", "PrepareAsync", "Launch(", "InstallerPath" })
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
    }

    /// <summary>The badge is the whole of what it may produce, and only for the one outcome that means
    /// a newer release exists.</summary>
    [Fact]
    public void TheStartupCheckRaisesTheBadgeOnlyForAnAvailableUpdate()
    {
        var body = StartupCheckBody();

        Assert.Matches(@"Outcome\s*==\s*(?:ZeroZero\.Update\.)?UpdateCheckOutcome\.UpdateAvailable", body);
        Assert.Contains("SetUpdateBadge", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shared component ships a check scheduler. Starting it would turn the start-up check into a
    /// recurring one, and the manual flow it would drive downloads and runs an installer.
    /// </summary>
    [Theory]
    [InlineData("App.xaml.cs")]
    [InlineData("Services", "AppUpdate.cs")]
    [InlineData("UI", "TrayMenu.cs")]
    public void NothingSchedulesAnUnattendedUpdate(params string[] parts)
    {
        var code = CodeOf(parts);

        Assert.DoesNotContain("UpdateScheduler", code, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateTrigger.Scheduled", code, StringComparison.Ordinal);
    }

    /// <summary>The explicit check is the only path that installs anything, and it runs as a manual
    /// one — the trigger the shared flow reports every outcome for.</summary>
    [Fact]
    public void TheExplicitCheckRunsAsAManualOne() =>
        Assert.Contains("UpdateTrigger.Manual", CodeOf("Services", "AppUpdate.cs"), StringComparison.Ordinal);

    // ── The running version is this app's, not whatever is hosting the code ─────

    /// <summary>
    /// The shared component falls back to the entry assembly's version, which is this library the
    /// moment the code is shared and a test host the moment it is not. Stating it keeps the comparison
    /// the app's own decision.
    /// </summary>
    [Fact]
    public void TheRunningVersionIsStatedRatherThanInferred()
    {
        Assert.Matches(@"new AppUpdate\(\s*\n?\s*System\.Reflection\.Assembly\.GetExecutingAssembly\(\)",
                       CodeOf("App.xaml.cs"));
        // Whitespace-tolerant: the initialiser's column alignment moves whenever a longer name joins it.
        Assert.Matches(@"RunningVersion\s+=\s+runningVersion,",
                       CodeOf("Helpers", "AppUpdateOptions.cs"));
    }

    // ── The wording stays where it can be asserted ──────────────────────────────

    /// <summary>
    /// The shared update window words every step a person sees during an explicit check, so the
    /// prompts this app supplies are an adapter and nothing more. A sentence written here would be
    /// one no test can see and one the window would show beside its own wording for the same
    /// outcome.
    /// </summary>
    [Fact]
    public void ThePromptsSayNothingOfTheirOwn()
    {
        var code = CodeOf("UI", "TrayUpdatePrompts.cs");
        var literals = Regex.Matches(code, @"""[^""]*""").Cast<Match>().Select(m => m.Value).ToArray();

        Assert.True(literals.Length == 0,
            $"UI\\TrayUpdatePrompts.cs carries a string literal ({string.Join(", ", literals)}). The shared "
          + "update window words every outcome a person reads during a check; anything this app still says "
          + "for itself belongs in Helpers\\UpdateStatusUi.cs, where it is asserted without a running app.");
    }

    // ── The dashboard survives a window opened on top of it ─────────────────────

    private static string DashboardActivationBody()
    {
        var match = Regex.Match(CodeOf("UI", "DashboardWindow.xaml.cs"),
            @"private void OnActivated\(object sender, WindowActivatedEventArgs e\)\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        Assert.True(match.Success,
            "DashboardWindow.OnActivated could not be located, so the rule that the dashboard asks before "
          + "hiding itself is no longer being asserted. Fix this test's pattern, don't skip it.");
        return match.Groups["body"].Value;
    }

    /// <summary>
    /// The dashboard hides itself the moment it loses focus, and the shared update window opens in
    /// front of whatever is there. Without the count of this app's own short-lived windows, choosing
    /// "Check for updates" from the dashboard would close the dashboard in the same gesture — the
    /// trap the shared About window answers with the same question. The order is what matters: the
    /// question has to be asked before the window is hidden, not after.
    /// </summary>
    [Fact]
    public void TheDashboardAsksTheSharedCountBeforeHidingItselfOnLostFocus()
    {
        var body  = DashboardActivationBody();
        var asked = body.IndexOf("TransientWindows.AnyOpen", StringComparison.Ordinal);
        var hides = body.IndexOf("HideWindow()", StringComparison.Ordinal);

        Assert.True(asked >= 0,
            "DashboardWindow.OnActivated hides the window on lost focus without asking "
          + "TransientWindows.AnyOpen first, so the dashboard closes the moment the update window opens "
          + "above it.");
        Assert.True(hides > asked,
            "DashboardWindow.OnActivated hides the window before asking TransientWindows.AnyOpen, so the "
          + "question changes nothing.");
    }

    // ── A result this app never reads cannot change what it shows ───────────────

    /// <summary>
    /// The update component appends outcomes as it grows — a download the user stopped is the
    /// newest. This app reads none of them: both call sites discard the run and let the shared
    /// window do the reporting, so an appended member cannot quietly take an arm meant for
    /// something else. The day this app starts matching on the result, this fails, and whoever
    /// wrote the match handles every member including the stopped download.
    /// </summary>
    [Fact]
    public void ThisAppReadsNoUpdateFlowResult()
    {
        Assert.True(Enum.IsDefined(UpdateFlowResult.DownloadCancelled),
            "The update component no longer names the outcome of a download the user stopped, so the "
          + "reporting this app leaves to the shared window has changed shape.");

        foreach (var file in ApplicationSourceFiles())
        {
            var code = Regex.Replace(Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", "", RegexOptions.Singleline),
                                     @"//[^\n]*", "");
            Assert.False(code.Contains("UpdateFlowResult.", StringComparison.Ordinal),
                $"'{Path.GetRelativePath(RepoRoot(), file)}' reads a member of UpdateFlowResult. The component "
              + "appends members — UpdateFlowResult.DownloadCancelled is the newest — so a match over them "
              + "has to name every one, including the ones added after it was written.");
        }
    }

    // ── No thumbprint is ever written down ─────────────────────────────────────

    /// <summary>
    /// The pin is read from the embedded certificate at run time, so a thumbprint literal in the source
    /// would be a second authority — one that goes stale the day the certificate is rotated, and that
    /// silently refuses every release afterwards. Forty hexadecimal digits is a SHA-1 thumbprint and
    /// sixty-four a SHA-256 one; neither belongs in a source file.
    /// </summary>
    [Fact]
    public void NoThumbprintIsHardCodedInTheApplicationSource()
    {
        var hex = new Regex(@"(?<![0-9A-Za-z])(?:[0-9a-f]{40}|[0-9A-F]{40}|[0-9a-f]{64}|[0-9A-F]{64})(?![0-9A-Za-z])");

        foreach (var file in ApplicationSourceFiles())
        {
            var match = hex.Match(File.ReadAllText(file));
            Assert.False(match.Success,
                $"'{Path.GetRelativePath(RepoRoot(), file)}' carries a {match.Length}-digit hexadecimal literal. "
              + "A certificate thumbprint belongs in scripts\\ZeroZeroSoftware.cer, which the build embeds "
              + "and Helpers\\ExpectedPublisher.cs reads — never written out beside it.");
        }
    }

    /// <summary>Every C# file the application itself is built from — build output and this test
    /// assembly left out. One enumeration, so two rules about the application's source cannot come
    /// to disagree about what the application's source is.</summary>
    private static IEnumerable<string> ApplicationSourceFiles() =>
        Directory.EnumerateFiles(RepoRoot(), "*.cs", SearchOption.AllDirectories).Where(f => !IsExcluded(f));

    /// <summary>Build output, and this test assembly, whose fixtures state hashes on purpose.</summary>
    private static bool IsExcluded(string file)
    {
        var relative = Path.GetRelativePath(RepoRoot(), file);
        return relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("Tests" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // ── One certificate, one authority ─────────────────────────────────────────

    /// <summary>
    /// The app's pin, the test assembly's copy of it and the release workflow's verification are the
    /// same file. Two files would be two authorities, and a release signed by one and checked against
    /// the other is refused on every machine that installed it.
    /// </summary>
    [Fact]
    public void TheAppTheTestsAndTheReleaseWorkflowPinTheSameCertificate()
    {
        Assert.Contains(@"Include=""scripts\ZeroZeroSoftware.cer"" LogicalName=""ZeroZeroSoftware.cer""",
                        Source("HyperVManagerTray.csproj"), StringComparison.Ordinal);
        Assert.Contains(@"Include=""..\scripts\ZeroZeroSoftware.cer"" LogicalName=""ZeroZeroSoftware.cer""",
                        Source("Tests", "HyperVManagerTray.Tests.csproj"), StringComparison.Ordinal);
        Assert.Contains(@"scripts\ZeroZeroSoftware.cer",
                        Source(".github", "workflows", "release.yml"), StringComparison.Ordinal);
    }

    /// <summary>The asset name the flow asks the release for is the one the installer is built under.
    /// The shared flow never takes the first executable it finds, so a drift here means every update
    /// reports that the release carries no installer.</summary>
    [Fact]
    public void TheInstallerAssetNameMatchesWhatTheInstallerIsBuiltAs()
    {
        Assert.Contains("OutputBaseFilename=HyperVManagerTray-Setup-{#AppVersion}",
                        Source("installer", "HyperVManagerTray.iss"), StringComparison.Ordinal);
        Assert.Contains(@"AppInfo.Id + ""-Setup-{version}.exe""",
                        Source("Helpers", "AppUpdateOptions.cs"), StringComparison.Ordinal);
    }
}
