using System;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Window-chrome branding for this app's one titled window (issue #36): gives it the product icon in
/// the title bar / taskbar / Alt-Tab, and — on the dark theme — paints the standard title bar the same
/// colour as the <c>MicaBackdrop Kind="BaseAlt"</c> backdrop behind it, so the two stop clashing.
/// The colour choices themselves live in <see cref="TitleBarPalette"/>; this file is only the WinUI glue.
///
/// <para>Only touches the icon and the title-bar colours — never the presenter or the border — so it is
/// safe to call on any window. On a frameless popup the colour half is simply inert.</para>
///
/// <para><b>Every step is independently guarded and nothing throws out to the caller.</b> This runs from
/// <c>SettingsWindow</c>'s constructor, where an escaping exception means the window is never shown —
/// a failure this app has actually hit. Cosmetic chrome is never worth losing the window for, so a
/// failure to set the icon must not also cost the colours (hence the separate try blocks).</para>
/// </summary>
internal static class TitleBarTheme
{
    /// <summary>
    /// Applies the product icon and, when <paramref name="theme"/> resolves to dark, the matching
    /// title-bar colours to <paramref name="appWindow"/>.
    /// </summary>
    /// <param name="theme">
    /// Normally the hosting window's <c>ActualTheme</c>. <see cref="ElementTheme.Default"/> is resolved
    /// against the application's current theme.
    /// </param>
    internal static void Apply(AppWindow? appWindow, ElementTheme theme)
    {
        if (appWindow is null) return;

        ApplyIcon(appWindow);
        ApplyColors(appWindow, theme);
    }

    /// <summary>
    /// Points the window at <c>Assets\AppIcon.ico</c> — the plated product icon.
    ///
    /// <para>Why the .ico and not the new <c>AppIcon.png</c> from issue #35: <see cref="AppWindow.SetIcon(string)"/>
    /// takes an ICO file, and a PNG path simply is not a valid argument — the two assets are not
    /// interchangeable even though they render the same artwork. The .ico is also already this exe's
    /// <c>ApplicationIcon</c>, so using it here makes the title bar match the taskbar button and Alt-Tab
    /// entry the window already had, rather than introducing a second face.</para>
    ///
    /// <para>(The sibling app documents a preference for an UNPLATED icon on its dark title bar. That
    /// reasoning does not transfer: its AppIcon.ico is transparent, whereas this app's product icon IS
    /// the plated one everywhere it appears — exe, taskbar, installer — so the plate is the identity,
    /// not a deviation from it.)</para>
    /// </summary>
    private static void ApplyIcon(AppWindow appWindow)
    {
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(icoPath)) appWindow.SetIcon(icoPath);
        }
        catch (Exception ex)
        {
            AppInfo.AppendCrashLogLine("TitleBarTheme", $"SetIcon: {ex}");
        }
    }

    /// <summary>
    /// Paints the title bar to match the Mica BaseAlt backdrop on the dark theme; leaves the system
    /// default alone otherwise (see <see cref="TitleBarPalette.ForTheme"/> for why light is a no-op).
    /// Gated on <see cref="AppWindowTitleBar.IsCustomizationSupported"/>.
    /// </summary>
    private static void ApplyColors(AppWindow appWindow, ElementTheme theme)
    {
        try
        {
            if (TitleBarPalette.ForTheme(IsDark(theme)) is not { } palette) return;
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;

            var tb = appWindow.TitleBar;
            var bg    = ToColor(palette.Background);
            var fg    = ToColor(palette.Foreground);
            var hover = ToColor(palette.ButtonHover);

            tb.BackgroundColor         = bg;
            tb.InactiveBackgroundColor = bg;
            tb.ForegroundColor         = fg;
            tb.InactiveForegroundColor = fg;

            // The caption-button strip must be painted explicitly: Mica does NOT fill the non-client
            // caption area, so leaving these transparent renders a light strip behind the min/max/close
            // buttons that defeats the point of theming the bar at all.
            tb.ButtonBackgroundColor         = bg;
            tb.ButtonInactiveBackgroundColor = bg;
            tb.ButtonForegroundColor         = fg;
            tb.ButtonHoverForegroundColor    = fg;
            tb.ButtonHoverBackgroundColor    = hover;
        }
        catch (Exception ex)
        {
            AppInfo.AppendCrashLogLine("TitleBarTheme", $"ApplyColors: {ex}");
        }
    }

    /// <summary>
    /// Resolves an <see cref="ElementTheme"/> to dark/light, falling back to the application's current
    /// theme for <see cref="ElementTheme.Default"/> (which, since this app sets no RequestedTheme, is
    /// the OS theme).
    /// </summary>
    private static bool IsDark(ElementTheme theme) => theme switch
    {
        ElementTheme.Dark  => true,
        ElementTheme.Light => false,
        _                  => Application.Current?.RequestedTheme == ApplicationTheme.Dark,
    };

    private static Color ToColor(TitleBarPalette.Rgb rgb) => Color.FromArgb(0xFF, rgb.R, rgb.G, rgb.B);
}
