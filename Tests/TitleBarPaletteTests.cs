using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The pure title-bar colour decisions (issues #36 and #146): that each bar colour is exactly the
/// backdrop tone it sits above, in both themes and in both of the backdrop's two states, and that the
/// caption-button hover blend is a real source-over composite.
/// </summary>
public class TitleBarPaletteTests
{
    // ── ForTheme ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The dark bar's two backgrounds are the backdrop's own two tones. Drift here shows up as a band
    /// across the top of the window that nothing else reports.
    /// </summary>
    [Fact]
    public void ForTheme_Dark_MatchesBothBackdropTonesExactly()
    {
        var p = TitleBarPalette.ForTheme(isDark: true);

        // The product active tone ProductMicaBackdrop paints the window with.
        Assert.Equal(new TitleBarPalette.Rgb(0x05, 0x19, 0x20), p.Background);
        // SolidBackgroundFillColorBaseAlt (dark) = #0A0A0A — what Mica Alt falls back to with no input.
        Assert.Equal(new TitleBarPalette.Rgb(0x0A, 0x0A, 0x0A), p.InactiveBackground);
        // TextFillColorPrimary (dark) = #FFFFFF.
        Assert.Equal(new TitleBarPalette.Rgb(0xFF, 0xFF, 0xFF), p.Foreground);
    }

    /// <summary>
    /// The light bar is painted too. The system default matched a stock light Mica ground and does not
    /// match the product tone, so leaving light alone would put an unrelated bar over a tinted window.
    /// </summary>
    [Fact]
    public void ForTheme_Light_MatchesBothBackdropTonesExactly()
    {
        var p = TitleBarPalette.ForTheme(isDark: false);

        Assert.Equal(new TitleBarPalette.Rgb(0xD1, 0xE1, 0xE8), p.Background);
        // SolidBackgroundFillColorBaseAlt (light) = #DADADA.
        Assert.Equal(new TitleBarPalette.Rgb(0xDA, 0xDA, 0xDA), p.InactiveBackground);
        // TextFillColorPrimary (light) #E4000000 composited over the active bar: 209 - 209*(228/255) ≈ 22.
        Assert.Equal(new TitleBarPalette.Rgb(0x16, 0x18, 0x19), p.Foreground);
    }

    /// <summary>
    /// The two states must differ, in both themes. A single colour for both is exactly the mistake that
    /// leaves the bar carrying the product tone on a window that has gone grey.
    /// </summary>
    [Fact]
    public void ForTheme_ActiveAndInactive_AreNotTheSameColour()
    {
        foreach (var isDark in new[] { true, false })
        {
            var p = TitleBarPalette.ForTheme(isDark);
            Assert.NotEqual(p.Background, p.InactiveBackground);
        }
    }

    [Fact]
    public void ForTheme_Dark_HoverIsLighterThanBarButStillDark()
    {
        var p = TitleBarPalette.ForTheme(isDark: true);

        // The hover must lift off the bar (feedback) without becoming a bright flash.
        Assert.True(p.ButtonHover.R > p.Background.R, "hover should lift off the title-bar colour");
        Assert.True(p.ButtonHover.R < 0x40, "hover should stay a subtle dark grey, not a bright flash");
        // SubtleFillColorSecondary (dark) #0FFFFFFF over #051920: 5 + (255-5)*(15/255) ≈ 20.
        Assert.Equal(new TitleBarPalette.Rgb(0x14, 0x27, 0x2D), p.ButtonHover);
    }

    [Fact]
    public void ForTheme_Light_HoverIsDarkerThanBarButStillLight()
    {
        var p = TitleBarPalette.ForTheme(isDark: false);

        Assert.True(p.ButtonHover.R < p.Background.R, "hover should darken off the title-bar colour");
        Assert.True(p.ButtonHover.R > 0xB0, "hover should stay a subtle tint, not a dark slab");
        // SubtleFillColorSecondary (light) #09000000 over #d1e1e8: 209 - 209*(9/255) ≈ 202.
        Assert.Equal(new TitleBarPalette.Rgb(0xCA, 0xD9, 0xE0), p.ButtonHover);
    }

    // ── Blend ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Blend_ZeroAlpha_KeepsUnderlyingColour()
    {
        var under = new TitleBarPalette.Rgb(0x0A, 0x14, 0x1E);
        Assert.Equal(under, TitleBarPalette.Blend(under, new TitleBarPalette.Rgb(0xFF, 0xFF, 0xFF), 0x00));
    }

    [Fact]
    public void Blend_FullAlpha_ReplacesWithOverlayColour()
    {
        var over = new TitleBarPalette.Rgb(0xFF, 0x80, 0x00);
        Assert.Equal(over, TitleBarPalette.Blend(new TitleBarPalette.Rgb(0x0A, 0x0A, 0x0A), over, 0xFF));
    }

    [Fact]
    public void Blend_HalfAlpha_LandsMidwayPerChannel()
    {
        var result = TitleBarPalette.Blend(
            new TitleBarPalette.Rgb(0x00, 0x00, 0x00),
            new TitleBarPalette.Rgb(0xFF, 0x40, 0x20),
            0x80);

        // 0x80/255 ≈ 0.502 — each channel lands just over halfway, rounded away from zero.
        Assert.Equal(new TitleBarPalette.Rgb(0x80, 0x20, 0x10), result);
    }

    [Fact]
    public void Blend_DarkeningOverlay_MovesChannelsDown()
    {
        // Blend is directional (under → over), not a max(): a darker overlay must darken.
        var result = TitleBarPalette.Blend(
            new TitleBarPalette.Rgb(0xFF, 0xFF, 0xFF),
            new TitleBarPalette.Rgb(0x00, 0x00, 0x00),
            0x80);

        Assert.True(result.R < 0xFF && result.G < 0xFF && result.B < 0xFF);
    }
}
