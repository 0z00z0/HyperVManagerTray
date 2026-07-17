using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Issue #57 — the dashboard popup's width band, and the property that pays for it: nothing the popup
/// truncates is unreachable.
/// </summary>
public class DashboardSizingTests
{
    // The row Espen reported, from the screenshot: a bridged VM whose rule name is long enough to be
    // cut mid-word ("Bridged · Petterhaugen [dock…") beside a guest IPv4.
    private static SplitRow ReportedRow(string ip = "192.168.1.50") =>
        new("Bridged · Petterhaugen [dockbenk]", 10, ip, 12, 8);

    // ── The font metric everything else rests on ────────────────────────────────

    [Fact]
    public void MonoAdvance_matches_the_shipped_face()
    {
        // Read from CascadiaMono.ttf itself: hmtx holds one non-zero advance (1200) against a 2048
        // unitsPerEm head. If the brand face is ever replaced with one that is not fixed-pitch — or
        // fixed at a different advance — every width below is wrong and this is where it surfaces.
        Assert.Equal(1200.0 / 2048.0, DashboardSizing.MonoAdvanceEm, 6);
        Assert.Equal(0.5859375, DashboardSizing.MonoAdvanceEm, 6);
    }

    [Theory]
    [InlineData("", 12, 0)]
    [InlineData(null, 12, 0)]
    [InlineData("abc", 0, 0)]
    [InlineData("255.255.255.255", 12, 15 * 12 * 0.5859375)]   // the widest IPv4 the Auto column can hold
    public void TextWidth_is_length_times_size_times_advance(string? text, double size, double expected)
        => Assert.Equal(expected, DashboardSizing.TextWidth(text, size), 6);

    // ── Step 0: this is #31's shape but not #31's defect ────────────────────────

    [Fact]
    public void Ip_column_cannot_starve_the_subtitle_the_way_issue_31_starved_its_text()
    {
        // #31's Auto child was a ComboBox declaring MinWidth 200-220, which could demand more than the
        // row had and drive the Star column toward zero. The dashboard's Auto child is ONE IPv4
        // (VmService.ReadIps takes a single dotted, colon-free address), so it is bounded at 15 chars.
        // At the 320 floor the sub-row is 258 DIP; the worst case still leaves the subtitle ~144 DIP —
        // roughly 24 characters at 10 px, i.e. truncation, never the one-character-per-line collapse.
        var worst = new SplitRow("anything", 10, "255.255.255.255", 12, 8);
        double left = DashboardSizing.AvailableForLeft(worst, DashboardSizing.MinContentWidth);

        Assert.True(left > 140, $"subtitle starved to {left:F1} DIP — the Auto column is no longer bounded");
        Assert.True(left / (10 * DashboardSizing.MonoAdvanceEm) > 20);
    }

    // ── Part 1: the band ────────────────────────────────────────────────────────

    [Fact]
    public void Short_content_still_opens_at_the_320_floor()
    {
        // The popup every existing user already has: it must not shrink below what it opens at today.
        var tidy = new SplitRow("dev-01", 10, "10.0.0.2", 12, 8);
        Assert.Equal(320, DashboardSizing.ContentWidth(DashboardSizing.RequiredContentWidth(tidy), 0));
    }

    [Fact]
    public void The_reported_row_now_fits_inside_the_band_without_truncating()
    {
        // The point of the whole change: Espen's actual row grows the popup instead of being cut.
        var row   = ReportedRow();
        double cw = DashboardSizing.ContentWidth(DashboardSizing.RequiredContentWidth(row), 0);

        Assert.InRange(cw, 320, 480);
        Assert.False(DashboardSizing.IsLeftTruncated(row, cw));
        Assert.Null(DashboardSizing.LeftTooltip(row, cw));
    }

    [Fact]
    public void The_reported_row_was_truncated_at_the_old_hard_pinned_320()
    {
        // Guards the diagnosis, not just the fix: if this ever stops truncating at 320, the change
        // above is solving a problem that no longer exists and the band's floor should be revisited.
        Assert.True(DashboardSizing.IsLeftTruncated(ReportedRow(), 320));
    }

    [Fact]
    public void Width_never_leaves_the_band()
    {
        var huge = new SplitRow(new string('x', 400), 10, "192.168.1.50", 12, 8);
        Assert.Equal(480, DashboardSizing.ContentWidth(DashboardSizing.RequiredContentWidth(huge), 0));

        var empty = new SplitRow("", 10, "", 12, 8);
        Assert.Equal(320, DashboardSizing.ContentWidth(DashboardSizing.RequiredContentWidth(empty), 0));

        Assert.Equal(320, DashboardSizing.ContentWidth(double.NaN, 0));
        Assert.Equal(320, DashboardSizing.RequiredContentWidth((IEnumerable<SplitRow>)null!));
        Assert.Equal(320, DashboardSizing.RequiredContentWidth(Array.Empty<SplitRow>()));
    }

    [Fact]
    public void The_widest_row_sets_the_width()
    {
        var rows = new[]
        {
            new SplitRow("short", 10, "10.0.0.1", 12, 8),
            ReportedRow(),
            new SplitRow("mid", 10, "10.0.0.2", 12, 8),
        };
        Assert.Equal(DashboardSizing.RequiredContentWidth(ReportedRow()), DashboardSizing.RequiredContentWidth(rows), 6);
    }

    // ── Part 1: staying on screen (both DPIs) ───────────────────────────────────

