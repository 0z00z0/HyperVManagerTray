namespace HyperVManagerTray.Helpers;

/// <summary>What the one-time config relocation did.</summary>
internal enum ConfigMigrationOutcome
{
    /// <summary>The current location already holds a config, or the legacy one holds none.</summary>
    NotNeeded,
    Copied,
    Failed,
}

/// <summary>
/// Copies config.json from the legacy location beside the executable to <see cref="AppInfo.DataDir"/>
/// (issue #74). Never a move — see <see cref="Run"/>.
/// </summary>
internal static class ConfigMigration
{
    /// <summary>The pre-relocation location: beside the executable.</summary>
    internal static string LegacyPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>
    /// Copies <paramref name="legacyPath"/> to <paramref name="configPath"/> when the latter has no
    /// config yet. A copy, never a move: an install rolled back to an earlier build must still find
    /// the file it reads.
    /// </summary>
    /// <param name="onError">Receives whatever the copy threw — the caller has no logger this early.</param>
    internal static ConfigMigrationOutcome Run(string configPath, string legacyPath, Action<Exception>? onError = null)
    {
        try
        {
            if (File.Exists(configPath) || !File.Exists(legacyPath)) return ConfigMigrationOutcome.NotNeeded;

            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.Copy(legacyPath, configPath, overwrite: false);
            return ConfigMigrationOutcome.Copied;
        }
        catch (Exception ex)
        {
            // Best-effort: a failed copy leaves the legacy file intact, and the retry is preserved by
            // MayCreateDefault keeping the blank slate out of the target.
            onError?.Invoke(ex);
            return ConfigMigrationOutcome.Failed;
        }
    }

    /// <summary>
    /// Whether startup may write the blank-slate default after this outcome. False for
    /// <see cref="ConfigMigrationOutcome.Failed"/>: the blank slate would occupy the target, so
    /// <see cref="Run"/> would report <see cref="ConfigMigrationOutcome.NotNeeded"/> on every later
    /// start and the user's real config would stay beside the executable, unread, for good.
    /// </summary>
    internal static bool MayCreateDefault(ConfigMigrationOutcome outcome) =>
        outcome != ConfigMigrationOutcome.Failed;

    /// <summary>
    /// The tray balloon for a failed copy; null when there is nothing to report. Says the settings were
    /// not lost and that the copy runs again, because the alternative reading — the app came up empty —
    /// is the one the user will otherwise reach. Kept short: Win32 caps balloon text.
    /// </summary>
    internal static string? FailureBalloon(ConfigMigrationOutcome outcome, string legacyPath) =>
        outcome != ConfigMigrationOutcome.Failed ? null
        : $"config.json could not be copied from {legacyPath} — no settings are loaded. "
          + "The original is untouched and the copy is retried at the next start.";
}
