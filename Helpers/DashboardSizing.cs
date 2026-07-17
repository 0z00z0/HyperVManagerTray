namespace HyperVManagerTray.Helpers;

/// <summary>
/// One row of a dashboard card that puts a variable-length value on the LEFT (a Star column) and a
/// short, bounded value on the RIGHT (an Auto column): the VM header (name | state) and the VM
/// sub-row (switch · rule | guest IPv4).
/// </summary>
/// <param name="Left">The Star-column text — the one that can outgrow its slot.</param>
/// <param name="LeftFontSize">The Left text's font size (DIP).</param>
/// <param name="Right">The Auto-column text.</param>
/// <param name="RightFontSize">The Right text's font size (DIP).</param>
/// <param name="Gap">Space between the two, i.e. the right label's left margin.</param>
public readonly record struct SplitRow(
    string? Left, double LeftFontSize, string? Right, double RightFontSize, double Gap);

/// <summary>
/// Pure, WinUI-free sizing arithmetic for the dashboard popup (issue #57) — the same idiom as
/// <see cref="WindowPlacement"/>: numbers in, numbers out, so the load-bearing decisions (how wide
/// should the popup be? does this value truncate? must it carry a tooltip?) are unit-testable
/// without a GUI or a live host.
///
/// <para><b>Why this is not issue #31.</b> The VM sub-row has #31's exact SHAPE — a <c>Star</c> text
/// column beside an <c>Auto</c> column — and <c>Auto</c> does take its width first. But it is not
/// #31's DEFECT. #31 collapsed because the Auto child was a ComboBox declaring a MinWidth of 200-220
/// inside a card that shrank with the window: Auto could demand more than the row had, Star was
/// starved toward zero, and the WRAPPING description degenerated into one character per line. Here
/// the Auto child is a single IPv4 (<c>VmService.ReadIps</c> selects one dotted, colon-free address),
/// so it is bounded at 15 characters ≈ 113 DIP of a 258 DIP row — Star always keeps ≥ 144 DIP and
/// cannot starve. The Star text is also NoWrap, so it truncates rather than collapsing. The sub-row
/// truncates for an ordinary reason: the popup's width was a hard-pinned constant (320) and the value
/// is simply longer than the budget. #31's fix (stack the control beneath the text) would be actively
/// wrong here — the IP belongs beside the subtitle, not under it. Hence a width band, not a stack.</para>
///
/// <para><b>Why character arithmetic is exact rather than an estimate.</b> The brand face (issue #44)
/// is Cascadia Mono, and the shipped <c>CascadiaMono.ttf</c> was inspected directly: its <c>hmtx</c>
/// table contains exactly two distinct advances — 0 and 1200 — against a 2048 unitsPerEm head, i.e.
/// every rendering glyph is <see cref="MonoAdvanceEm"/> = 0.5859 em wide. Text width is therefore
/// literally <c>length × fontSize × 0.5859</c>, not an approximation of one. (The same 0.586 figure
/// <see cref="WindowPlacement.SettingRowTextMinWidth"/> already documents.)</para>
///
/// <para><b>Why there is no "narrower font" lever here</b> (issue #57 asked for one). The same
/// inspection settles it: <c>CascadiaMono.ttf</c> IS a variable font — it carries <c>fvar</c>,
/// <c>gvar</c>, <c>avar</c>, <c>HVAR</c> and <c>STAT</c> — but it exposes exactly ONE axis,
/// <c>wght</c> (200–700, default 400). There is no <c>wdth</c> axis, and there could not usefully be
/// one: the face is fixed-pitch, so every glyph shares the 1200-unit advance and a lighter weight
/// buys strictly ZERO horizontal room. Reaching for Light/SemiLight to fit a longer VM name would
/// change how the row LOOKS while leaving <see cref="TextWidth"/> — and therefore the truncation —
/// bit-for-bit identical. Horizontal room on this popup comes from the two levers above it (a
/// tighter separator, a wider band) and nowhere else.</para>
///
/// <para><b>Where that exactness stops</b> — and why the tooltip, not the arithmetic, is the
/// guarantee. It holds for the repertoire Cascadia Mono actually covers (Latin, punctuation, box
/// drawing). A VM named in a script the face lacks (CJK, say) falls back to another font whose glyphs
/// are wider, and this would under-measure it. That is a real limit, so the row is ALSO given
/// <c>TextTrimming.CharacterEllipsis</c>: an under-measured value shows an ellipsis rather than a
/// hard clip, and the failure mode is a visible "…" on a value whose tooltip we predicted it would
/// not need — not a silently amputated one. Widening the popup is the optimisation; the tooltip is
/// the correctness property.</para>
/// </summary>
public static class DashboardSizing
{
    /// <summary>
    /// Advance width of every Cascadia Mono glyph, in ems (1200/2048, read from the shipped .ttf's
    /// <c>hmtx</c>/<c>head</c>). The face is fixed-pitch — <c>post.isFixedPitch</c> is 1 and <c>hmtx</c>
    /// holds a single non-zero advance — so this one number describes every character.
    /// </summary>
    public const double MonoAdvanceEm = 1200.0 / 2048.0;

