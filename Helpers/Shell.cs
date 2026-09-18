using System.Diagnostics;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Thin wrappers around <see cref="Process.Start(ProcessStartInfo)"/> for the two things the app
/// launches through the shell: opening a path/URL in its default handler, and starting
/// <c>vmconnect.exe</c> for a VM.  Both were previously copy-pasted (with subtly varying
/// try/catch handling) across TrayMenu, DashboardWindow, and the shared About window.
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
    /// Opens a path in its default handler; if that fails, reveals it in Explorer (<c>/select</c>).
    /// Centralises the "open, else reveal" pattern that was inlined in the Settings window.
    /// </summary>
    public static void OpenOrReveal(string path)
    {
        if (Open(path)) return;
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Opens Hyper-V's VM Connection (<c>vmconnect.exe</c>) for the VM with <paramref name="vmId"/> on the
    /// local host — by its ID (<c>-G</c>), since a name may be shared by two VMs — warning the user if the
    /// Hyper-V tools aren't installed.
    /// </summary>
    public static void OpenVmConnect(string vmId)
    {
        // Only a well-formed GUID reaches the command line.
        if (!Guid.TryParse(HostIdentity.Bare(vmId), out var guid)) return;
        try
        {
            Process.Start(new ProcessStartInfo("vmconnect.exe", $"localhost -G {guid:D}")
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
