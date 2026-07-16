using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The Settings window's placement and row-layout arithmetic (issue #31). Every decision that matters
/// — is the saved rect usable, does it land on a connected monitor, must a row stack — is a pure
/// function of numbers, so it is all testable here without a GUI.
/// </summary>
public class WindowPlacementTests
{
    // A typical 1080p work area (1080 minus a 40 px taskbar), used as the "connected monitor".
    private const int L = 0, T = 0, R = 1920, B = 1040;

    // ── Saved-vs-default decision ────────────────────────────────────────────────

    [Fact]
    public void TryGetSavedRect_ReturnsRect_WhenAllFourPresent()
        => Assert.Equal(new WindowRect(100, 200, 960, 760), WindowPlacement.TryGetSavedRect(100, 200, 960, 760));

    [Fact]
    public void TryGetSavedRect_AcceptsNegativeOrigin_ForAMonitorLeftOfOrAbovePrimary()
        => Assert.Equal(new WindowRect(-1900, -300, 960, 760), WindowPlacement.TryGetSavedRect(-1900, -300, 960, 760));

    // A fresh install, or a config written before issue #31 added these properties, has none of them:
    // a half-known position must fall back to the centred default, never be placed.
    [Theory]
    [InlineData(null, null, null, null)]   // never saved
    [InlineData(100, null, 960, 760)]      // partially written
    [InlineData(100, 200, null, 760)]
    [InlineData(100, 200, 960, null)]
    public void TryGetSavedRect_ReturnsNull_WhenIncomplete(int? x, int? y, int? w, int? h)
        => Assert.Null(WindowPlacement.TryGetSavedRect(x, y, w, h));

    [Theory]
    [InlineData(0, 760)]
    [InlineData(960, 0)]
    [InlineData(-10, 760)]
    public void TryGetSavedRect_ReturnsNull_WhenSizeIsNotPositive(int w, int h)
        => Assert.Null(WindowPlacement.TryGetSavedRect(100, 200, w, h));

    // ── Clamping onto a connected monitor ────────────────────────────────────────

    [Fact]
    public void ClampToWorkArea_LeavesAFullyVisibleRectAlone()
    {
        var rect = new WindowRect(100, 100, 960, 760);
        Assert.Equal(rect, WindowPlacement.ClampToWorkArea(rect, L, T, R, B));
    }

    // The issue's core hazard: a rect saved on a monitor that has since been unplugged. The caller
    // resolves the nearest *connected* monitor; this must pull the window fully onto it.
    [Fact]
    public void ClampToWorkArea_PullsAnOffscreenRectBackOn()
    {
        var clamped = WindowPlacement.ClampToWorkArea(new WindowRect(4000, 2000, 960, 760), L, T, R, B);
        Assert.Equal(new WindowRect(1920 - 960, 1040 - 760, 960, 760), clamped);
    }

    [Fact]
    public void ClampToWorkArea_PullsARectOffTheTopLeftBackOn()
    {
        var clamped = WindowPlacement.ClampToWorkArea(new WindowRect(-5000, -5000, 960, 760), L, T, R, B);
        Assert.Equal(new WindowRect(0, 0, 960, 760), clamped);
    }

    [Fact]
    public void ClampToWorkArea_ShrinksARectLargerThanTheMonitor()
    {
        var clamped = WindowPlacement.ClampToWorkArea(new WindowRect(0, 0, 5000, 3000), L, T, R, B);
        Assert.Equal(new WindowRect(0, 0, 1920, 1040), clamped);
    }

    [Fact]
    public void ClampToWorkArea_HonoursTheMinimumSize()
    {
        var clamped = WindowPlacement.ClampToWorkArea(new WindowRect(100, 100, 200, 150), L, T, R, B, 702, 480);
        Assert.Equal(new WindowRect(100, 100, 702, 480), clamped);
    }

    // A window sized to the minimum must still be reachable on a screen smaller than the minimum:
    // the work area wins, so it is sized down rather than pushed off the edge.
    [Fact]
    public void ClampToWorkArea_LetsTheWorkAreaBeatTheMinimum_OnATinyScreen()
    {
        var clamped = WindowPlacement.ClampToWorkArea(new WindowRect(0, 0, 400, 300), 0, 0, 640, 400, 702, 480);
        Assert.Equal(new WindowRect(0, 0, 640, 400), clamped);
    }

