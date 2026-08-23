using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Tests for the config relocation (issue #74): where config.json now lives, and the one-time copy out
/// of the app directory. The copy must never become a move — an install rolled back to an earlier build
/// still reads the file beside the executable.
/// </summary>
public class ConfigMigrationTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hvmt_migrate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private const string LegacyJson = """{ "logLevel": "Warning", "virtualMachines": [ { "name": "Real", "nicName": "Network Adapter" } ] }""";
    private const string CurrentJson = """{ "logLevel": "Trace", "virtualMachines": [] }""";

    // ── Where the file lives ──────────────────────────────────────────────────

    /// <summary>The config now sits beside the logs, in the per-user data directory.</summary>
    [Fact]
    public void ConfigPathIsInThePerUserDataDirectory()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HyperVManagerTray", "config.json");

        Assert.Equal(expected, ConfigManager.GetConfigPath());
        Assert.Equal(AppInfo.DataDir, Path.GetDirectoryName(ConfigManager.GetConfigPath()));
    }

    /// <summary>The two locations must be distinct, or the migration below is comparing a file to itself.</summary>
    [Fact]
    public void LegacyPathIsBesideTheExecutableAndNotTheCurrentPath()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "config.json"), ConfigMigration.LegacyPath);
        Assert.NotEqual(ConfigMigration.LegacyPath, ConfigManager.GetConfigPath());
    }

    // ── The copy ──────────────────────────────────────────────────────────────

    /// <summary>An upgrade over a real install: the existing config is carried across verbatim.</summary>
    [Fact]
    public void CopiesTheLegacyConfigWhenTheCurrentLocationHasNone()
    {
        var legacy  = Path.Combine(TempDir(), "config.json");
        var current = Path.Combine(TempDir(), "config.json");
        File.WriteAllText(legacy, LegacyJson);

        Assert.Equal(ConfigMigrationOutcome.Copied, ConfigMigration.Run(current, legacy));

        Assert.Equal(LegacyJson, File.ReadAllText(current));
    }

    /// <summary>The whole point of a copy: rolling back to an earlier build must still find its file.</summary>
    [Fact]
    public void LeavesTheLegacyConfigInPlaceAndUnmodified()
    {
        var legacy  = Path.Combine(TempDir(), "config.json");
        var current = Path.Combine(TempDir(), "config.json");
        File.WriteAllText(legacy, LegacyJson);
        var written = File.GetLastWriteTimeUtc(legacy);

        ConfigMigration.Run(current, legacy);

        Assert.True(File.Exists(legacy));
        Assert.Equal(LegacyJson, File.ReadAllText(legacy));
        Assert.Equal(written, File.GetLastWriteTimeUtc(legacy));
    }

    /// <summary>
    /// Every start after the first. The copy must never run twice: the legacy file is frozen at the
    /// version the last pre-relocation build wrote, so copying it again would silently roll every
    /// setting made since back to that state.
    /// </summary>
    [Fact]
    public void NeverOverwritesAnExistingConfigAtTheCurrentPath()
    {
        var legacy  = Path.Combine(TempDir(), "config.json");
        var current = Path.Combine(TempDir(), "config.json");
        File.WriteAllText(legacy, LegacyJson);
        File.WriteAllText(current, CurrentJson);

        Assert.Equal(ConfigMigrationOutcome.NotNeeded, ConfigMigration.Run(current, legacy));

        Assert.Equal(CurrentJson, File.ReadAllText(current));
    }

    /// <summary>A clean install: nothing to carry across, and nothing created here.</summary>
    [Fact]
    public void DoesNothingWhenThereIsNoLegacyConfig()
    {
        var legacy  = Path.Combine(TempDir(), "config.json");
        var current = Path.Combine(TempDir(), "config.json");

        Assert.Equal(ConfigMigrationOutcome.NotNeeded, ConfigMigration.Run(current, legacy));

        Assert.False(File.Exists(current));
    }

    /// <summary>The copy creates the data directory itself — a first run has nothing there yet.</summary>
    [Fact]
    public void CreatesTheTargetDirectory()
    {
        var legacy  = Path.Combine(TempDir(), "config.json");
        var current = Path.Combine(TempDir(), "nested", "config.json");
        File.WriteAllText(legacy, LegacyJson);

        Assert.Equal(ConfigMigrationOutcome.Copied, ConfigMigration.Run(current, legacy));

        Assert.Equal(LegacyJson, File.ReadAllText(current));
    }

    /// <summary>
    /// The data directory is the app's to create, unlike the app directory the installer always made.
    /// A FileSystemWatcher on a directory that does not exist throws, which would be fatal at startup.
    /// </summary>
    [Fact]
    public void ConfigManagerCreatesTheConfigDirectoryBeforeWatchingIt()
    {
        var path = Path.Combine(TempDir(), "nested", "config.json");

        using var mgr = new ConfigManager(path, NullLogger.Instance);

        Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
    }

    /// <summary>An unwritable target must not throw — startup carries on and reports it in ui.log.</summary>
    [Fact]
    public void ReportsFailureWithoutThrowing()
    {
        var legacy = Path.Combine(TempDir(), "config.json");
        File.WriteAllText(legacy, LegacyJson);

        // A path whose "directory" is an existing FILE — Directory.CreateDirectory throws on it.
        var blocker = Path.Combine(TempDir(), "not-a-directory");
        File.WriteAllText(blocker, "not a directory");

        Exception? seen = null;
        Assert.Equal(ConfigMigrationOutcome.Failed,
                     ConfigMigration.Run(Path.Combine(blocker, "config.json"), legacy, ex => seen = ex));

        Assert.NotNull(seen);
        Assert.Equal(LegacyJson, File.ReadAllText(legacy));
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}
