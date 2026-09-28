using System;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// The pure colour arithmetic behind <see cref="TitleBarTheme"/> (issues #36 and #146): which
/// title-bar colours a given theme wants, and the caption-button hover blend. Numbers only — no WinUI,
/// no Win32 — so the decisions can be unit-tested without a Windows App SDK runtime (the same split
/// this repo already uses for <see cref="NetworkStatusUi"/> / <see cref="VmStateUi"/> /
/// <see cref="WindowPlacement"/>).
///
/// <para>The bar carries two backgrounds, because the window's own backdrop does: the active tone
/// carries the product colour and everything drops to a flat grey when the window has no input. Each
/// bar colour is exactly the ground it sits above, so the bar and the client area read as one surface
/// in both states rather than as a band across the top.</para>
///
/// <para>The text and hover values are the Windows App SDK's OWN theme-resource values, read out of
/// the shipped Microsoft.WinUI <c>Themes/generic.xaml</c> rather than eyeballed, so the caption buttons
/// behave as they do in every other window.</para>
/// </summary>
internal static class TitleBarPalette
{
    /// <summary>An opaque 8-bit-per-channel colour. Deliberately not Windows.UI.Color — that type is a
    /// WinRT projection and would drag the App SDK into the (runtime-free) test assembly.</summary>
    internal readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>The colours a themed title bar needs: the bar while the window has input, the bar
    /// while it does not, the text and glyphs, and the caption-button hover fill.</summary>
    internal readonly record struct Colors(Rgb Background, Rgb InactiveBackground, Rgb Foreground, Rgb ButtonHover);

    // The backdrop's active tone, derived on the product colour's hue at the dark theme's own
    // lightness. ProductMicaBackdrop paints the window with this, so the bar matching it is what makes
    // the two one surface.
    private static readonly Rgb DarkBackground = new(0x05, 0x19, 0x20);

    // SolidBackgroundFillColorBaseAlt, "Default" (dark) dictionary = #0A0A0A. The colour Mica Alt falls
    // back to when the window has no input, and therefore the bar's own inactive colour.
    private static readonly Rgb DarkInactiveBackground = new(0x0A, 0x0A, 0x0A);

    // TextFillColorPrimary, "Default" (dark) dictionary = #FFFFFF.
    private static readonly Rgb DarkForeground = new(0xFF, 0xFF, 0xFF);

    // The light theme's pair, the same two roles: the backdrop's light active tone, and
    // SolidBackgroundFillColorBaseAlt, "Light" dictionary = #DADADA.
    private static readonly Rgb LightBackground = new(0xD1, 0xE1, 0xE8);
    private static readonly Rgb LightInactiveBackground = new(0xDA, 0xDA, 0xDA);

    // TextFillColorPrimary, "Light" dictionary = #E4000000 — black at alpha 0xE4. AppWindowTitleBar
    // wants an opaque colour, so it is composited here rather than handing the platform an alpha it may
    // ignore. The same value serves both bar states: the two grounds differ by 1.04:1.
    private const byte LightForegroundAlpha = 0xE4;
    private static readonly Rgb Black = new(0x00, 0x00, 0x00);

    // SubtleFillColorSecondary — #0FFFFFFF on dark, #09000000 on light. That resource IS the hover fill
    // WinUI paints over its own surfaces, and it is composited here for the same reason as the text.
    private const byte DarkHoverAlpha = 0x0F;
    private const byte LightHoverAlpha = 0x09;
    private static readonly Rgb White = new(0xFF, 0xFF, 0xFF);

    /// <summary>
    /// Composites <paramref name="over"/> at <paramref name="alpha"/> onto the opaque
    /// <paramref name="under"/> (standard source-over on an opaque backdrop, so the result is opaque).
    /// </summary>
    internal static Rgb Blend(Rgb under, Rgb over, byte alpha)
    {
        static byte Channel(byte under, byte over, byte alpha)
            => (byte)Math.Round(under + ((over - under) * (alpha / 255.0)), MidpointRounding.AwayFromZero);

        return new Rgb(
            Channel(under.R, over.R, alpha),
            Channel(under.G, over.G, alpha),
            Channel(under.B, over.B, alpha));
    }

    /// <summary>
    /// The title-bar colours for the given theme. Both themes are painted: the bar has to follow the
    /// backdrop's own active tone, which the system default does not.
    ///
    /// <para>The hover fill is composited over the ACTIVE background, since a caption button can only be
    /// hovered while the window has input.</para>
    /// </summary>
    internal static Colors ForTheme(bool isDark)
        => isDark
            ? new Colors(
                DarkBackground,
                DarkInactiveBackground,
                DarkForeground,
                Blend(DarkBackground, White, DarkHoverAlpha))
            : new Colors(
                LightBackground,
                LightInactiveBackground,
                Blend(LightBackground, Black, LightForegroundAlpha),
                Blend(LightBackground, Black, LightHoverAlpha));
}
