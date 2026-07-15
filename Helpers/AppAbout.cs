using ZeroZero.Brand.Core;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Single source of truth for the app's <see cref="AboutInfo"/> — name, version, description, repo URL
/// and the external-libraries credits list. Both the tray "About…" window and the Settings → About
/// window build their <see cref="BrandAboutOptions"/> from this, so the six-entry credits list (a
/// maintenance trap that had already drifted between the two call sites) lives in exactly one place
/// (cleanup 10). Keep this list in sync with the csproj package references and the README "External
/// libraries" table.
/// </summary>
internal static class AppAbout
{
    private const string Description =
        "Automatically connects Hyper-V VMs to the right virtual switch when the host changes networks. " +
        "Manage VM power and state directly from the system tray.";

    private const string RepoUrl = "https://github.com/0z00z0/HyperVManagerTray";

    /// <summary>Builds a fresh <see cref="AboutInfo"/> (a new instance each call, so a window can't
    /// mutate a shared one).</summary>
    public static AboutInfo CreateInfo() => new()
    {
        AppName     = AppInfo.Name,
        Version     = AppInfo.Version,
        Description = Description,
        RepoUrl     = RepoUrl,
        // Every third-party runtime package the app references. H.NotifyIcon.WinUI is the only
        // non-Microsoft dependency; the Microsoft packages ship under the Microsoft Software Licence
        // Terms (the WinAppSDK *source* is MIT on GitHub).
        ExternalLibraries =
        [
            new ExternalLibrary("Microsoft.WindowsAppSDK", "Microsoft", "WinUI 3 framework (windowing, XAML, Mica)", "MS-EULA", "https://github.com/microsoft/WindowsAppSDK"),
            new ExternalLibrary("Microsoft.Windows.SDK.BuildTools", "Microsoft", "Windows SDK build tooling for the App SDK", "MS-EULA", "https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools"),
            new ExternalLibrary("H.NotifyIcon.WinUI", "HavenDV", "System-tray icon + native context menu for WinUI 3", "MIT", "https://github.com/HavenDV/H.NotifyIcon"),
            new ExternalLibrary("System.Drawing.Common", "Microsoft", "Renders the tray .ico at runtime", "MIT", "https://www.nuget.org/packages/System.Drawing.Common"),
            new ExternalLibrary("Microsoft.Extensions.Logging", "Microsoft", "Logging abstraction; output goes to a small custom file sink", "MIT", "https://www.nuget.org/packages/Microsoft.Extensions.Logging"),
            new ExternalLibrary("System.Management", "Microsoft", "WMI access (root\\virtualization\\v2) for VM status/power", "MIT", "https://www.nuget.org/packages/System.Management"),
        ],
    };
}
