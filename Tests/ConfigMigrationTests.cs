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

    // ── A failed copy keeps its retry, and says so ────────────────────────────

    /// <summary>
    /// The blank slate must not be written after a failed copy. It would occupy the target, so every
    /// later start would see a config there, report NotNeeded, and never try again — the user's real
    /// rules stranded beside the executable for good, on one transient file lock.
    /// </summary>
    [Fact]
    public void AFailedCopyDoesNotAllowTheBlankSlate()
    {
        Assert.False(ConfigMigration.MayCreateDefault(ConfigMigrationOutcome.Failed));
    }

    /// <summary>Nothing else blocks it: a clean install and a completed copy both leave startup free.</summary>
    [Fact]
    public void EveryOtherOutcomeAllowsTheBlankSlate()
    {
        Assert.True(ConfigMigration.MayCreateDefault(ConfigMigrationOutcome.NotNeeded));
        Assert.True(ConfigMigration.MayCreateDefault(ConfigMigrationOutcome.Copied));
    }

    /// <summary>
    /// The whole point of withholding the blank slate: the next start copies the config this one could
    /// not. Drives the real startup sequence — Run, then the blank-slate write only if
    /// <see cref="ConfigMigration.MayCreateDefault"/> allows it — against the realistic failure, an
    /// upgrade whose legacy file is momentarily locked by a scanner or an editor. The target directory
    /// is perfectly writable throughout, so nothing but the guard keeps the blank slate out of it.
    /// </summary>
    [Fact]
    public void TheCopyIsRetriedAtTheNextStart()
    {
        var legacy  = Path.Combine(TempDir(), "config.json");
        var current = Path.Combine(TempDir(), "config.json");
        File.WriteAllText(legacy, LegacyJson);

        ConfigMigrationOutcome first;
        using (File.Open(legacy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first = ConfigMigration.Run(current, legacy);
            Assert.Equal(ConfigMigrationOutcome.Failed, first);
            if (ConfigMigration.MayCreateDefault(first))
                ConfigManager.CreateDefaultIfMissing(current, NullLogger.Instance);
        }

        // Nothing may occupy the target, or the retry below reports NotNeeded and the config is stranded.
        Assert.False(File.Exists(current));

        var second = ConfigMigration.Run(current, legacy);
        Assert.Equal(ConfigMigrationOutcome.Copied, second);
        Assert.Equal(LegacyJson, File.ReadAllText(current));
    }

    /// <summary>
    /// A failed copy is announced, and names the file that was left behind. Without this the user gets
    /// the blank-slate balloon instead — "a default was created", i.e. told a fresh install happened
    /// while their real settings sit unread.
    /// </summary>
    [Fact]
    public void AFailedCopyIsAnnouncedAndNamesTheOriginal()
    {
        const string legacy = @"C:\Users\someone\AppData\Local\Programs\HyperVManagerTray\config.json";

        var message = ConfigMigration.FailureBalloon(ConfigMigrationOutcome.Failed, legacy);

        Assert.NotNull(message);
        Assert.Contains(legacy, message);
        Assert.Contains("retried", message);
        // Win32 truncates balloon text at 255 characters, so a message that names a path must fit.
        Assert.True(message.Length < 255, $"Balloon text is {message.Length} characters.");
    }

    /// <summary>There is no failure to announce when the copy landed, or was not needed at all.</summary>
    [Fact]
    public void NothingIsAnnouncedWhenTheCopyDidNotFail()
    {
        Assert.Null(ConfigMigration.FailureBalloon(ConfigMigrationOutcome.NotNeeded, @"C:\somewhere\config.json"));
        Assert.Null(ConfigMigration.FailureBalloon(ConfigMigrationOutcome.Copied,    @"C:\somewhere\config.json"));
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}
