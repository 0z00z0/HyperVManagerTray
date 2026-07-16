using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The pure title-bar colour decisions (issue #36): that the dark bar matches the Mica BaseAlt backdrop
/// exactly, that the light theme is left alone rather than force-darkened, and that the caption-button
/// hover blend is a real source-over composite.
/// </summary>
public class TitleBarPaletteTests
{
    // ── ForTheme ────────────────────────────────────────────────────────────────

    [Fact]
    public void ForTheme_Dark_MatchesMicaBaseAltFallbackExactly()
    {
        var p = TitleBarPalette.ForTheme(isDark: true);

        Assert.NotNull(p);
        // SolidBackgroundFillColorBaseAlt (dark) = #0A0A0A — the colour Mica Alt falls back to, and
        // therefore the one the title bar must be to match the window's BaseAlt backdrop.
        Assert.Equal(new TitleBarPalette.Rgb(0x0A, 0x0A, 0x0A), p!.Value.Background);
        // TextFillColorPrimary (dark) = #FFFFFF.
        Assert.Equal(new TitleBarPalette.Rgb(0xFF, 0xFF, 0xFF), p.Value.Foreground);
    }

    [Fact]
    public void ForTheme_Light_LeavesSystemDefaultAlone()
    {
        // Not a placeholder: this app follows the OS theme (no RequestedTheme), so a hard-coded dark
        // bar would sit over a LIGHT Mica window. Returning null means "don't touch it", and the stock
        // light title bar already matches. Guards against someone "fixing" this into a dark constant.
        Assert.Null(TitleBarPalette.ForTheme(isDark: false));
    }

    [Fact]
    public void ForTheme_Dark_HoverIsLighterThanBarButStillDark()
    {
        var p = TitleBarPalette.ForTheme(isDark: true)!.Value;

        // The hover must be visible against the bar (feedback) without becoming a bright flash.
        Assert.True(p.ButtonHover.R > p.Background.R, "hover should lift off the title-bar colour");
        Assert.True(p.ButtonHover.R < 0x40, "hover should stay a subtle dark grey, not a bright flash");
        // SubtleFillColorSecondary (dark) #0FFFFFFF composited over #0A0A0A: 10 + (255-10)*(15/255) ≈ 24.
        Assert.Equal(new TitleBarPalette.Rgb(0x18, 0x18, 0x18), p.ButtonHover);
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