    [Fact]
    public void ClampToWorkArea_ClampsOntoAMonitorWithANonZeroOrigin()
    {
        // A second monitor to the left of primary: origin (-1920, 0).
        var clamped = WindowPlacement.ClampToWorkArea(new WindowRect(-5000, 0, 960, 760), -1920, 0, 0, 1040);
        Assert.Equal(new WindowRect(-1920, 0, 960, 760), clamped);
    }

    // ── The centred default ──────────────────────────────────────────────────────

    [Fact]
    public void DefaultRect_IsCentredOnTheWorkArea_At100Percent()
    {
        var rect = WindowPlacement.DefaultRect(L, T, R, B, 1.0, 960, 760);
        Assert.Equal(new WindowRect((1920 - 960) / 2, (1040 - 760) / 2, 960, 760), rect);
    }

    [Fact]
    public void DefaultRect_ScalesWithMonitorDpi()
    {
        // 175 % — the scale of the laptop panel on the dev machine's mixed-DPI desktop.
        var rect = WindowPlacement.DefaultRect(0, 0, 3840, 2000, 1.75, 960, 760);
        Assert.Equal(1680, rect.Width);   // 960 × 1.75
        Assert.Equal(1330, rect.Height);  // 760 × 1.75
    }

    // The default is a preference, not a demand: it must never exceed the screen it opens on.
    [Fact]
    public void DefaultRect_IsCappedToTheWorkArea_AndStaysOnScreen()
    {
        var rect = WindowPlacement.DefaultRect(0, 0, 800, 600, 2.0, 960, 760);
        Assert.Equal(new WindowRect(0, 0, 800, 600), rect);
    }

    // ── Minimum-size arithmetic ──────────────────────────────────────────────────

    // These pin the derivation, not a taste. If the window's chrome or the widest control changes, the
    // constants must be re-derived — and this test is what says so out loud.
    [Fact]
    public void SettingsMinWidth_IsChromePlusCardPlusTheWidestFixedControl()
    {
        Assert.Equal(384, WindowPlacement.SettingsChromeWidth);   // 320 nav pane + 48 padding + 16 scrollbar
        Assert.Equal(30,  WindowPlacement.SettingsCardWidth);     // 14+14 padding + 1+1 border

        Assert.Equal(
            WindowPlacement.SettingsChromeWidth + WindowPlacement.SettingsCardWidth
                + Math.Max((int)WindowPlacement.SettingRowTextMinWidth, WindowPlacement.SettingRowWidestFixedControl),
            WindowPlacement.SettingsMinWidth);
    }

