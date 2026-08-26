using System.Text.Json.Nodes;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Dropping the flat <c>mqtt</c> block an earlier build wrote (issue #75). The settings are discarded,
/// not converted — but it has to be a step of its own, because the alternative is what the app does
/// today: the first save of anything at all serialises the whole document and takes the block, and the
/// broker password with it, in silence.
/// </summary>
public class MqttLegacyRemovalTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    private string TempConfig(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hvmt_mqttlegacy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Fictional throughout — no value here comes from a real config.</summary>
    private const string LegacyJson = """
        {
          "logLevel": "Warning",
          "virtualMachines": [ { "name": "Real", "nicName": "Network Adapter" } ],
          "rules": [],
          "fallback": { "virtualSwitch": "Default Switch", "targetVms": [] },
          "mqtt": {
            "enabled": true,
            "host": "broker.invalid",
            "transport": "Tcp",
            "useTls": false,
            "username": "tray",
            "password": "fictional-secret",
            "discoveryPrefix": "homeassistant",
            "deviceName": "Hyper-V host",
            "nodeId": "hyperv",
            "publishNetwork": true,
            "publishVmState": true,
            "publishVmDiagnostics": false,
            "publishVmMetrics": true,
            "lastGoodEndpoint": { "host": "broker.invalid", "port": 1883 }
          },
          "settingsWindowX": 120
        }
        """;

    private const string CurrentShapeJson = """
        {
          "logLevel": "Debug",
          "mqtt": {
            "settings": { "enabled": true, "host": "broker.invalid", "deviceName": "Hyper-V host" },
            "endpoint": { "host": "broker.invalid", "username": "tray", "port": 1883, "transport": "Tcp" }
          }
        }
        """;

    private sealed record Line(LogLevel Level, string Message);

    private sealed class Recorder : ILogger
    {
        public readonly List<Line> Lines = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
            => Lines.Add(new Line(logLevel, formatter(state, exception)));
    }

    // ── The removal ───────────────────────────────────────────────────────────

    /// <summary>The block goes, an empty section in the current shape takes its place, and nothing the
    /// old one held is left in the file — the password least of all.</summary>
    [Fact]
    public void RemovesTheFlatBlockAndLeavesAnEmptySection()
    {
        var path = TempConfig(LegacyJson);

        Assert.Equal(MqttLegacyRemovalOutcome.Removed, MqttLegacyRemoval.Run(path, NullLogger.Instance));

        var mqtt = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path))!["mqtt"]);
        Assert.Equal(["settings"], mqtt.Select(p => p.Key));
        Assert.Empty(Assert.IsType<JsonObject>(mqtt["settings"]));
    }

    /// <summary>The point of the whole exercise: the credential is gone, deliberately and once, rather
    /// than as a side effect of the next unrelated save.</summary>
    [Fact]
    public void NoLegacyKeyOrValueSurvivesTheRewrite()
    {
        var path = TempConfig(LegacyJson);

        MqttLegacyRemoval.Run(path, NullLogger.Instance);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("fictional-secret", text, StringComparison.Ordinal);
        foreach (var key in MqttLegacyRemoval.LegacyKeys)
            Assert.DoesNotContain($"\"{key}\"", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Only the section is touched. A rewrite that lost a rule, a VM or the window position
    /// would be a far worse bargain than the one the user agreed to.</summary>
    [Fact]
    public void KeepsEverythingElseInTheDocument()
    {
        var path = TempConfig(LegacyJson);

        MqttLegacyRemoval.Run(path, NullLogger.Instance);

        using var mgr = new ConfigManager(path, NullLogger.Instance);
        Assert.Equal(LogLevel.Warning, mgr.Current.LogLevel);
        Assert.Equal("Real", Assert.Single(mgr.Current.VirtualMachines).Name);
        Assert.Equal("Default Switch", mgr.Current.Fallback.VirtualSwitch);
        Assert.Equal(120, mgr.Current.SettingsWindowX);
    }

    /// <summary>What the panel opens onto afterwards: a section that is present, inert, and asking to be
    /// filled in — not a null the consumers have to defend against.</summary>
    [Fact]
    public void TheReplacementLoadsAsConfiguredAndDisabled()
    {
        var path = TempConfig(LegacyJson);

        MqttLegacyRemoval.Run(path, NullLogger.Instance);

        using var mgr = new ConfigManager(path, NullLogger.Instance);
        Assert.NotNull(mgr.Current.Mqtt);
        Assert.NotNull(mgr.Current.Mqtt.Settings);
        Assert.False(mgr.Current.Mqtt.Settings.Enabled);
        Assert.Equal("", mgr.Current.Mqtt.Settings.Host);
        Assert.Equal("", mgr.Current.Mqtt.Settings.Password);
        Assert.Null(mgr.Current.Mqtt.Endpoint);
    }

    // ── Detection is positive ─────────────────────────────────────────────────

    /// <summary>One legacy key at the block's top level is the whole test. Each of them alone must be
    /// enough: a config half-written by an interrupted save is still a config with a password in it.</summary>
    [Theory]
    [InlineData("host")]
    [InlineData("publishNetwork")]
    [InlineData("password")]
    [InlineData("lastGoodEndpoint")]
    [InlineData("nodeId")]
    public void DetectsTheFlatBlockByAnyLegacyKeyAlone(string key)
    {
        var path = TempConfig($$"""{ "mqtt": { "{{key}}": "whatever" } }""");

        Assert.Equal(MqttLegacyRemovalOutcome.Removed, MqttLegacyRemoval.Run(path, NullLogger.Instance));

        Assert.False(Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path))!["mqtt"]).ContainsKey(key));
    }

    /// <summary>A hand-edited config is read case-insensitively everywhere else, so a capitalised
    /// leftover is the same leftover.</summary>
    [Fact]
    public void DetectsAHandEditedKeyWhateverItsCase()
    {
        var path = TempConfig("""{ "mqtt": { "Host": "broker.invalid" } }""");

        Assert.Equal(MqttLegacyRemovalOutcome.Removed, MqttLegacyRemoval.Run(path, NullLogger.Instance));
    }

    /// <summary>The keys the current shape holds are not evidence of anything, in either direction.</summary>
    [Fact]
    public void TheCurrentShapeIsNotLegacy()
    {
        Assert.False(MqttLegacyRemoval.IsLegacy(new JsonObject { ["settings"] = new JsonObject(), ["endpoint"] = new JsonObject() }));
        Assert.False(MqttLegacyRemoval.IsLegacy([]));
        Assert.True(MqttLegacyRemoval.IsLegacy(new JsonObject { ["host"] = "broker.invalid" }));
    }

    // ── Everything else is left exactly as it was ─────────────────────────────

    /// <summary>A config already in the current shape must survive untouched — settings, endpoint
    /// memory and all.</summary>
    [Fact]
    public void LeavesACurrentShapeSectionAlone()
    {
        AssertNotRewritten(CurrentShapeJson);
    }

    /// <summary>The state this removal itself leaves behind, and the state a fresh install reaches the
    /// moment anything MQTT is saved. Detecting by the ABSENCE of the current keys would match it, and
    /// rewrite config.json on every single start.</summary>
    [Fact]
    public void LeavesAnEmptySectionAlone()
    {
        AssertNotRewritten("""{ "logLevel": "Debug", "mqtt": { "settings": {} } }""");
        AssertNotRewritten("""{ "logLevel": "Debug", "mqtt": {} }""");
    }

    /// <summary>The blank slate, and every config written before the section existed.</summary>
    [Fact]
    public void LeavesAConfigWithNoSectionAlone()
    {
        AssertNotRewritten(DefaultConfig.Json);
    }

    /// <summary>A hand-blanked section carries no leftover to remove; ConfigManager.Load repairs the
    /// null to inert defaults on its own.</summary>
    [Fact]
    public void LeavesANullSectionAlone()
    {
        AssertNotRewritten("""{ "logLevel": "Debug", "mqtt": null }""");
    }

    /// <summary>A clean install, at the moment before the blank slate is written.</summary>
    [Fact]
    public void DoesNothingWhenThereIsNoConfigFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hvmt_mqttlegacy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var path = Path.Combine(dir, "config.json");

        Assert.Equal(MqttLegacyRemovalOutcome.NotNeeded, MqttLegacyRemoval.Run(path, NullLogger.Instance));

        Assert.False(File.Exists(path));
    }

    /// <summary>Every start after the first. A removal that ran again would be writing the file for no
    /// reason on every launch for the life of the install.</summary>
    [Fact]
    public void RunningAgainChangesNothing()
    {
        var path = TempConfig(LegacyJson);
        Assert.Equal(MqttLegacyRemovalOutcome.Removed, MqttLegacyRemoval.Run(path, NullLogger.Instance));
        var after = File.ReadAllText(path);
        var written = File.GetLastWriteTimeUtc(path);

        Assert.Equal(MqttLegacyRemovalOutcome.NotNeeded, MqttLegacyRemoval.Run(path, NullLogger.Instance));

        Assert.Equal(after, File.ReadAllText(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    // ── Failure ───────────────────────────────────────────────────────────────

    /// <summary>An unreadable config must not take startup down, and must not be written over: the next
    /// start tries again, and until then the block is still there to remove.</summary>
    [Fact]
    public void ReportsFailureWithoutThrowingAndLeavesTheFile()
    {
        var path = TempConfig(LegacyJson);
        var recorder = new Recorder();

        MqttLegacyRemovalOutcome outcome;
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            outcome = MqttLegacyRemoval.Run(path, recorder);

        Assert.Equal(MqttLegacyRemovalOutcome.Failed, outcome);
        Assert.Equal(LegacyJson, File.ReadAllText(path));
        Assert.Contains(recorder.Lines, l => l.Level == LogLevel.Error);
    }

    /// <summary>A config that does not parse is ConfigManager's to report — but it is emphatically not
    /// this step's to overwrite.</summary>
    [Fact]
    public void DoesNotWriteOverAConfigItCannotParse()
    {
        var path = TempConfig("{ this is not json");

        Assert.Equal(MqttLegacyRemovalOutcome.Failed, MqttLegacyRemoval.Run(path, NullLogger.Instance));

        Assert.Equal("{ this is not json", File.ReadAllText(path));
    }

    // ── The record of it ──────────────────────────────────────────────────────

    /// <summary>The user's broker goes quiet after an upgrade and nothing else in the app explains why.
    /// The line has to be findable in mqtt.log and say what has to be done, or the only route left is
    /// guessing.</summary>
    [Fact]
    public void TheRemovalIsLoggedAsAWarningThatSaysItMustBeEnteredAgain()
    {
        var path = TempConfig(LegacyJson);
        var recorder = new Recorder();

        MqttLegacyRemoval.Run(path, recorder);

        var line = Assert.Single(recorder.Lines);
        // Warning, not Information: a configuration was thrown away, and the level is what decides
        // whether the line is in the file at all on a host running at reduced verbosity.
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("discarded", line.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("again", line.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(path, line.Message, StringComparison.Ordinal);
        // Nothing of what was discarded may be quoted into a log the user is being told to go and read.
        Assert.DoesNotContain("fictional-secret", line.Message, StringComparison.Ordinal);
    }

    /// <summary>Nothing was removed, so there is nothing to say. A line every start would train the
    /// reader to skip the one start where it mattered.</summary>
    [Fact]
    public void NothingIsLoggedWhenThereWasNothingToRemove()
    {
        var path = TempConfig(CurrentShapeJson);
        var recorder = new Recorder();

        MqttLegacyRemoval.Run(path, recorder);

        Assert.Empty(recorder.Lines);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Asserts the file is reported as needing nothing and is not written at all — content and
    /// timestamp both, since "left untouched" is the claim being made.</summary>
    private void AssertNotRewritten(string json)
    {
        var path = TempConfig(json);
        var written = File.GetLastWriteTimeUtc(path);

        Assert.Equal(MqttLegacyRemovalOutcome.NotNeeded, MqttLegacyRemoval.Run(path, NullLogger.Instance));

        Assert.Equal(json, File.ReadAllText(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}
