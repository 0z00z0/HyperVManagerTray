using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Tests for the blank-slate config (issue #38): the shipped sample, the self-heal write, and the
/// property that makes the sample safe to ship at all — that it is genuinely inert.
/// </summary>
public class DefaultConfigTests : IDisposable
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly List<string> _tempFiles = [];

    private string TempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvmt_default_{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Git and editors disagree about line endings; the CONTENT is what must match.</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static AppConfig Parse(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, ReadOpts) ?? new AppConfig();

    // ── The sample is the default, and the default is inert ────────────────────

    /// <summary>
    /// The config.json shipped in the repo (and installed with <c>Flags: onlyifdoesntexist</c>) must be
    /// exactly what <see cref="ConfigManager.CreateDefaultIfMissing"/> writes. Two files that mean
    /// "blank slate" but say it differently is how the old sample kept a phantom <c>MyVM</c> in the box.
    /// </summary>
    [Fact]
    public void ShippedSampleMatchesTheDefault()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "ShippedConfig", "config.json");
        Assert.True(File.Exists(shipped), $"The shipped config.json was not copied to the test output ({shipped}).");

        Assert.Equal(Normalize(DefaultConfig.Json), Normalize(File.ReadAllText(shipped)));
    }

    /// <summary>
    /// The regression this issue is really about. The old sample shipped a fake "Office LAN" rule with
    /// MAC AA:BB:CC:DD:EE:FF, and a VM named "MyVM" that exists on no machine — targeted by both the
    /// rule and the fallback. It cost hours of live debugging: a real VM was never switched because the
    /// placeholder was still sitting in the config, and the only symptom was a warning in a log file.
    /// Nothing resembling a placeholder may ever ship in this file again.
    /// </summary>
    [Fact]
    public void ShippedDefaultContainsNoPlaceholderData()
    {
        var cfg = Parse(DefaultConfig.Json);

        Assert.Empty(cfg.Rules);
        Assert.Empty(cfg.VirtualMachines);
        Assert.Empty(cfg.Fallback.TargetVms);

        // Belt and braces: catch a placeholder reintroduced under any shape the model checks above miss
        // (a comment, a new collection, a renamed key).
        foreach (var placeholder in new[] { "MyVM", "Office LAN", "AA:BB:CC:DD:EE:FF", "10.0.0.0/23" })
            Assert.DoesNotContain(placeholder, DefaultConfig.Json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why an empty <c>targetVms</c> is inert rather than merely small: the apply pass iterates the
    /// fallback's target list, and every name it can't find in <c>virtualMachines</c> is a warning. An
    /// empty list iterates nothing, so a fresh install logs nothing — which is the acceptance criterion
    /// "zero warnings across an evaluation cycle". The old sample failed this on both counts: its
    /// fallback targeted MyVM, and MyVM was in no VM list.
    /// </summary>
    [Fact]
    public void DefaultFallbackHasNoTargets()
    {
        var cfg = Parse(DefaultConfig.Json);

        Assert.Empty(cfg.Fallback.TargetVms);
        // Every fallback/rule target must be resolvable in virtualMachines, or the apply pass warns.
        // Vacuously true for the default — which is the point — but this states the rule that keeps it so.
        var known = cfg.VirtualMachines.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(cfg.Fallback.TargetVms.Concat(cfg.Rules.SelectMany(r => r.TargetVms)),
                   name => Assert.Contains(name, known));
    }

    /// <summary>The default must still be a working config: a real fallback switch and Debug logging.</summary>
    [Fact]
    public void DefaultIsUsableAsShipped()
    {
        var cfg = Parse(DefaultConfig.Json);

        Assert.Equal("Default Switch", cfg.Fallback.VirtualSwitch);
        Assert.Equal(LogLevel.Debug, cfg.LogLevel);
        Assert.Empty(cfg.RuleSwitches);
    }

    // ── Self-heal ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A missing config.json is healed, not fatal (issue #38) — the app used to error-box and exit
    /// rather than start into the Settings editor that would have fixed it.
    /// </summary>
    [Fact]
    public void CreateDefaultIfMissingWritesTheDefaultAndReportsIt()
    {
        var path = TempPath();

        Assert.True(ConfigManager.CreateDefaultIfMissing(path, NullLogger.Instance));

        Assert.True(File.Exists(path));
        Assert.Equal(Normalize(DefaultConfig.Json), Normalize(File.ReadAllText(path)));
    }

    /// <summary>
    /// An existing config is never touched — the same promise the installer's
    /// <c>Flags: onlyifdoesntexist</c> makes. If this ever regressed, startup would silently wipe the
    /// user's real rules.
    /// </summary>
    [Fact]
    public void CreateDefaultIfMissingLeavesAnExistingConfigAlone()
    {
        var path = TempPath();
        const string existing = """{ "logLevel": "Warning", "virtualMachines": [ { "name": "Real", "nicName": "Network Adapter" } ] }""";
        File.WriteAllText(path, existing);

        Assert.False(ConfigManager.CreateDefaultIfMissing(path, NullLogger.Instance));
        Assert.Equal(existing, File.ReadAllText(path));
    }

    /// <summary>An unwritable location must not throw — the app carries on and reports it elsewhere.</summary>
    [Fact]
    public void CreateDefaultIfMissingNeverThrows()
    {
        // A path whose "directory" is an existing FILE — Directory.CreateDirectory throws on it.
        var file = TempPath();
        File.WriteAllText(file, "not a directory");

        Assert.False(ConfigManager.CreateDefaultIfMissing(Path.Combine(file, "config.json"), NullLogger.Instance));
    }

    /// <summary>The default a fresh install writes must load cleanly through the real ConfigManager.</summary>
    [Fact]
    public void FreshDefaultLoadsCleanly()
    {
        var path = TempPath();
        ConfigManager.CreateDefaultIfMissing(path, NullLogger.Instance);

        using var mgr = new ConfigManager(path, NullLogger.Instance);

        Assert.True(mgr.LastLoad.Succeeded);
        Assert.Equal(0, mgr.LastLoad.RuleCount);
        Assert.Equal(0, mgr.LastLoad.VmCount);
        Assert.Empty(mgr.Current.Fallback.TargetVms);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        GC.SuppressFinalize(this);
    }
}
