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
        Assert.Equal(384, WindowPlacement.SettingsChromeWidth);          // 320 nav pane + 48 padding + 16 scrollbar
        Assert.Equal(30,  WindowPlacement.SettingsCardWidth);            // 14+14 padding + 1+1 border
        Assert.Equal(288, WindowPlacement.SettingRowWidestFixedControl); // 150 action + 8 + 130 delay
        Assert.Equal(702, WindowPlacement.SettingsMinWidth);
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
}
