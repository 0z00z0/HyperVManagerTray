using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.UI;

/// <summary>
/// The interactive "check for updates" flow, shared by the tray menu and the About window
/// (previously an identical ~55-line block was copy-pasted into both).  Queries GitHub, shows the
/// Win32 update dialog, and either downloads+launches the installer or opens the releases page.
/// </summary>
internal static class UpdatePrompt
{
    /// <summary>
    /// Runs the full check-and-prompt flow.  MUST be awaited on the UI thread: the check itself is
    /// off-thread, but <see cref="NativeMethods.ShowUpdateDialog"/> needs the UI thread's comctl32 v6
    /// activation context (a thread-pool thread throws <c>EntryPointNotFoundException</c>), so this
    /// deliberately does not use <c>ConfigureAwait(false)</c> before showing the dialog.
    /// </summary>
    /// <param name="hwnd">Parent window for the dialog, captured by the caller before awaiting.</param>
    public static async Task RunAsync(UpdateChecker updateChecker, IntPtr hwnd)
    {
        var result  = await updateChecker.CheckAsync();
        var running  = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        if (result.UpdateAvailable)
        {
            bool canDownload = !string.IsNullOrEmpty(result.InstallerUrl);
            var action = NativeMethods.ShowUpdateDialog(
                result.LatestVersion, running,
                result.ReleaseNotes,  AppInfo.Name,
                canDownload,          hwnd);

            switch (action)
            {
                case NativeMethods.UpdateAction.Update:
                    // Download in the background; Inno Setup's CloseApplications=yes restarts us.
                    NativeMethods.Info(
                        // "…", not "..." — the ellipsis character everywhere else in the app (issue #42).
                        $"Downloading v{result.LatestVersion}…\n\nThe installer will launch automatically when ready.",
                        AppInfo.Name);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var path = await updateChecker
                                .DownloadInstallerAsync(result.InstallerUrl)
                                .ConfigureAwait(false);
                            Shell.Open(path);
                        }
                        catch (Exception ex)
                        {
                            NativeMethods.Warn(
                                $"Download failed:\n{ex.Message}\n\nTry updating from the releases page.",
                                AppInfo.Name);
                            Shell.Open(result.ReleasePageUrl);
                        }
                    });
                    break;

                case NativeMethods.UpdateAction.ShowReleases:
                    Shell.Open(result.ReleasePageUrl);
                    break;

                // Cancel — do nothing
            }
        }
        else if (result.LatestVersion == "none")
        {
            NativeMethods.Info("No releases have been published yet.", AppInfo.Name);
        }
        else if (!string.IsNullOrEmpty(result.LatestVersion))
        {
            NativeMethods.Info($"You're on the latest version ({running}).", AppInfo.Name);
        }
        else
        {
            NativeMethods.Warn("Could not check for updates. Check your internet connection.", AppInfo.Name);
        }
    }
}
