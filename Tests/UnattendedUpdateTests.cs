using System.Runtime.CompilerServices;
using HyperVManagerTray.Helpers;
using Xunit;
using ZeroZero.Primitives;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The facts the unattended update rests on, none of which fails visibly when it drifts: the record
/// of an attempt is reported once and then gone, the version comparison points the right way, the
/// report fits the balloon that carries it, the installer runs with no wizard and no message box, and
/// the installer script reads the switch and writes the file the app expects. Nothing here runs the
/// installer, and no test in this project can.
/// </summary>
public sealed class UnattendedUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hvmt-unattended-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder; best effort */ }
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    private static string InstallerScript() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "installer", "HyperVManagerTray.iss"));

    /// <summary>
    /// An update that did not land is reported at the next start with Setup's reason, and only once:
    /// a record left behind would report the same failure at every start, and one that is lost says
    /// nothing at all.
    /// </summary>
    [Fact]
    public void AFailedAttempt_IsReportedOnceWithItsReason_ThenCleared()
    {
        UnattendedUpdate.Record(_dir, "2.7.14", DateTimeOffset.UtcNow, NullLogSink.Instance);
        // What the installer script writes when it refuses.
        File.WriteAllText(Path.Combine(_dir, UnattendedUpdate.RefusalFileName), "Setup installed nothing.\r\n");

        var outcome = UnattendedUpdate.Take(_dir, "2.7.13", NullLogSink.Instance);

        Assert.NotNull(outcome);
        Assert.Equal(UpdateVerdict.DidNotComplete, outcome.Verdict);
        Assert.Equal("2.7.14", outcome.TargetVersion);
        Assert.Equal("Setup installed nothing.", outcome.Refusal);
        Assert.Null(UnattendedUpdate.Take(_dir, "2.7.13", NullLogSink.Instance));
        Assert.False(File.Exists(Path.Combine(_dir, UnattendedUpdate.HandoverFileName)));
        Assert.False(File.Exists(Path.Combine(_dir, UnattendedUpdate.RefusalFileName)));
    }

    /// <summary>
    /// The direction is the whole report, and only a real update ever exercises it: inverted, every
    /// successful update would announce a failure and every failure would be announced as a success.
    /// </summary>
    [Fact]
    public void TheVersionComparison_PointsTheRightWay()
    {
        Assert.Equal(UpdateVerdict.DidNotComplete,    UnattendedUpdate.VerdictFor("2.7.14", "2.7.13"));
        Assert.Equal(UpdateVerdict.Installed,         UnattendedUpdate.VerdictFor("2.7.14", "2.7.14"));
        Assert.Equal(UpdateVerdict.Installed,         UnattendedUpdate.VerdictFor("2.7.14", "2.8.0"));
        Assert.Equal(UpdateVerdict.NothingHandedOver, UnattendedUpdate.VerdictFor(null, "2.7.13"));
    }

    /// <summary>
    /// A tray balloon holds 255 characters and drops a longer text whole, so a failure report that
    /// grew past it would be a failure nobody is told about.
    /// </summary>
    [Fact]
    public void TheFailureReport_FitsInABalloon()
    {
        var longest = new UnattendedUpdate.Outcome(UpdateVerdict.DidNotComplete, "10.10.100", "10.10.99",
                                                   new string('x', UnattendedUpdate.MaxRefusalLength));

        var report = UpdateStatusUi.UnattendedOutcomeReport(longest);

        Assert.True(report.IsError);
        Assert.InRange(report.Message.Length, 1, 255);
        Assert.Contains("v10.10.100", report.Message);
        Assert.Contains("installer-10.10.99.log", report.Message);
    }

    /// <summary>
    /// A silent switch without <c>/SUPPRESSMSGBOXES</c> still raises Setup's error boxes, which under a
    /// silent run stand alone with no wizard behind them; without the switch the app passes, the
    /// installer treats the run as a winget one and neither waits for the app nor starts it again.
    /// </summary>
    [Fact]
    public void TheInstaller_RunsSilentlyAsTheAppsOwnUpdate()
    {
        var arguments = AppUpdateOptions.InstallerArgumentsFor(new Version(2, 7, 13));

        foreach (var expected in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                                         AppUpdateOptions.StartedByApplicationSwitch, "/LOG=" })
            Assert.Contains(expected, arguments, StringComparison.Ordinal);
    }

    /// <summary>Neither side can read the other's constants: the script tells this flow apart by the
    /// switch alone, and writes the refusal the next start reads.</summary>
    [Fact]
    public void TheInstallerScript_ReadsTheSwitchAndWritesTheRefusalTheAppExpects()
    {
        var script = InstallerScript();
        var name   = AppUpdateOptions.StartedByApplicationSwitch.TrimStart('/').Split('=')[0];

        Assert.Contains($"{{param:{name}|0}}", script, StringComparison.Ordinal);
        Assert.Contains(@"{userappdata}\" + AppInfo.Id, script, StringComparison.Ordinal);
        Assert.Contains(@"\" + UnattendedUpdate.RefusalFileName, script, StringComparison.Ordinal);
    }
}
