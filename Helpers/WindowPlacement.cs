namespace HyperVManagerTray.Helpers;

/// <summary>A window rectangle in physical screen pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct WindowRect(int X, int Y, int Width, int Height);

/// <summary>
/// Pure, WinUI-free window-placement and settings-row-layout arithmetic (issue #31). Everything here
/// is a function of numbers only — no Win32, no XAML — so the load-bearing decisions (is the saved
/// rect usable? does it land on a connected monitor? must this row stack?) are unit-testable without
/// a GUI, in the same style as <see cref="SettingsOptions"/> and <see cref="AdapterNameRules"/>.
///
/// <para><see cref="NativeMethods.ClampRectToNearestMonitor"/> supplies the monitor's work area and
/// delegates the maths here; <c>SettingsWindow</c> supplies the measured widths.</para>
/// </summary>
public static class WindowPlacement
{
    // ── Settings window sizing (device-independent pixels; callers scale by monitor DPI) ─────────
    //
    // The numbers below are derived from the window's real content, not picked round. Fixed chrome
    // that every settings row sits inside, at 100 % scale:
    //
    //   NavigationView pane (SettingsWindow.xaml pins PaneDisplayMode="Left",
    //   so the pane never auto-collapses; width = the in-box OpenPaneLength
    //   default)                                                         320
    //   ScrollViewer Padding="24,16,24,24" (left + right)                 48
    //   Vertical scroll bar, reserved conservatively (WinUI may overlay
    //   it rather than take layout space — over-reserving only ever
    //   makes the floor safer, never tighter)                             16
    //                                                                    ────
    //   SettingsChromeWidth                                              384
    //
    // and the card each row is wrapped in (Card(): Padding 14+14, BorderThickness 1+1) costs 30.
    //
    // The content Grid additionally carries MaxWidth="720", which caps a row from the top on a wide
    // window. It never binds at or below SettingsDefaultWidth (960 - 384 = 576 < 720), so it plays no
    // part in the floor arithmetic below — it only stops rows stretching on a maximised window.

    /// <summary>Non-content width the Settings window always spends: nav pane + scroll padding + scroll bar.</summary>
    public const int SettingsChromeWidth = 384;

    /// <summary>Horizontal cost of the <c>Card()</c> border a settings row sits in (padding 14+14, border 1+1).</summary>
    public const int SettingsCardWidth = 30;

    /// <summary>
    /// Floor for a settings row's header/description column. Below this the text column starves and
    /// wraps toward one character per line — the collapse reported in issue #31. 180 DIP fits roughly
    /// 25 characters of the 11 px description per line, so a line break always falls between words.
    ///
    /// <para>Re-checked when the brand mono face landed (issue #44) — this is the ONE constant here
    /// whose justification is a font metric rather than a declared width, so a wider face could in
    /// principle have invalidated it. Cascadia Mono is fixed-advance at 0.586 em/char (measured from
    /// the shipped .ttf), i.e. 6.45 DIP at 11 px, so 180 DIP still carries ~28 characters per line —
    /// comfortably above the ~25 this floor was set for. The number therefore stands unchanged; it is
    /// the reasoning behind it, not the value, that the font touched.</para>
    /// </summary>
    public const double SettingRowTextMinWidth = 180;

    /// <summary>The <c>ColumnSpacing</c> between a settings row's text column and its control.</summary>
    public const double SettingRowColumnSpacing = 12;

    /// <summary>
    /// The widest control any settings row declares a fixed minimum for: the Managed-VMs row's
    /// action ComboBox (MinWidth 150) + 8 spacing + delay ComboBox (MinWidth 130). Rows whose control
    /// is text-sized instead (the Maintenance buttons) are handled by
    /// <see cref="ShouldStackSettingRow"/> at whatever width they turn out to need, which is why the
    /// minimum below does not have to grow with their label text.
    ///
    /// <para>Issue #44 (brand mono face) does not move this: it is the widest DECLARED minimum, and a
    /// font only invalidates it if some control's CONTENT outgrows the MinWidth it declares. With
    /// App.xaml's ControlContentThemeFontSize trimmed to 12.5 the widest combo item in the window
    /// ("Trace — most verbose", the Log-level row) measures ~191 DIP against its declared MinWidth of
    /// 200, so every settings control is still bounded by its own declared minimum and 288 remains the
    /// largest of them. At the untrimmed 14 px that same item measures ~209 and WOULD have burst its
    /// MinWidth — which is a second reason the trim is load-bearing, not cosmetic.</para>
    /// </summary>
    public const int SettingRowWidestFixedControl = 288;

    /// <summary>
    /// First-open width (DIP): chrome + card + the widest fixed control + spacing + a comfortable text
    /// column, so every row opens side-by-side as designed rather than stacked.
    /// 384 + 30 + 288 + 12 + 246 = 960.
    /// </summary>
    public const int SettingsDefaultWidth = 960;

    /// <summary>First-open height (DIP) — a comfortable reading height for the settings cards.</summary>
    public const int SettingsDefaultHeight = 760;

