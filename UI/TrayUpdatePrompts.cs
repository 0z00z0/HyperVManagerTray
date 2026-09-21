using HyperVManagerTray.Helpers;
using ZeroZero.Update;
using ZeroZero.Update.Win32;
using ZeroZero.Update.WinUI;

namespace HyperVManagerTray.UI;

/// <summary>
/// What the update flow asks and says: the shared update window, wrapped so this app keeps the two
/// things the window has no way to do for it.
///
/// <para>Every sentence a user reads during an update is the shared component's, in one window that
/// carries the question, the download's progress and the answer. Nothing here words anything —
/// <see cref="UpdateStatusUi"/> keeps this app's own update wording for what the window never
/// shows: the tray badge and the report the next start makes about an unattended update.</para>
/// </summary>
/// <remarks>
/// Every method here must be called on the thread that owns this app's windows, and the flow's own
/// awaits return to it, so the window appears where the check was started.
/// </remarks>
internal sealed class TrayUpdatePrompts : IUpdatePrompts
{
    /// <summary>The window follows the system theme, as every other window of this app does: no
    /// RequestedTheme is set anywhere, so ElementTheme.Default is what light and dark both mean
    /// here. The release notes come from the release body, which is where this app's are.</summary>
    private readonly UpdateWindowPrompts _window =
        new(new UpdateWindowOptions { ApplicationName = AppInfo.Name });

    /// <summary>The version the user last agreed to install, or null. Read when the installer has
    /// started, to record what the unattended update was for. The window reports a choice to the
    /// flow and not to this app, so the choice is caught on its way past.</summary>
    public string? AcceptedVersion { get; private set; }

    public async Task<InstallChoice> AskToInstallAsync(ReleaseInfo release, Version runningVersion)
    {
        ArgumentNullException.ThrowIfNull(release);
        AcceptedVersion = null;

        var choice = await _window.AskToInstallAsync(release, runningVersion);
        if (choice == InstallChoice.Install) AcceptedVersion = release.VersionText;
        return choice;
    }

    public DownloadSurface BeginDownload(ReleaseInfo release) => _window.BeginDownload(release);

    public Task SayUpToDateAsync(Version runningVersion) => _window.SayUpToDateAsync(runningVersion);

    public Task SayNothingReleasedAsync() => _window.SayNothingReleasedAsync();

    public Task SayCheckFailedAsync(UpdateCheckResult result) => _window.SayCheckFailedAsync(result);

    public async Task SayLaunchFailedAsync(PreparedUpdate update, LaunchResult result) =>
        await _window.SayLaunchFailedAsync(update, result);

    public async Task SayCannotInstallAsync(PreparedUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _window.SayCannotInstallAsync(update);

        // After the window has been read, and only where the failure leaves the released file
        // trustworthy. A refusal deliberately does not send the user to fetch by hand the file that
        // was just rejected.
        if (UpdateStatusUi.OffersReleasePageAfter(update.Outcome) && update.Release.HtmlUri is { } page)
            Shell.Open(page.AbsoluteUri);
    }

    public void Dismiss() => _window.Dismiss();
}