    /// <summary>
    /// The widest control is DERIVED from the row inventory, never restated beside it.
    ///
    /// <para>It was a bare <c>const 288</c>, justified by a doc-comment inventory ("150 action + 8 + 130
    /// delay") that was true when issue #31 wrote it and false once #34/#41 added rows — the Override row
    /// alone declares 160 + 8 + 160 = 328. Issue #44 then re-derived the number having re-checked only the
    /// font, not the row set, because the comment presented the inventory as settled. A prose inventory
    /// cannot be checked by anything; this one can be, and is.</para>
    /// </summary>
    [Fact]
    public void SettingRowWidestFixedControl_IsTheMaximumOverTheRowInventory()
    {
        Assert.NotEmpty(WindowPlacement.SettingRowControlMinimums);
        Assert.Equal(
            WindowPlacement.SettingRowControlMinimums.Max(r => r.MinWidth),
            WindowPlacement.SettingRowWidestFixedControl);

        // Every entry names the row it came from, so a stale one can be traced back to a real control
        // rather than guessed at — the failure that let 288 outlive its own justification twice.
        Assert.All(WindowPlacement.SettingRowControlMinimums, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Row));
            Assert.True(r.MinWidth > 0);
        });
    }

    /// <summary>
    /// Every row in the inventory can show its control at its declared minimum when the window is at its
    /// own minimum width and the row has stacked. This is what the floor is FOR, and it is asserted over
    /// the whole row set rather than over one hand-picked row.
    /// </summary>
    [Fact]
    public void AtTheMinimumWidth_EveryInventoriedRowsControlFits()
    {
        double rowWidth = WindowPlacement.SettingsMinWidth
                        - WindowPlacement.SettingsChromeWidth
                        - WindowPlacement.SettingsCardWidth;

        Assert.All(WindowPlacement.SettingRowControlMinimums, r =>
            Assert.True(r.MinWidth <= rowWidth,
                $"'{r.Row}' declares {r.MinWidth} DIP but a stacked row at the minimum window width has "
                + $"only {rowWidth} — it would be clipped out of the card, and RootScroll cannot scroll to it"));
    }

    /// <summary>
    /// The point of the minimum: at the narrowest the user can drag the window, every row must still be
    /// able to give its text the floor below which it wraps per character. At that width the widest row
    /// stacks, so the text gets the row's whole width.
    /// </summary>
    [Fact]
    public void AtTheMinimumWidth_TheWidestRowStacks_AndItsTextClearsTheFloor()
    {
        double rowWidth = WindowPlacement.SettingsMinWidth
                        - WindowPlacement.SettingsChromeWidth
                        - WindowPlacement.SettingsCardWidth;

        Assert.True(WindowPlacement.ShouldStackSettingRow(rowWidth, WindowPlacement.SettingRowWidestFixedControl));
        Assert.True(rowWidth >= WindowPlacement.SettingRowTextMinWidth);
    }

    /// <summary>
    /// The point of the default: at first open nothing stacks — every row shows side by side as designed.
    /// </summary>
    [Fact]
    public void AtTheDefaultWidth_TheWidestRowDoesNotStack()
    {
        double rowWidth = WindowPlacement.SettingsDefaultWidth
                        - WindowPlacement.SettingsChromeWidth
                        - WindowPlacement.SettingsCardWidth;

        Assert.False(WindowPlacement.ShouldStackSettingRow(rowWidth, WindowPlacement.SettingRowWidestFixedControl));
        Assert.True(WindowPlacement.SettingsDefaultWidth > WindowPlacement.SettingsMinWidth);
    }

    // ── The row stacking decision ────────────────────────────────────────────────

    [Fact]
    public void ShouldStackSettingRow_IsFalse_WhenTheTextClearsItsFloor()
        => Assert.False(WindowPlacement.ShouldStackSettingRow(480, 288)); // 480 - 12 - 288 = 180, exactly the floor

    [Fact]
    public void ShouldStackSettingRow_IsTrue_OneDipBelowTheFloor()
        => Assert.True(WindowPlacement.ShouldStackSettingRow(479, 288));

    // This is the regression: at the OLD 720 px default the text column was left ~6 DIP — a vertical
    // column of letters. The row must now stack there instead.
    [Fact]
    public void ShouldStackSettingRow_StacksAtTheOldDefaultWidth_TheReportedCollapse()
    {
        double rowWidth = 720 - WindowPlacement.SettingsChromeWidth - WindowPlacement.SettingsCardWidth; // 306
        Assert.True(WindowPlacement.ShouldStackSettingRow(rowWidth, WindowPlacement.SettingRowWidestFixedControl));
    }

    // A long-labelled control widens itself; the row must stack rather than starve the text — this is
    // why the decision is measured per row instead of assumed from the minimum window size.
    [Fact]
    public void ShouldStackSettingRow_StacksForAnUnusuallyWideControl_EvenOnAWideRow()
        => Assert.True(WindowPlacement.ShouldStackSettingRow(600, 450));

    // A not-yet-measured row must not flap into stacked mode before its first real measure pass.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void ShouldStackSettingRow_IsFalse_BeforeTheRowHasAWidth(double availableWidth)
        => Assert.False(WindowPlacement.ShouldStackSettingRow(availableWidth, 288));

    [Fact]
    public void ShouldStackSettingRow_IsFalse_WhenMeasuredUnbounded()
        => Assert.False(WindowPlacement.ShouldStackSettingRow(double.PositiveInfinity, 288));

    [Fact]
    public void ShouldStackSettingRow_TreatsAnUnmeasuredControlAsZeroWide()
        => Assert.False(WindowPlacement.ShouldStackSettingRow(300, double.NaN));

    // ── WrapStrip: a row of controls must never be laid out past the edge of its row ──────

    private const double StripSpacing = 8;

    /// <summary>The width a line of children occupies, including the gaps between them.</summary>
    private static double LineWidth(IReadOnlyList<int> line, IReadOnlyList<double> widths) =>
        line.Sum(i => widths[i]) + StripSpacing * (line.Count - 1);

    /// <summary>
    /// THE property: a child is never placed on a line too narrow to hold it. This is the clipping that
    /// made the Override row unusable — a horizontal StackPanel reports its children's SUM however little
    /// room it is given and arranges the overflow outside the row, where RootScroll (horizontal scrolling
    /// disabled) clips it away entirely.
    /// </summary>
    [Theory]
    [InlineData(288)]   // a stacked row at the window's minimum width
    [InlineData(400)]
    [InlineData(546)]   // a stacked row at the default width
    public void WrapStrip_NeverOverfillsALine(double rowWidth)
    {
        // The real Override row: VM combo 160, switch combo 160, "Apply override" ≈ 120.
        double[] widths = [160, 160, 120];

        var lines = WindowPlacement.WrapStrip(widths, rowWidth, StripSpacing);

        Assert.Equal(widths.Length, lines.Sum(l => l.Count));   // nothing dropped
        Assert.All(lines, line =>
            Assert.True(LineWidth(line, widths) <= rowWidth,
                $"a line of {LineWidth(line, widths)} DIP was placed in a {rowWidth} DIP row — the "
                + "overflow is clipped out of the card and cannot be scrolled to"));
    }

    /// <summary>
    /// The Override row at the window's own minimum width — the reported symptom, in numbers. All three
    /// controls need ~456 DIP; the row has 288. They must wrap onto more than one line rather than two of
    /// them being clipped away.
    /// </summary>
    [Fact]
    public void WrapStrip_WrapsTheOverrideRowAtTheMinimumWindowWidth()
    {
        double rowWidth = WindowPlacement.SettingsMinWidth
                        - WindowPlacement.SettingsChromeWidth
                        - WindowPlacement.SettingsCardWidth;
        double[] widths = [160, 160, 120];

        var lines = WindowPlacement.WrapStrip(widths, rowWidth, StripSpacing);

        Assert.True(lines.Count > 1, "the row does not fit on one line, so it must use more than one");
        Assert.All(lines, line => Assert.True(LineWidth(line, widths) <= rowWidth));
    }

    /// <summary>Order is preserved: wrapping is a line break, not a reshuffle.</summary>
    [Fact]
    public void WrapStrip_KeepsChildrenInOrder()
    {
        double[] widths = [160, 160, 120];
        var flattened = WindowPlacement.WrapStrip(widths, 288, StripSpacing).SelectMany(l => l).ToList();
        Assert.Equal([0, 1, 2], flattened);
    }

    /// <summary>When everything fits, the layout is the single line the StackPanel gave — nothing changes
    /// at a comfortable width.</summary>
    [Fact]
    public void WrapStrip_KeepsOneLineWhenEverythingFits()
    {
        double[] widths = [160, 160, 120];
        var lines = WindowPlacement.WrapStrip(widths, 1000, StripSpacing);
        Assert.Equal([0, 1, 2], Assert.Single(lines));
    }

    /// <summary>
    /// A child wider than the whole row gets a line to itself — there is nowhere better for it, and it
    /// must not drag a sibling off the edge with it.
    /// </summary>
    [Fact]
    public void WrapStrip_GivesAnOversizeChildItsOwnLine()
    {
        double[] widths = [400, 100];
        var lines = WindowPlacement.WrapStrip(widths, 288, StripSpacing);

        Assert.Equal(2, lines.Count);
        Assert.Equal([0], lines[0]);
        Assert.Equal([1], lines[1]);
    }

    /// <summary>
    /// A not-yet-measured or unbounded row is "no decision yet — one line", the same reasoning
    /// <see cref="WindowPlacement.ShouldStackSettingRow"/> applies to a non-positive width. Wrapping every
    /// child onto its own line during the first measure pass would be a visible flap.
    /// </summary>
    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(0)]
    public void WrapStrip_UsesOneLineWhenTheWidthIsUnknown(double width)
    {
        double[] widths = [160, 160, 120];
        Assert.Equal([0, 1, 2], Assert.Single(WindowPlacement.WrapStrip(widths, width, StripSpacing)));
    }

    [Fact]
    public void WrapStrip_HandlesNoChildren()
        => Assert.Empty(WindowPlacement.WrapStrip([], 288, StripSpacing));
}