    [Fact]
    public void A_display_too_narrow_for_the_band_wins_over_the_floor()
    {
        // WindowPlacement.ClampToWorkArea's established rule: the work area beats the minimum, so the
        // popup is sized to the display rather than pushed off it.
        Assert.Equal(200, DashboardSizing.ContentWidth(500, screenLimit: 200));
        Assert.Equal(400, DashboardSizing.ContentWidth(500, screenLimit: 400));
        Assert.Equal(480, DashboardSizing.ContentWidth(500, screenLimit: 0));      // 0 = unknown, no cap
        Assert.Equal(480, DashboardSizing.ContentWidth(500, screenLimit: 9999));
    }

    [Theory]
    [InlineData(1.0,  1920, 1080)]   // the external panel at 100 %
    [InlineData(1.75, 2880, 1800)]   // the laptop panel at 175 % — Espen's mixed-DPI machine
    [InlineData(1.5,  1920, 1200)]
    public void The_popup_lands_on_screen_beside_the_tray_at_any_scale(double scale, int workW, int workH)
    {
        // Mirrors ResizeAndPlace: cap in DIP against the work area, scale to physical px, park at the
        // bottom-right inside the margin. The widest possible content must still not fall off the left.
        const int edgeMargin = 12;
        int margin = (int)Math.Ceiling(edgeMargin * scale);
        int maxW   = workW - margin * 2;

        var widest = new SplitRow(new string('x', 400), 10, "255.255.255.255", 12, 8);
        double contentWidth = DashboardSizing.ContentWidth(
            DashboardSizing.RequiredContentWidth(widest), screenLimit: maxW / scale);

        int w = Math.Min((int)Math.Ceiling(contentWidth * scale), maxW);
        int x = workW - w - margin;   // work.Right - w - margin, with work.Left = 0

        Assert.True(x >= margin, $"popup starts at {x}, inside the {margin}px margin at {scale:0.##}x");
        Assert.True(x + w <= workW, $"popup right edge {x + w} overruns the {workW}px work area");
        Assert.True(w <= (int)Math.Ceiling(480 * scale));
        _ = workH;
    }

    // ── Part 2: the reclaimed separator ─────────────────────────────────────────

    [Fact]
    public void Tightening_the_separator_reclaims_room_before_the_window_spends_width()
    {
        // "Bridged  ·  rule" -> "Bridged · rule": 2 characters at the sub-row's 10 px.
        var before = new SplitRow("Bridged  ·  Petterhaugen [dockbenk]", 10, "192.168.1.50", 12, 8);
        var after  = ReportedRow();

        double saved = DashboardSizing.RequiredContentWidth(before) - DashboardSizing.RequiredContentWidth(after);
        Assert.Equal(2 * 10 * DashboardSizing.MonoAdvanceEm, saved, 6);
        Assert.True(saved > 11);
    }

    // ── The property the truncation is only acceptable because of ───────────────

    public static TheoryData<string, string, double> Corpus()
    {
        var data = new TheoryData<string, string, double>();
        foreach (var left in new[]
                 {
                     "", "Bridged · LAN", "Bridged · Petterhaugen [dockbenk]",
                     "Default Switch · Home office wired uplink rule",
                     new string('x', 200), "—  ·  —",
                 })
        foreach (var right in new[] { "", "10.0.0.1", "192.168.1.50", "255.255.255.255" })
        foreach (var width in new[] { 200.0, 320.0, 400.0, 480.0 })
            data.Add(left, right, width);
        return data;
    }

    /// <summary>
    /// The load-bearing guard (issues #37/#40 — never hide): at EVERY width in the band, for every
    /// value the sub-row can hold, a value that does not fit carries its full text as a tooltip, and
    /// one that fits carries none. A truncated value with no tooltip is a bug, not a style.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Nothing_is_ever_truncated_without_a_tooltip(string left, string right, double contentWidth)
    {
        var row = new SplitRow(left, 10, right, 12, 8);

        // Decided independently of the helper's own truncation flag, so the two cannot agree by
        // construction: measure the text and the room, and compare them here.
        double needed    = left.Length * 10 * DashboardSizing.MonoAdvanceEm;
        double available = Math.Max(0, contentWidth - DashboardSizing.CardChromeWidth - 8
                                     - right.Length * 12 * DashboardSizing.MonoAdvanceEm);
        bool willBeCut = needed > available;

        string? tip = DashboardSizing.LeftTooltip(row, contentWidth);

        if (willBeCut)
        {
            Assert.NotNull(tip);
            Assert.Equal(left, tip);   // the FULL value, not the visible remnant
        }
        else
        {
            Assert.Null(tip);          // nothing hidden — a tooltip would only repeat the screen
        }
    }

    [Fact]
    public void A_value_past_the_cap_truncates_but_stays_reachable()
    {
        var row = new SplitRow(new string('x', 200), 10, "192.168.1.50", 12, 8);
        double cw = DashboardSizing.ContentWidth(DashboardSizing.RequiredContentWidth(row), 0);

        Assert.Equal(480, cw);                                  // the band stopped growing
        Assert.True(DashboardSizing.IsLeftTruncated(row, cw));  // so it truncates
        Assert.Equal(row.Left, DashboardSizing.LeftTooltip(row, cw));   // and is still reachable
    }

    [Fact]
    public void A_row_with_no_left_text_needs_no_tooltip()
    {
        var row = new SplitRow("", 10, "255.255.255.255", 12, 8);
        Assert.Null(DashboardSizing.LeftTooltip(row, 320));
    }
}
