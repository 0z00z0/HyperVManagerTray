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
    /// The command line <c>vmconnect.exe</c> is started with for the VM with <paramref name="vmId"/> on
    /// the local host, or <c>null</c> when that is not a well-formed VM ID.
    ///
    /// <para>The VM is named by its ID (<c>-G</c>) and never by its name, because two VMs may share a
    /// name. <paramref name="settingsOnly"/> adds <c>/edit</c>, which opens the connection's own settings
    /// dialog — display size, saved credentials and which local resources the session may reach — instead
    /// of the console. Hyper-V's documented switch, and it combines with <c>-G</c>.</para>
    /// </summary>
    internal static string? VmConnectArguments(string vmId, bool settingsOnly)
    {
        // Only a well-formed GUID reaches the command line.
        if (!Guid.TryParse(HostIdentity.Bare(vmId), out var guid)) return null;
        return settingsOnly ? $"localhost -G {guid:D} /edit" : $"localhost -G {guid:D}";
    }

    /// <summary>
    /// Opens Hyper-V's VM Connection (<c>vmconnect.exe</c>) for the VM with <paramref name="vmId"/> on the
    /// local host — its console, or with <paramref name="settingsOnly"/> the connection settings dialog —
    /// warning the user if the Hyper-V tools aren't installed.
    /// </summary>
    /// <param name="vmName">Used in that warning only, so it can say which machine could not be opened.
    /// The launch itself never carries a name.</param>
    public static void OpenVmConnect(string vmId, bool settingsOnly = false, string? vmName = null)
    {
        var arguments = VmConnectArguments(vmId, settingsOnly);
        if (arguments is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("vmconnect.exe", arguments) { UseShellExecute = true });
        }
        catch
        {
            var what    = settingsOnly ? "connection settings" : "connection window";
            var machine = string.IsNullOrWhiteSpace(vmName) ? "this machine" : vmName;
            NativeMethods.Warn(
                $"The {what} could not be opened for {machine}.\n\nEnsure the Hyper-V management tools are installed.",
                AppInfo.Name);
        }
    }
}
