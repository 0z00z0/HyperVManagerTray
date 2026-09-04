using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

public class AppInfoTests
{
    // ── FormatVersion: pure formatting used for the About window and the tray hover tooltip ────

    [Fact]
    public void FormatVersion_ThreeComponentVersion_ReturnsThreeComponentString()
        => Assert.Equal("2.3.0", AppInfo.FormatVersion(new Version(2, 3, 0)));

    [Fact]
    public void FormatVersion_FourComponentVersion_TruncatesToThreeComponents()
        => Assert.Equal("2.3.0", AppInfo.FormatVersion(new Version(2, 3, 0, 42)));

    [Fact]
    public void FormatVersion_ZeroVersion_ReturnsZeroDotZeroDotZero()
        => Assert.Equal("0.0.0", AppInfo.FormatVersion(new Version(0, 0, 0)));

    [Fact]
    public void FormatVersion_NullVersion_ReturnsUnknown()
        => Assert.Equal("unknown", AppInfo.FormatVersion(null));

    // ── FormatStartupVersionLine: the build-identifying line written first in every run (#93) ──

    /// <summary>
    /// The informational version is what makes a build identifiable beyond four components (it carries
    /// the source revision), so it must lead. Reading the assembly version instead is the defect this
    /// line exists to remove.
    /// </summary>
    [Fact]
    public void FormatStartupVersionLine_PrefersTheInformationalVersion()
    {
        var line = AppInfo.FormatStartupVersionLine("2.7.4+9a1c3f7", new Version(2, 7, 4, 0));
        Assert.Contains("2.7.4+9a1c3f7", line);
    }

    /// <summary>Both versions appear: the update check and the installer compare the assembly one.</summary>
    [Fact]
    public void FormatStartupVersionLine_AlsoNamesTheAssemblyVersion()
    {
        var line = AppInfo.FormatStartupVersionLine("2.7.4+9a1c3f7", new Version(2, 7, 4, 0));
        Assert.Contains("2.7.4.0", line);
    }

    /// <summary>
    /// A build with no <c>AssemblyInformationalVersionAttribute</c> must still identify itself, rather
    /// than logging a blank where the version belongs.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FormatStartupVersionLine_NoInformationalVersion_FallsBackToTheAssemblyVersion(string? informational)
    {
        var line = AppInfo.FormatStartupVersionLine(informational, new Version(2, 7, 4, 0));
        Assert.Equal($"{AppInfo.Name} 2.7.4.0 starting (assembly 2.7.4.0)", line);
    }

    /// <summary>Neither version available — the line still says which app started, and says so honestly.</summary>
    [Fact]
    public void FormatStartupVersionLine_NoVersionMetadata_ReportsUnknown()
    {
        var line = AppInfo.FormatStartupVersionLine(null, null);
        Assert.Equal($"{AppInfo.Name} unknown starting (assembly unknown)", line);
    }

    /// <summary>The product name anchors the line, so a log search finds it without knowing the version.</summary>
    [Fact]
    public void FormatStartupVersionLine_NamesTheProduct()
        => Assert.StartsWith(AppInfo.Name, AppInfo.FormatStartupVersionLine("2.7.4", new Version(2, 7, 4, 0)));

    /// <summary>
    /// The running assembly's own line, as the app will actually log it: proves the attribute lookup and
    /// the formatting are wired together, not just that the formatter works on hand-made input.
    /// </summary>
    [Fact]
    public void StartupVersionLine_ForTheRunningAssembly_IsNeitherBlankNorUnknown()
    {
        var line = AppInfo.StartupVersionLine;
        Assert.StartsWith(AppInfo.Name, line);
        Assert.DoesNotContain("unknown", line);
    }
}
