using ZeroZero.Primitives;
using ZeroZero.Update;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Everything the shared update flow is told about this product: where the releases are, what the
/// installer asset is called, and who must have signed it. The shared component carries none of its
/// own, so this is the single place any of it is stated.
/// </summary>
internal static class AppUpdateOptions
{
    public const string RepositoryOwner = "0z00z0";
    public const string RepositoryName  = "HyperVManagerTray";

    /// <summary>How long the check may take before it is abandoned. Named because
    /// <see cref="UpdateStatusUi"/> tells the user the number.</summary>
    public const int RequestTimeoutSeconds = 10;

    /// <summary>The release asset the installer must be published under, exactly. The shared flow
    /// never takes the first executable it finds, and this must stay in step with
    /// <c>OutputBaseFilename</c> in <c>installer\HyperVManagerTray.iss</c>.</summary>
    public const string InstallerFileName = AppInfo.Id + "-Setup-{version}.exe";

    /// <summary>Downloads land in a fresh directory of this prefix under the temporary folder, one
    /// per download, so nothing can be planted at the path ahead of time.</summary>
    public const string DownloadDirectoryPrefix = AppInfo.Id + "-update";

    /// <summary>Download directories older than this are swept at start-up.</summary>
    public static readonly TimeSpan StaleDownloadAge = TimeSpan.FromDays(1);

    /// <param name="runningVersion">Stated rather than left to the component, which would otherwise
    /// read the entry assembly. Passing it makes the comparison this app's own decision and keeps a
    /// host with a different entry assembly — a test runner — from standing in for the product.</param>
    public static UpdateOptions For(Version runningVersion, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(runningVersion);
        ArgumentNullException.ThrowIfNull(log);

        return new UpdateOptions
        {
            RepositoryOwner    = RepositoryOwner,
            RepositoryName     = RepositoryName,
            ProductName        = AppInfo.Id,
            RunningVersion     = runningVersion,
            ExpectedSigner     = ExpectedPublisher.Load(),
            DirectoryPrefix    = DownloadDirectoryPrefix,
            InstallerFileName  = InstallerFileName,
            InstallerArguments = InstallerArgumentsFor(runningVersion),
            RequestTimeout     = TimeSpan.FromSeconds(RequestTimeoutSeconds),
            Log                = log,
        };
    }

    /// <summary>The installer asset's name for one release, as the release publishes it.</summary>
    public static string InstallerFileNameFor(string versionText) =>
        InstallerFileName.Replace("{version}", versionText, StringComparison.Ordinal);

    /// <summary>Tells the installer this run is the app's own update, so it waits for the app to exit
    /// by itself before ending it. Read by <c>StartedByTheApplication</c> in
    /// <c>installer\HyperVManagerTray.iss</c> as <c>{param:UPDATEFROMAPP}</c>.</summary>
    public const string StartedByApplicationSwitch = "/UPDATEFROMAPP=1";

    /// <summary>The switch above, plus a request that the installer log the run it makes (issue #91:
    /// diagnosing why the update flow's installer run does not always rewrite the uninstall entry).
    /// The log is named for the version running before the update, so the folder holds one log per
    /// version rather than growing without bound. Never throws — a log that cannot be arranged must
    /// not stop an update.</summary>
    private static string InstallerArgumentsFor(Version runningVersion)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            var path = Path.Combine(AppInfo.DataDir, $"installer-{AppInfo.FormatVersion(runningVersion)}.log");
            return $"{StartedByApplicationSwitch} /LOG=\"{path}\"";
        }
        catch
        {
            return StartedByApplicationSwitch;
        }
    }
}
