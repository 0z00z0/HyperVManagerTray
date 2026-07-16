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
    /// Every settings row that declares a fixed minimum width for its control, and what that row needs.
    /// <see cref="SettingRowWidestFixedControl"/> is the maximum over this table — a value DERIVED from
    /// the row set rather than a number restated beside a prose inventory of it.
    ///
    /// <para><b>Why a table.</b> The constant was 288, justified in a comment as "the Managed-VMs action
    /// ComboBox (150) + 8 + delay ComboBox (130)". That was true when issue #31 wrote it and false by the
    /// time #34/#41 had added rows — the Override row alone declares 160 + 8 + 160 = 328 — and #44 then
    /// re-derived the number having re-checked only the FONT, not the row set, because the comment
    /// presented the inventory as settled. A prose list of rows in a doc-comment cannot be checked by
    /// anything; this one is checked by <c>WindowPlacementTests</c>, and a new row that forgets to appear
    /// here is a row whose entry the test can name as missing rather than a silent 40-DIP shortfall.</para>
    ///
    /// <para><b>What belongs here.</b> A row whose control declares a MinWidth, at that MinWidth. A row
    /// whose control is a <c>ControlStrip</c> contributes its WIDEST SINGLE CHILD, not the sum: the strip
    /// wraps, so that is genuinely all it needs. A row whose control is text-sized and shrinkable (a lone
    /// button, the startup ToggleSwitch) contributes nothing — <see cref="ShouldStackSettingRow"/> gives
    /// it whatever it turns out to need.</para>
    ///
    /// <para><b>What a font change can still invalidate</b> (issue #44's real question): a control whose
    /// CONTENT outgrows the MinWidth it declares. With App.xaml's ControlContentThemeFontSize trimmed to
    /// 12.5 the widest combo item in the window ("Trace — most verbose", the Log-level row) measures ~191
    /// DIP against its declared 200, so every entry below is still bounded by its own declared minimum. At
    /// the untrimmed 14 px that item measures ~209 and WOULD have burst it — which is why that trim is
    /// load-bearing, not cosmetic.</para>
    /// </summary>
    public static readonly IReadOnlyList<(string Row, int MinWidth)> SettingRowControlMinimums =
    [
        // General
        ("Log level — ComboBox MinWidth 200",                                     200),
        // Managed VMs
        ("Start managing a VM — combo MinWidth 220 over the Add button",          220),
        ("Network adapter — SuggestionCombo MinWidth 220",                        220),
        ("On bridge lost — ControlStrip: action combo 150 | delay combo 130",     150),
        // Network
        ("Fallback switch — SuggestionCombo MinWidth 220",                        220),
        ("Fallback target VMs — box MinWidth 220 over the Add VM picker",         220),
        ("Override VM switch — ControlStrip: VM 160 | switch 160 | Apply btn",    160),
        // Rows whose control is a lone shrinkable button or a wrapping strip of them (Maintenance's
        // Config & logs / Network / Updates, the Adapters rename rows, the startup ToggleSwitch) declare
        // no minimum and are deliberately absent — see the remarks.
    ];

    /// <summary>
    /// The widest minimum any settings row's control declares — the maximum over
    /// <see cref="SettingRowControlMinimums"/>. Feeds <see cref="SettingsMinWidth"/>.
    /// </summary>
    public static int SettingRowWidestFixedControl => SettingRowControlMinimums.Max(r => r.MinWidth);

    /// <summary>
    /// First-open width (DIP): wide enough that the rows open as designed rather than immediately
    /// stacked. Deliberately its own number and NOT derived from
    /// <see cref="SettingRowWidestFixedControl"/>: this is a comfort judgement about how the window
    /// should look on opening, whereas the minimum is a correctness floor, and deriving the two from one
    /// term made a floor change silently resize the window every user opens.
    /// </summary>
    public const int SettingsDefaultWidth = 960;

    /// <summary>First-open height (DIP) — a comfortable reading height for the settings cards.</summary>
    public const int SettingsDefaultHeight = 760;

    /// <summary>
    /// Minimum width (DIP). The floor is the point below which even a *stacked* row could not show its
    /// content: chrome (384) + card (30) + the wider of the text-column floor (180) and
    /// <see cref="SettingRowWidestFixedControl"/>. Between this and <see cref="SettingsDefaultWidth"/>
    /// rows stack individually (see <see cref="ShouldStackSettingRow"/>).
    ///
    /// <para>Note what this floor is and is not. It guarantees that a row's control can show itself at
    /// its declared minimum. It is NOT what stops a multi-control row overflowing the card — a constant
    /// cannot do that job at every width, and trying to made it wrong twice (see
    /// <see cref="SettingRowControlMinimums"/>). <c>ControlStrip</c> holds that guarantee, by wrapping.</para>
    /// </summary>
    public static int SettingsMinWidth =>
        SettingsChromeWidth + SettingsCardWidth + Math.Max((int)SettingRowTextMinWidth, SettingRowWidestFixedControl);

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
    /// Groups a horizontal run of controls into wrapped lines: which child indices share each line, given
    /// their natural widths, the width available and the gap between them. Backs <c>ControlStrip</c>.
    ///
    /// <para>Pure, so the load-bearing property is testable without a GUI: <b>a child is never placed on a
    /// line that cannot hold it</b> — the clipping that made the Override row unusable at the window's
    /// minimum width. A child WIDER than the whole row still gets a line to itself (there is nowhere
    /// better for it, and it is then the panel's reported desired width, so the row above can react),
    /// but it never shares one.</para>
    /// </summary>
    /// <param name="childWidths">Each child's natural (unconstrained) desired width, in order.</param>
    /// <param name="availableWidth">Room for the run. Infinite/NaN means "no constraint" — one line.</param>
    /// <param name="spacing">Gap between adjacent children on a line.</param>
    /// <returns>One list of child indices per line, in order. Empty when there are no children.</returns>
    public static IReadOnlyList<IReadOnlyList<int>> WrapStrip(
        IReadOnlyList<double> childWidths, double availableWidth, double spacing)
    {
        if (childWidths is null || childWidths.Count == 0) return [];

        // Unbounded or not-yet-measured: one line, the natural layout. A zero/NaN width must not wrap
        // every child onto its own line during the first measure pass, the same reason
        // ShouldStackSettingRow treats a non-positive width as "no decision yet".
        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
            return [[.. Enumerable.Range(0, childWidths.Count)]];

        var lines   = new List<IReadOnlyList<int>>();
        var current = new List<int>();
        double used = 0;

        for (int i = 0; i < childWidths.Count; i++)
        {
            double w = double.IsNaN(childWidths[i]) ? 0 : Math.Max(0, childWidths[i]);
            double needed = current.Count == 0 ? w : used + spacing + w;

            if (current.Count > 0 && needed > availableWidth)
            {
                lines.Add(current);
                current = [];
                used = 0;
                needed = w;
            }

            current.Add(i);
            used = needed;
        }

        if (current.Count > 0) lines.Add(current);
        return lines;
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
