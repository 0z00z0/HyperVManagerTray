using System;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// The pure colour arithmetic behind <see cref="TitleBarTheme"/> (issue #36): which title-bar colours
/// a given theme wants, and the caption-button hover blend. Numbers only — no WinUI, no Win32 — so the
/// decisions can be unit-tested without a Windows App SDK runtime (the same split this repo already
/// uses for <see cref="NetworkStatusUi"/> / <see cref="VmStateUi"/> / <see cref="WindowPlacement"/>).
///
/// <para>The values are the Windows App SDK's OWN theme-resource values, read out of the shipped
/// Microsoft.WinUI <c>Themes/generic.xaml</c> rather than eyeballed, so the title bar matches the
/// <c>MicaBackdrop Kind="BaseAlt"</c> the Settings window sets by construction.</para>
/// </summary>
internal static class TitleBarPalette
{
    /// <summary>An opaque 8-bit-per-channel colour. Deliberately not Windows.UI.Color — that type is a
    /// WinRT projection and would drag the App SDK into the (runtime-free) test assembly.</summary>
    internal readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>The three colours a themed title bar needs: the bar itself, its text/glyphs, and the
    /// caption-button hover fill.</summary>
    internal readonly record struct Colors(Rgb Background, Rgb Foreground, Rgb ButtonHover);

    // SolidBackgroundFillColorBaseAlt, "Default" (dark) dictionary = #0A0A0A. This is precisely the
    // colour Mica *Alt* falls back to, which is why it matches the window's BaseAlt backdrop.
    private static readonly Rgb DarkBackground = new(0x0A, 0x0A, 0x0A);

    // TextFillColorPrimary, "Default" (dark) dictionary = #FFFFFF.
    private static readonly Rgb DarkForeground = new(0xFF, 0xFF, 0xFF);

    // SubtleFillColorSecondary, "Default" (dark) dictionary = #0FFFFFFF — i.e. white at alpha 0x0F.
    // That resource IS the hover fill WinUI paints over a dark surface, but AppWindowTitleBar wants an
    // opaque colour, so we composite it ourselves instead of handing the platform an alpha it may ignore.
    private const byte SubtleHoverAlpha = 0x0F;
    private static readonly Rgb SubtleHoverTint = new(0xFF, 0xFF, 0xFF);

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
    /// The title-bar colours for the given theme, or <c>null</c> meaning "leave the system default
    /// alone".
    ///
    /// <para>Light returns <c>null</c> deliberately. The sibling app (ChargeKeeper) hard-codes a dark
    /// title bar, but it can: it forces <c>RequestedTheme="Dark"</c> application-wide, so light never
    /// happens there. THIS app sets no RequestedTheme and follows the system, so unconditionally
    /// painting the bar dark would leave a near-black title bar over a light Mica BaseAlt window for a
    /// user on the OS Light theme — trading one mismatch for another. The stock light title bar already
    /// matches light Mica, so the correct action there is to do nothing.</para>
    /// </summary>
    internal static Colors? ForTheme(bool isDark)
        => isDark
            ? new Colors(
                DarkBackground,
                DarkForeground,
                Blend(DarkBackground, SubtleHoverTint, SubtleHoverAlpha))
            : null;
}
