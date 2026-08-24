using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>The boundary between text the app shows a person and text it hands to a machine. Every test
/// drives real production code under <c>nb-NO</c> and asserts the machine-readable form did not move.
///
/// <para>Only the FORMATTING pins are provable here: dropping the pinned culture from a format call
/// fails a test, dropping it from an int, long or <c>TimeSpan</c> parse does not — none of those three
/// is culture-sensitive for the inputs the app hands them.</para></summary>
public class CultureBoundaryTests
{
    /// <summary>A locale with a comma decimal separator, which turns "3.5" into "3,5" the moment a
    /// call site forgets to pin a culture.</summary>
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

    /// <summary>Proves the switch bites. In globalization-invariant mode, or with <c>nb-NO</c> silently
    /// resolving to the invariant culture, every assertion below passes while testing nothing.</summary>
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

    /// <summary>The persisted form of a config carrying a hand-edited CIDR and an ISO rename stamp.
    /// Asserted on the bytes on disk: the values reaching the serialiser are composed by the
    /// app.</summary>
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

    // ── Uptime ──────────────────────────────────────────────────────────────────

    /// <summary>Uptime is rendered from a stored <c>TimeSpan</c> string onto the dashboard card, so its
    /// wording may not drift with the machine's locale.</summary>
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

    /// <summary>The full round trip: WMI milliseconds → the stored string → the displayed text. The
    /// read must match <c>TimeSpan.ToString()</c>'s form, not the machine's time separators.</summary>
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

    /// <summary>The setting whose removal this file guards. Not observable from inside the test process
    /// — the test assembly has its own globalization mode — so the app's build file is read.</summary>
    [Fact]
    public void TheAppIsNotBuiltInGlobalizationInvariantMode()
    {
        var csproj = Source("HyperVManagerTray.csproj");

        Assert.DoesNotContain("<InvariantGlobalization>true</InvariantGlobalization>", csproj);
        Assert.Contains("<InvariantGlobalization>false</InvariantGlobalization>", csproj);
    }

    /// <summary><c>UI\AdapterRenameFlow.cs</c> stamps the rename date into config.json. WinUI code, not
    /// linked into this assembly, so the pin is read as text.</summary>
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
}
