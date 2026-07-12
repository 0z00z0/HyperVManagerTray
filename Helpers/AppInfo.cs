namespace HyperVManagerTray.Helpers;

/// <summary>
/// Single source of truth for the app's display name and the per-user data locations under
/// <c>%APPDATA%</c>.  Previously the folder name "HyperVManagerTray" and the display name
/// "Hyper-V Manager Tray" were hard-coded in App, TrayMenu, StartupManager, UpdateChecker,
/// etc.; centralising them here keeps the log/crash paths consistent and makes a
/// rename a one-line change.
/// </summary>
internal static class AppInfo
{
    /// <summary>Human-readable product name (message-box captions, tooltip header, About window).</summary>
    public const string Name = "Hyper-V Manager Tray";

    /// <summary>Identifier used for the %APPDATA% folder, scheduled task, and HTTP user-agent.</summary>
    public const string Id = "HyperVManagerTray";

    /// <summary>Per-user data directory (<c>%APPDATA%\HyperVManagerTray</c>). Not created by this getter.</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Id);

    /// <summary>Full path of the rolling application log.</summary>
    public static string LogFile => Path.Combine(DataDir, "switcher.log");

    /// <summary>Full path of the crash log written by the global exception handlers.</summary>
    public static string CrashLog => Path.Combine(DataDir, "crash.log");

    /// <summary>
    /// Appends a timestamped, tagged line to <see cref="CrashLog"/>. Never throws — a logging path
    /// must not itself crash the caller (this is used from exit/teardown paths where the DI logger
    /// may already be gone). Shared by App's managed-exception logging and the self-heal watchdog's
    /// lifecycle notes so the two don't carry separate copies of the same append logic.
    /// </summary>
    public static void AppendCrashLogLine(string tag, string message)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(CrashLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{tag}] {message}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
