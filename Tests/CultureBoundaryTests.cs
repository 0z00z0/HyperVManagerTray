using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Xunit;
using ZeroZero.Mqtt.HomeAssistant;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The boundary between text the app shows a person and text it hands to a machine (issue #61).
///
/// <para>The app no longer runs in globalization-invariant mode, so <see cref="CultureInfo.CurrentCulture"/>
/// is the machine's and every unqualified <c>ToString</c>/<c>Parse</c> follows it. That is what the UI
/// wants and what config.json, MQTT topics and payloads, log lines and the crash-stamp file must never
/// do: a number written with a decimal comma into a JSON config or an MQTT payload is a corrupt value,
/// not a localised one. Every test below drives real production code under <c>nb-NO</c> — a locale with
/// a comma decimal separator and a U+2212 negative sign — and asserts the machine-readable form did not
/// move.</para>
///
/// <para><b>Why these cannot be assumed.</b> Invariant mode used to make all of this true by accident:
/// there was no other culture for a call site to pick up. Removing it makes each pin load-bearing, and
/// none of them is visible in a run under the developer's own locale. <see cref="TheHarnessItselfIsHonest"/>
/// exists so a failure to enter <c>nb-NO</c> at all cannot let the rest pass vacuously.</para>
///
/// <para><b>What these tests do NOT prove.</b> Only the FORMATTING pins are provable here. Deleting the
/// pinned culture from <c>MqttEntitySet.Number</c>, <c>LatencyLog</c> or the topic slug fails a test
/// below; deleting it from an integer, long or <c>TimeSpan</c> PARSE does not, because — measured over
/// all 889 cultures this runtime carries — none of those three parses is culture-sensitive for the
/// inputs the app hands them. Those pins are stated intent at a boundary, not a fix for observable
/// behaviour, and the tests over them are ordinary regression pins on the parse rather than proof the
/// pin is honoured. The one genuine divergence found was the negative sign (57 Arabic-script cultures
/// reject an ASCII "-1"), which no value the app parses can reach.</para>
/// </summary>
public class CultureBoundaryTests
{
    /// <summary>A locale with a comma decimal separator — the realistic one on this app's machines,
    /// and the one that turns "3.5" into "3,5" the moment a call site forgets to pin a culture.</summary>
    private const string CommaDecimalLocale = "nb-NO";

    /// <summary>Runs <paramref name="body"/> with the thread's culture switched, and always puts it
    /// back. <see cref="CultureInfo.CurrentCulture"/> is per-thread, so this cannot leak into tests
    /// running in parallel on other threads — provided <paramref name="body"/> stays synchronous.</summary>
    private static void InCulture(string name, Action body)
    {
        var culture   = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture   = new CultureInfo(name);
            CultureInfo.CurrentUICulture = new CultureInfo(name);
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture   = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    // ── The harness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Proves the switch actually bites before anything else relies on it. If the process were ever
    /// put back into globalization-invariant mode, or <c>nb-NO</c> silently resolved to the invariant
    /// culture, every assertion below would pass while testing nothing at all.
    /// </summary>
    [Fact]
    public void TheHarnessItselfIsHonest() => InCulture(CommaDecimalLocale, () =>
    {
        Assert.Equal(CommaDecimalLocale, CultureInfo.CurrentCulture.Name);
        Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
        // The exact mistake every pin below exists to prevent, demonstrated on an unpinned call.
        Assert.Equal("3,5", 3.5.ToString());
    });

    // ── config.json ─────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The persisted form of a config carrying a hand-edited CIDR and an ISO rename stamp. System.Text.Json
    /// is invariant by construction, but the values reaching it are composed by the app, so the assertion
    /// is on the bytes on disk rather than on the serialiser's reputation.
    /// </summary>
    [Fact]
    public void AConfigWrittenUnderACommaLocale_KeepsItsIsoDatesAndDottedNumbers() =>
        InCulture(CommaDecimalLocale, () =>
        {
            var config = new AppConfig
            {
                Rules =
                [
                    new NetworkRule
                    {
                        Name          = "Home",
                        Priority      = 10,
                        VirtualSwitch = "Bridged",
                        Conditions    = new RuleConditions { IpCidr = "10.0.0.0/23" },
                    },
                ],
                AdapterNames =
                [
                    new AdapterNameOverride
                    {
                        DeviceInstanceId     = @"PCI\VEN_8086&DEV_15F3\3&11583659&0&FE",
                        OriginalFriendlyName = "Intel(R) Ethernet Controller I225-V",
                        CurrentFriendlyName  = "Desk (wired)",
                        RenamedOn            = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    },
                ],
            };

            var json = JsonSerializer.Serialize(config, WriteOpts);

            Assert.Contains("\"priority\": 10", json);
            Assert.Contains("\"ipCidr\": \"10.0.0.0/23\"", json);
            Assert.Matches("\"renamedOn\": \"[0-9]{4}-[0-9]{2}-[0-9]{2}\"", json);
            // No decimal comma anywhere: a digit-comma-digit run is the signature of a locale that
            // leaked into a persisted value.
            Assert.DoesNotMatch("[0-9],[0-9]", json);
        });

    /// <summary>A CIDR read back out of config.json must select the same hosts whatever the machine's
    /// locale does to digits — this is the rule that decides which virtual switch a VM lands on.</summary>
    [Theory]
    [InlineData("10.0.0.5",  "10.0.0.0/24", true)]
    [InlineData("10.0.1.5",  "10.0.0.0/24", false)]
    [InlineData("10.0.1.5",  "10.0.0.0/23", true)]
    [InlineData("192.168.1.9", "192.168.0.0/16", true)]
    public void ACidrFromConfig_MatchesTheSameHostsUnderACommaLocale(string ip, string cidr, bool expected) =>
        InCulture(CommaDecimalLocale, () =>
            Assert.Equal(expected, AdapterMatcher.IsInCidr(System.Net.IPAddress.Parse(ip), cidr)));

    /// <summary>The Settings-side validation of the same value. A prefix that stopped parsing would
    /// reject a rule the user already has saved.</summary>
    [Theory]
    [InlineData("10.0.0.0/23", true)]
    [InlineData("10.0.0.0/32", true)]
    [InlineData("10.0.0.0/0",  true)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("10.0.0.0/-1", false)]
    public void ACidrPrefix_ValidatesTheSameUnderACommaLocale(string cidr, bool expected) =>
        InCulture(CommaDecimalLocale, () => Assert.Equal(expected, SettingsOptions.IsValidCidr(cidr)));

    // ── MQTT topics and payloads ────────────────────────────────────────────────

    /// <summary>A topic segment is an identity: the same VM must own the same topic on every machine,
    /// so the slug may not acquire a locale's casing rules. <c>tr-TR</c> is the classic breaker — its
    /// lower-case of 'I' is 'ı', which is not an ASCII letter and would be dropped.</summary>
    [Theory]
    [InlineData("DevBox",        "devbox")]
    [InlineData("VM-01 (Test)",  "vm_01_test")]
    [InlineData("IIS Front-End", "iis_front_end")]
    public void AVmTopicSlug_IsTheSameInEveryLocale(string vmName, string expected)
    {
        Assert.Equal(expected, MqttObjectIds.Slug(vmName));
        InCulture(CommaDecimalLocale, () => Assert.Equal(expected, MqttObjectIds.Slug(vmName)));
        InCulture("tr-TR",            () => Assert.Equal(expected, MqttObjectIds.Slug(vmName)));
    }

    /// <summary>
    /// THE payload test. Home Assistant parses a numeric sensor state as a JSON number; "1536,5" is
    /// not one, and the entity goes unavailable. Both fractional metrics are asserted, because a
    /// rounding call site is exactly where a forgotten culture hides.
    /// </summary>
    [Fact]
    public void NumericMqttPayloads_UseADotUnderACommaLocale() => InCulture(CommaDecimalLocale, () =>
    {
        var set = BuildEntitySet(new VmStatus
        {
            Name        = "DevBox",
            State       = "Running",
            Switch      = "Bridged",
            Cpu         = 7,
            MemAssigned = (long)(1536.5 * 1024 * 1024),
            MemMax      = 8L * 1024 * 1024 * 1024,
            VhdBytes    = (long)(64.25 * 1024 * 1024 * 1024),
            Uptime      = "1.03:14:00",
        });

        Assert.Equal("7",     Payload(set, "vm_devbox_cpu"));
        Assert.Equal("1536.5", Payload(set, "vm_devbox_memory"));
        Assert.Equal("64.25",  Payload(set, "vm_devbox_vhd"));
    });

    /// <summary>Uptime is published as a sensor state as well as shown on the dashboard card, so its
    /// wording is a protocol value and may not drift with the machine's locale.</summary>
    [Theory]
    [InlineData("00:47:00",   "47m")]
    [InlineData("03:14:00",   "3h 14m")]
    [InlineData("1.03:14:00", "1d 3h")]
    public void FormattedUptime_IsTheSameUnderACommaLocale(string raw, string expected)
    {
        var status = new VmStatus { Name = "DevBox", State = "Running", Uptime = raw };
        Assert.Equal(expected, UptimeFormatter.Format(status));
        InCulture(CommaDecimalLocale, () => Assert.Equal(expected, UptimeFormatter.Format(status)));
    }

    /// <summary>The full round trip: WMI milliseconds → the stored string → the published text. The
    /// write is <c>TimeSpan.ToString()</c>, whose form is culture-independent, and the read must match
    /// it rather than the machine's time separators.</summary>
    [Fact]
    public void UptimeSurvivesTheRoundTripUnderACommaLocale() => InCulture(CommaDecimalLocale, () =>
    {
        string stored = WmiVmMapper.UptimeString(uptimeMs: (3 * 3600 + 14 * 60) * 1000UL);

        Assert.Equal("03:14:00", stored);
        Assert.Equal("3h 14m",
            UptimeFormatter.Format(new VmStatus { Name = "DevBox", State = "Running", Uptime = stored }));
    });

    // ── Log lines ───────────────────────────────────────────────────────────────

    /// <summary>A log line is read by tooling and by a person comparing two runs; a decimal comma in
    /// one of them makes the pair incomparable.</summary>
    [Fact]
    public void LatencyLines_KeepADotUnderACommaLocale() => InCulture(CommaDecimalLocale, () =>
    {
        Assert.Equal("3.5 ms",  LatencyLog.FormatMs(3.5));
        Assert.Equal("1235 ms", LatencyLog.FormatMs(1234.7));
        Assert.Equal("148 MB",  LatencyLog.FormatMb(148L * 1024 * 1024));
        Assert.Equal("4.2 h",   LatencyLog.FormatGap(TimeSpan.FromHours(4.2)));

        Assert.Contains("menu rebuild 1.3 ms",
            LatencyLog.RightClickLine(1.3, 15L * 1024 * 1024, TimeSpan.FromHours(4.2)));
        Assert.Contains("at 2100 ms since process start",
            LatencyLog.StartupLine("tray icon visible", 2100, autoRelaunch: false));
    });

    // ── Call sites no test can reach ────────────────────────────────────────────

    /// <summary>
    /// The setting whose removal this whole file guards. Invariant mode makes every Task Scheduler
    /// write throw (<c>Trigger</c>'s static initialiser calls <c>CreateSpecificCulture("en")</c>, and
    /// <c>TaskFolder.RegisterTaskDefinition</c> reaches it), and it pins displayed numbers to the
    /// invariant culture. It cannot be observed from inside the test process — the test assembly has
    /// its own globalization mode — so the app's build file is read instead.
    /// </summary>
    [Fact]
    public void TheAppIsNotBuiltInGlobalizationInvariantMode()
    {
        var csproj = Source("HyperVManagerTray.csproj");

        Assert.DoesNotContain("<InvariantGlobalization>true</InvariantGlobalization>", csproj);
        Assert.Contains("<InvariantGlobalization>false</InvariantGlobalization>", csproj);
    }

    /// <summary>
    /// <c>UI\AdapterRenameFlow.cs</c> stamps the rename date straight into config.json. It is WinUI
    /// code and is deliberately not linked into this assembly, so the pin is read as text — coarse,
    /// but aimed at the realistic regression: the format argument being dropped during an edit.
    /// </summary>
    [Fact]
    public void TheRenameStampIsWrittenWithAPinnedCulture()
    {
        var source = Source("UI", "AdapterRenameFlow.cs");

        Assert.Contains(
            "DateTime.Now.ToString(\"yyyy-MM-dd\", CultureInfo.InvariantCulture)", source);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    /// <summary>Located from THIS file's compile-time path: the sources are not copied to the test
    /// output.</summary>
    private static string Source(params string[] parts)
    {
        var path = Path.Combine([RepoRoot(), .. parts]);
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    private static HaEntitySet BuildEntitySet(VmStatus status)
    {
        var state = new MqttStateCache();
        state.SetVms([status]);

        return MqttEntitySet.Build(new MqttEntitySpec
        {
            VmNames              = [status.Name],
            RuleSwitches         = ["Bridged"],
            State                = state,
            VmIp                 = _ => null,
            PublishMetrics       = () => true,
            ReCheckNetwork       = _ => Task.CompletedTask,
            RepairHostNetworking = _ => Task.CompletedTask,
            Power                = (_, _, _) => Task.CompletedTask,
            OverrideSwitch       = (_, _, _) => Task.CompletedTask,
            Refuse               = _ => { },
        });
    }

    private static string? Payload(HaEntitySet set, string objectId) =>
        set.All.Single(e => e.ObjectId == objectId).Payload();
}
