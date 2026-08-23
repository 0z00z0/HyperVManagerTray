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
/// Moves config.json from the legacy location beside the executable to <see cref="AppInfo.DataDir"/>
/// (issue #74).
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
            // Best-effort: a failed copy leaves the legacy file intact and startup writes a blank slate.
            onError?.Invoke(ex);
            return ConfigMigrationOutcome.Failed;
        }
    }
}