    /// <summary>
    /// Floor for the popup's content width (DIP) — the width it has always opened at, kept as the
    /// minimum so a host with short VM names sees exactly the popup it sees today. Issue #57 turned
    /// this from a hard pin into the bottom of a band.
    /// </summary>
    public const double MinContentWidth = 320;

    /// <summary>
    /// Cap for the popup's content width (DIP), chosen by Espen in issue #57. Past this the popup
    /// stops growing and values truncate with a tooltip instead: a tray popup that keeps widening to
    /// fit an arbitrarily long VM name stops reading as a tray popup.
    /// </summary>
    public const double MaxContentWidth = 480;

    /// <summary>
    /// Breathing room (DIP) added to each row's required width (issue #59). #57 sized the popup to the
    /// text's EXACT advance sum, so a row could be sized to fit to the DIP — and then truncate on screen
    /// anyway: WinUI measures with its own font metrics and rounds every column to a whole pixel at the
    /// active DPI, and a trimmed TextBlock reserves space for the "…", so a dead-even fit renders an
    /// ellipsis while the arithmetic (and the test built on the same arithmetic) both said it fit. This
    /// is a full character at the header's 12px value (≈ 7 DIP) plus a rounding pixel, so the Star text
    /// always gets STRICTLY more room than it needs, never exactly equal.
    /// </summary>
    public const double FitSlack = 8;

    /// <summary>
    /// Sub-pixel safety (DIP) for <see cref="IsLeftTruncated"/> (issue #59): a row whose Star text fills
    /// its slot to within this margin is treated as truncated, so it still carries a tooltip. At the
    /// width cap a value can need almost exactly its slot, where a strict "&gt;" comparison would call a
    /// will-render-an-ellipsis row "fits" and drop the tooltip — the one unreachable-value case the
    /// tooltip exists to prevent. Erring toward attaching a tooltip is free (it only ever repeats the
    /// on-screen text in the boundary case); dropping one is the bug.
    /// </summary>
    public const double SubPixelSafety = 1.0;

    /// <summary>
    /// Horizontal chrome between the popup's content width and a card row's usable width:
    /// <c>Root</c>'s Padding (20+20) + the card Border's Padding (10+10) and BorderThickness (1+1).
    /// At the 320 floor this leaves 258 DIP — the figure issue #44 independently measured the
    /// (non-wrapping) button row against at ~251 of 258.
    /// </summary>
    public const double CardChromeWidth = 62;

    /// <summary>Rendered width (DIP) of <paramref name="text"/> in the brand mono face. Exact — see the remarks on the class.</summary>
    public static double TextWidth(string? text, double fontSize)
        => string.IsNullOrEmpty(text) || fontSize <= 0 ? 0 : text.Length * fontSize * MonoAdvanceEm;

