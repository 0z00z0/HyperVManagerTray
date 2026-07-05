using System.Diagnostics;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Thin wrappers around <see cref="Process.Start(ProcessStartInfo)"/> for the two things the app
/// launches through the shell: opening a path/URL in its default handler, and starting
/// <c>vmconnect.exe</c> for a VM.  Both were previously copy-pasted (with subtly varying
/// try/catch handling) across TrayMenu, DashboardWindow, and AboutWindow.
/// </summary>
internal static class Shell
{
    /// <summary>
    /// Opens a file path or URL with its default handler (<c>UseShellExecute = true</c>).
    /// Returns false instead of throwing if the shell can't launch it.
    /// </summary>
    public static bool Open(string pathOrUrl)
    {
        if (string.IsNullOrEmpty(pathOrUrl)) return false;
        try { Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true }); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Opens Hyper-V's VM Connection (<c>vmconnect.exe</c>) for the given VM on the local host,
    /// warning the user (once) if the Hyper-V tools aren't installed.
    /// </summary>
    public static void OpenVmConnect(string vmName)
    {
        try
        {
            Process.Start(new ProcessStartInfo("vmconnect.exe", $"localhost \"{vmName}\"")
                { UseShellExecute = true });
        }
        catch
        {
            NativeMethods.Warn(
                "Could not open VM Connection.\n\nEnsure Hyper-V Manager tools are installed.",
                AppInfo.Name);
        }
    }
}
