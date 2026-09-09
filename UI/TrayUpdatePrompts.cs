using HyperVManagerTray.Helpers;
using ZeroZero.Update;
using ZeroZero.Update.Win32;

namespace HyperVManagerTray.UI;

/// <summary>
/// What the update flow asks and says, in this app's own dialogs and this app's own words. The
/// shared component ships prompts of its own; they are deliberately not used, because every sentence
/// the user reads about an update is decided in <see cref="UpdateStatusUi"/>, where it is assertable
/// without a running app.
/// </summary>
/// <remarks>
/// Every method here must be called on the UI thread: the task dialog needs that thread's comctl32
/// version 6 activation context, and a thread-pool thread throws
/// <c>EntryPointNotFoundException</c> instead of showing anything.
/// </remarks>
internal sealed class TrayUpdatePrompts : IUpdatePrompts
{
    /// <summary>Parent window for the dialogs, captured by the caller before it awaits. Settable
    /// because one flow instance serves both the tray menu and the About window, and the guard that
    /// keeps two checks from running at once lives in that one instance.</summary>
    public IntPtr Owner { get; set; }

    public InstallChoice AskToInstall(ReleaseInfo release, Version runningVersion)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(runningVersion);

        // The release must carry the installer under exactly the expected name; the shared flow never
        // takes the first executable it finds. Without it the only honest offer is the releases page.
        var installer = AppUpdateOptions.InstallerFileNameFor(release.VersionText);
        var canDownload = release.FindAsset(installer) is not null;

        var action = NativeMethods.ShowUpdateDialog(
            release.VersionText, AppInfo.FormatVersion(runningVersion),
            ReleaseNotesText.Strip(release.Body), AppInfo.Name,
            canDownload, Owner);

        switch (action)
        {
            case NativeMethods.UpdateAction.Update:
                NativeMethods.Info(UpdateStatusUi.DownloadingMessage(release.VersionText), AppInfo.Name);
                return InstallChoice.Install;

            case NativeMethods.UpdateAction.ShowReleases:
                return InstallChoice.OpenReleasePage;

            default:
                return InstallChoice.Later;
        }
    }

    public void SayUpToDate(Version runningVersion) =>
        Say(UpdateStatusUi.ReportFor(
            new UpdateCheckDetail(UpdateCheckReason.UpToDate, 0, string.Empty),
            rateLimitResetsAt: null, AppInfo.FormatVersion(runningVersion), DateTimeOffset.Now));

    public void SayNothingReleased() =>
        Say(UpdateStatusUi.ReportFor(
            new UpdateCheckDetail(UpdateCheckReason.NoReleases, 0, string.Empty),
            rateLimitResetsAt: null, AppInfo.Version, DateTimeOffset.Now));

    public void SayCheckFailed(UpdateCheckResult result) =>
        Say(UpdateStatusUi.ReportFor(result, AppInfo.Version, DateTimeOffset.Now));

    public void SayCannotInstall(PreparedUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Say(UpdateStatusUi.CannotInstallReport(update));

        // Only where the failure leaves the released file trustworthy. A refusal deliberately does
        // not send the user to fetch by hand the file that was just rejected.
        if (UpdateStatusUi.OffersReleasePageAfter(update.Outcome) && update.Release.HtmlUri is { } page)
            Shell.Open(page.AbsoluteUri);
    }

    public void SayLaunchFailed(PreparedUpdate update, LaunchResult result) =>
        Say(UpdateStatusUi.LaunchFailedReport(result));

    private static void Say(UpdateStatusUi.UpdateReport? report)
    {
        if (report is not { } value) return;
        if (value.IsError) NativeMethods.Warn(value.Message, AppInfo.Name);
        else               NativeMethods.Info(value.Message, AppInfo.Name);
    }
}