    /// <summary>
    /// Content width (DIP) at which <paramref name="row"/> shows both its values in full, plus
    /// <see cref="FitSlack"/> so the Star text is never sized to a to-the-DIP-exact fit that renders an
    /// ellipsis anyway (issue #59).
    /// </summary>
    public static double RequiredContentWidth(SplitRow row)
        => CardChromeWidth
         + TextWidth(row.Left,  row.LeftFontSize)
         + FitSlack
         + Math.Max(0, row.Gap)
         + TextWidth(row.Right, row.RightFontSize);

    /// <summary>
    /// Content width (DIP) at which every row shows in full — the widest single requirement. Returns
    /// <see cref="MinContentWidth"/> for an empty set, so a dashboard with no cards opens at the floor.
    /// </summary>
    public static double RequiredContentWidth(IEnumerable<SplitRow> rows)
    {
        if (rows is null) return MinContentWidth;
        double required = MinContentWidth;
        foreach (var row in rows) required = Math.Max(required, RequiredContentWidth(row));
        return required;
    }

    /// <summary>
    /// The popup's content width: what the content needs, held inside
    /// [<see cref="MinContentWidth"/>, <see cref="MaxContentWidth"/>] and then capped to what the
    /// monitor can actually show.
    ///
    /// <para><paramref name="screenLimit"/> is the work area's width minus both edge margins, in DIP.
    /// It WINS over the floor, matching <see cref="WindowPlacement.ClampToWorkArea"/>'s established
    /// rule that the work area beats the minimum: on a display too narrow for even 320 the popup is
    /// sized to the display rather than pushed off it. This is what keeps a now-variable width on
    /// screen beside the tray at every DPI — the caller converts DIP to physical pixels with the
    /// monitor's own scale, so 480 DIP is 480 px at 100 % and 840 px at 175 %, and the cap is applied
    /// in the same units the work area is measured in. Pass a non-positive value for "unknown", which
    /// applies no screen cap.</para>
    /// </summary>
    public static double ContentWidth(double required, double screenLimit)
    {
        if (double.IsNaN(required)) required = MinContentWidth;
        double width = Math.Clamp(required, MinContentWidth, MaxContentWidth);
        return screenLimit > 0 ? Math.Min(width, screenLimit) : width;
    }

    /// <summary>Room (DIP) left for <paramref name="row"/>'s Star text once the Auto value and the gap are paid for.</summary>
    public static double AvailableForLeft(SplitRow row, double contentWidth)
        => Math.Max(0, contentWidth - CardChromeWidth - Math.Max(0, row.Gap) - TextWidth(row.Right, row.RightFontSize));

    /// <summary>
    /// True when <paramref name="row"/>'s Star text cannot be shown in full at
    /// <paramref name="contentWidth"/> — including the case where it fills the slot to within
    /// <see cref="SubPixelSafety"/>, which renders an ellipsis on screen (issue #59).
    /// </summary>
    public static bool IsLeftTruncated(SplitRow row, double contentWidth)
        => TextWidth(row.Left, row.LeftFontSize) > AvailableForLeft(row, contentWidth) - SubPixelSafety;

    /// <summary>
    /// The tooltip <paramref name="row"/>'s Star text must carry at <paramref name="contentWidth"/>:
    /// the full value when it truncates, otherwise <c>null</c> (nothing is hidden, so a tooltip would
    /// only repeat what is already on screen).
    ///
    /// <para>Truncation and the tooltip are decided by this ONE function on purpose. The app's rule
    /// (issues #37/#40, <c>docs/DISPLAY-VOCABULARY.md</c>) is that it never hides and never claims
    /// what it has not verified; truncation hides, and the tooltip is the only thing that makes it
    /// acceptable. Were the trim decided in the layout and the tooltip attached separately, the two
    /// could drift and a value could go silently unreachable — the bug this method exists to make
    /// unrepresentable. Callers must not test truncation themselves; they assign whatever this
    /// returns.</para>
    /// </summary>
    public static string? LeftTooltip(SplitRow row, double contentWidth)
        => IsLeftTruncated(row, contentWidth) ? row.Left : null;
}
