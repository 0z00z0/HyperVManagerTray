using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The three font keys in <c>App.xaml</c> must name the path the brand package actually carries.
///
/// <para><b>Why this is worth a test.</b> A font URI that resolves to nothing raises nothing at all:
/// XAML falls back to whatever face the machine has installed, so the window looks correct on a machine
/// with Cascadia Mono and wrong on one without — a defect that no build, no other test and no glance at
/// a developer machine reports. On a package reference the face lands under the brand assembly's own
/// folder, and the output root holds no <c>Assets\Fonts\</c> at all.</para>
/// </summary>
public class BrandFontPathTests
{
    /// <summary>The path the brand package's own markup addresses the bundled face by.</summary>
    private const string BrandFontUri =
        "ms-appx:///ZeroZero.Brand.WinUI/Assets/Fonts/CascadiaMono.ttf#Cascadia Mono";

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    private static string Source(params string[] parts)
    {
        var path = Path.Combine([RepoRoot(), .. parts]);
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    [Fact]
    public void EveryFontKey_NamesThePathThePackageCarries()
    {
        var xaml = Source("App.xaml");

        foreach (var key in new[] { "BrandFont", "ContentControlThemeFontFamily", "ControlContentThemeFontFamily" })
        {
            var declared = Regex.Match(xaml, @"<FontFamily\s+x:Key=""" + key + @"""\s*>([^<]+)</FontFamily>");
            Assert.True(declared.Success, $"App.xaml declares no FontFamily for '{key}'.");
            Assert.Equal(BrandFontUri, declared.Groups[1].Value.Trim());
        }
    }

    /// <summary>
    /// The negative half. Without it the test above passes while a fourth declaration, or a reinstated
    /// old one, still names the output root — which is the path that silently resolves to nothing.
    /// </summary>
    [Fact]
    public void NoFontKey_NamesTheOutputRootPath()
    {
        Assert.DoesNotContain("ms-appx:///Assets/Fonts/", Source("App.xaml"), StringComparison.Ordinal);
    }
}