    /// <summary>
    /// Minimum width (DIP). The floor is the point below which even a *stacked* row could not show
    /// its content: chrome (384) + card (30) + the wider of the text-column floor (180) and the
    /// widest fixed control (288) → 384 + 30 + 288 = 702. Between this and
    /// <see cref="SettingsDefaultWidth"/> rows stack individually (see <see cref="ShouldStackSettingRow"/>);
    /// at or above it no row's text can be squeezed below <see cref="SettingRowTextMinWidth"/>.
    /// </summary>
    public const int SettingsMinWidth = SettingsChromeWidth + SettingsCardWidth + SettingRowWidestFixedControl;

    /// <summary>
    /// Minimum height (DIP). The nav pane is the tallest thing that cannot scroll: 6 items × 40 = 240,
    /// pane footer (1 px divider + 8 margin + two text lines ≈ 34 + 20 margins) ≈ 63, NavigationView top
    /// area ≈ 44 → ≈ 347, plus a 32 px title bar ≈ 379. Rounded up to 480 so at least one settings card
    /// is visible beside the pane rather than a scroll bar alone.
    /// </summary>
    public const int SettingsMinHeight = 480;

    /// <summary>
    /// True when a settings row must stack its control beneath the text instead of beside it — i.e.
    /// when the row cannot give the text column its <see cref="SettingRowTextMinWidth"/> floor while
    /// the control keeps its natural width.
    ///
    /// <para>This is the structural fix for issue #31 (3): it is evaluated against *measured* DIP
    /// widths at layout time, so it holds at any DPI, any font, and any label length — unlike a fixed
    /// minimum window size, which only papers over the symptom at one scale.</para>
    /// </summary>
    /// <param name="availableWidth">The row grid's actual width (DIP), i.e. inside the card padding.</param>
    /// <param name="controlWidth">The control's measured desired width (DIP).</param>
    public static bool ShouldStackSettingRow(double availableWidth, double controlWidth)
    {
        // A not-yet-measured row (width 0/NaN) must not flap into stacked mode before its first real
        // measure pass, so treat a non-positive width as "no decision yet — keep side by side".
        if (double.IsNaN(availableWidth) || availableWidth <= 0) return false;
        if (double.IsNaN(controlWidth) || controlWidth < 0) controlWidth = 0;

        return availableWidth < SettingRowTextMinWidth + SettingRowColumnSpacing + controlWidth;
    }

    /// <summary>
    /// The saved rect from config, or <c>null</c> when there isn't a complete, usable one. All four
    /// values must be present and the size positive — a partially written config (or one from before
    /// issue #31 added these properties) falls back to the centred default rather than placing the
    /// window at a half-known position.
    /// </summary>
    public static WindowRect? TryGetSavedRect(int? x, int? y, int? width, int? height)
        => x is { } sx && y is { } sy && width is { } w && height is { } h && w > 0 && h > 0
            ? new WindowRect(sx, sy, w, h)
            : null;

    /// <summary>
    /// The first-open rect (physical px): <paramref name="defaultWidth"/> × <paramref name="defaultHeight"/>
    /// scaled to the monitor's DPI, capped to its work area so it is never larger than the screen, and
    /// centred in that work area.
    /// </summary>
    public static WindowRect DefaultRect(
        int workLeft, int workTop, int workRight, int workBottom,
        double scale, int defaultWidth, int defaultHeight)
    {
        int workW = workRight  - workLeft;
        int workH = workBottom - workTop;
        int w = Math.Min((int)Math.Round(defaultWidth  * scale), workW);
        int h = Math.Min((int)Math.Round(defaultHeight * scale), workH);
        return new WindowRect(workLeft + (workW - w) / 2, workTop + (workH - h) / 2, w, h);
    }

    /// <summary>
    /// Fits <paramref name="rect"/> inside a monitor's work area: the size is clamped into
    /// [<paramref name="minWidth"/>/<paramref name="minHeight"/>, work area], then the position is
    /// clamped so the whole window stays on screen. A rect saved on a since-disconnected monitor
    /// therefore cannot strand the window offscreen — the caller resolves the *nearest connected*
    /// monitor's work area (see <see cref="NativeMethods.ClampRectToNearestMonitor"/>) and this pulls
    /// the rect onto it.
    ///
    /// <para>The work area wins over the minimum: on a screen smaller than the minimum the window is
    /// sized to the screen rather than pushed off it.</para>
    /// </summary>
    public static WindowRect ClampToWorkArea(
        WindowRect rect,
        int workLeft, int workTop, int workRight, int workBottom,
        int minWidth = 0, int minHeight = 0)
    {
        int workW = workRight  - workLeft;
        int workH = workBottom - workTop;

        int w = Math.Min(Math.Max(rect.Width,  minWidth),  workW);
        int h = Math.Min(Math.Max(rect.Height, minHeight), workH);

        int x = Math.Clamp(rect.X, workLeft, workRight  - w);
        int y = Math.Clamp(rect.Y, workTop,  workBottom - h);

        return new WindowRect(x, y, w, h);
    }
}
