using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Two values in <c>App.xaml</c> that must not drift, both of which fail silently when they do
/// (issue #146).
///
/// <para><b>The product colour.</b> A resource dictionary cannot read a C# constant, so the palette's
/// steel blue is written out in <c>App.xaml</c> as well as shipping in
/// <c>ZeroZero.Brand.Core</c>. The test project references that package, so the two can be held to
/// each other here rather than by eye. Drift shows up as a window in a colour nobody chose, which no
/// build and no other test would report.</para>
///
/// <para><b>The brand typeface's path.</b> A font URI that resolves to nothing raises nothing at all:
/// XAML falls back to whatever face the machine has installed, so the window looks correct on a
/// machine with Cascadia Mono and wrong on one without. On a package reference the face lands under
/// the brand assembly's own folder and nothing lands at the output root, which is exactly the path
/// this test holds the three URIs to.</para>
/// </summary>
public class BrandPaletteSourceTests
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

    /// <summary>
    /// The dark theme's heading colour is the palette's steel blue itself. The light theme's is a
    /// darker shade of the same hue, because an identity value that reads on a dark ground does not
    /// read on a light one, so only the dark entry can be pinned to the package.
    /// </summary>
    [Fact]
    public void HeadingColour_IsThePalettesSteelBlue()
    {
        var packaged = ZeroZero.Brand.Core.Brand.ColorSteelBlue.ToString()!.Trim();

        var declared = Regex.Match(
            Source("App.xaml"),
            @"<Color\s+x:Key=""ProductHeadingColour""\s*>\s*(#[0-9A-Fa-f]{6})\s*</Color>");

        Assert.True(declared.Success,
            "App.xaml declares no ProductHeadingColour. The headings, the MQTT panel's heading brush "
          + "and the dashboard's section labels all resolve that key, so without it every one of them "
          + "falls back to a stock theme neutral and the windows lose the product colour entirely.");

        Assert.Equal(packaged, declared.Groups[1].Value, ignoreCase: true);
    }

    /// <summary>
    /// All three font keys, and no surviving reference to the output-root path a package reference
    /// never produces.
    /// </summary>
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

        Assert.DoesNotContain("ms-appx:///Assets/Fonts/", xaml, StringComparison.Ordinal);
    }
}
