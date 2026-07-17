using System.Drawing;
using System.Drawing.Imaging;
using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

public class IconGeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public IconGeneratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Each state produces the correct named file ──────────────────────────────

    [Theory]
    [InlineData(TrayIconState.Unknown,  "icon-unknown-v5.ico")]
    [InlineData(TrayIconState.Starting, "icon-starting-v5.ico")]   // issue #56
    [InlineData(TrayIconState.Bridged,  "icon-bridged-v5.ico")]
    [InlineData(TrayIconState.Fallback, "icon-fallback-v5.ico")]
    [InlineData(TrayIconState.Failed,   "icon-failed-v5.ico")]     // issue #37
    public void GenerateAndSave_CreatesExpectedFile(TrayIconState state, string expectedFileName)
    {
        var path = IconGenerator.GenerateAndSave(_dir, state);

        Assert.Equal(Path.Combine(_dir, expectedFileName), path);
        Assert.True(File.Exists(path), $"Expected icon file was not created: {path}");
        Assert.True(new FileInfo(path).Length > 0, "Icon file is empty");
    }

    // ── Every state writes to its own distinct file ──────────────────────────────

    [Fact]
    public void GenerateAndSave_EveryState_ProducesADifferentFile()
    {
        // Enumerated rather than listed, so a state added later must also get its own icon file
        // instead of silently sharing (and therefore mis-signalling as) another state's.
        var paths = Enum.GetValues<TrayIconState>()
            .Select(state => IconGenerator.GenerateAndSave(_dir, state))
            .ToList();

        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    // ── Second call with the same state returns the same path without recreating ─

    [Theory]
    [InlineData(TrayIconState.Unknown)]
    [InlineData(TrayIconState.Starting)]
    [InlineData(TrayIconState.Bridged)]
    [InlineData(TrayIconState.Fallback)]
    [InlineData(TrayIconState.Failed)]
    public void GenerateAndSave_CalledTwice_ReturnsSamePathAndDoesNotRewrite(TrayIconState state)
    {
        var first    = IconGenerator.GenerateAndSave(_dir, state);
        var writtenAt = File.GetLastWriteTimeUtc(first);

        var second = IconGenerator.GenerateAndSave(_dir, state);

        Assert.Equal(first, second);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(second));
    }

    // ── Deleting the cached file causes it to be regenerated ─────────────────────

    [Fact]
    public void GenerateAndSave_AfterDeletion_RecreatesFile()
    {
        var path = IconGenerator.GenerateAndSave(_dir, TrayIconState.Bridged);
        File.Delete(path);

        var recreated = IconGenerator.GenerateAndSave(_dir, TrayIconState.Bridged);

        Assert.Equal(path, recreated);
        Assert.True(File.Exists(recreated));
    }

    // ── New v4 requirements: transparent background + coloured glyph ─────────────

    // The background must be fully transparent so the icon reads on light AND dark taskbars.
    [Theory]
    [InlineData(TrayIconState.Unknown)]
    [InlineData(TrayIconState.Starting)]
    [InlineData(TrayIconState.Bridged)]
    [InlineData(TrayIconState.Fallback)]
    [InlineData(TrayIconState.Failed)]
    public void RenderIcon_HasTransparentBackground(TrayIconState state)
    {
        using var bmp = IconGenerator.RenderIcon(32, state);

        // All four corners sit outside the glyph and must be fully transparent.
        foreach (var (x, y) in new[] { (0, 0), (31, 0), (0, 31), (31, 31) })
            Assert.Equal(0, bmp.GetPixel(x, y).A);
    }

    // The glyph itself must actually be drawn (opaque pixels present somewhere).
    [Theory]
    [InlineData(TrayIconState.Unknown)]
    [InlineData(TrayIconState.Starting)]
    [InlineData(TrayIconState.Bridged)]
    [InlineData(TrayIconState.Fallback)]
    [InlineData(TrayIconState.Failed)]
    public void RenderIcon_DrawsOpaqueGlyph(TrayIconState state)
    {
        using var bmp = IconGenerator.RenderIcon(32, state);

        bool anyOpaque = false;
        for (int y = 0; y < bmp.Height && !anyOpaque; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).A == 255) { anyOpaque = true; break; }

        Assert.True(anyOpaque, "Expected the glyph to draw at least one fully-opaque pixel.");
    }

    // Bridged is green-dominant, Fallback blue-dominant, Failed red-dominant — the colour encodes the
    // state. Failed (issue #37) must read as a problem, not as either working state.
    [Fact]
    public void RenderIcon_StateColoursAreDistinctAndCorrectHue()
    {
        var bridged  = DominantGlyphColor(TrayIconState.Bridged);
        var fallback = DominantGlyphColor(TrayIconState.Fallback);
        var failed   = DominantGlyphColor(TrayIconState.Failed);

        Assert.True(bridged.G  > bridged.R  && bridged.G  > bridged.B,  $"Bridged should be green-dominant, was {bridged}");
        Assert.True(fallback.B > fallback.R && fallback.B > fallback.G, $"Fallback should be blue-dominant, was {fallback}");
        Assert.True(failed.R   > failed.G   && failed.R   > failed.B,   $"Failed should be red-dominant, was {failed}");
    }

    // Every state must be visually distinguishable from every other — a failed apply that renders in the
    // same colour as a working one would defeat the whole point of issue #37.
    [Fact]
    public void RenderIcon_EveryStateHasItsOwnColour()
    {
        var colours = Enum.GetValues<TrayIconState>().Select(DominantGlyphColor).ToList();

        Assert.Equal(colours.Count, colours.Distinct().Count());
    }

    // Grey "we don't know yet" and red "we tried and failed" are deliberately NOT merged: they call for
    // different reactions from the user (wait vs. investigate).
    [Fact]
    public void RenderIcon_UnknownAndFailedAreDifferentColours() =>
        Assert.NotEqual(DominantGlyphColor(TrayIconState.Unknown), DominantGlyphColor(TrayIconState.Failed));

    // ── Starting (issue #56) ────────────────────────────────────────────────────

    /// <summary>
    /// Issue #56, as a pixel. "Still looking" and "looked, don't know" were the same grey for ~8 s at
    /// logon, so the app's honesty was indistinguishable from a hang. They must not share a colour, and
    /// (RenderIcon_EveryStateHasItsOwnColour above enumerates the same rule for all five states) neither
    /// may Starting share one with anything else.
    /// </summary>
    [Fact]
    public void RenderIcon_StartingAndUnknownAreDifferentColours() =>
        Assert.NotEqual(DominantGlyphColor(TrayIconState.Starting), DominantGlyphColor(TrayIconState.Unknown));

    /// <summary>
    /// Starting must not read as either CONFIRMED colour — the pre-#37 defect (an unconfirmed state
    /// wearing a success colour) would be no less a lie for happening in the first 8 s. The rule is
    /// enforced upstream by NetworkStatusUi.IconFor's enumerating guard, which decides that Starting is
    /// never Bridged/Fallback; this pins the other half, that the amber it does get cannot be MISTAKEN
    /// for one. Green- and blue-dominance are the signatures those two states are tested by
    /// (RenderIcon_StateColoursAreDistinctAndCorrectHue), so amber must be neither: R-dominant puts it on
    /// the "not a success colour" side of the palette by construction, not by luck.
    /// </summary>
    [Fact]
    public void RenderIcon_StartingIsNotASuccessColour()
    {
        var starting = DominantGlyphColor(TrayIconState.Starting);

        Assert.False(starting.G > starting.R && starting.G > starting.B,
            $"Starting is green-dominant ({starting}) — it reads as the confirmed 'bridged' state.");
        Assert.False(starting.B > starting.R && starting.B > starting.G,
            $"Starting is blue-dominant ({starting}) — it reads as the confirmed 'fallback' state.");
    }

    /// <summary>
    /// Amber and red are the closest pair in this palette — both warm, both R-dominant — so "they are
    /// different Color values" (which EveryStateHasItsOwnColour already checks) is far too weak a bar
    /// here: an orange one shade off Failed would pass it while telling the user their network had
    /// broken every time the app launched. The green channel is what separates a gold from a red, so the
    /// gap is asserted there, and asserted WIDE.
    /// </summary>
    [Fact]
    public void RenderIcon_StartingIsClearlyNotFailed()
    {
        var starting = DominantGlyphColor(TrayIconState.Starting);
        var failed   = DominantGlyphColor(TrayIconState.Failed);

        Assert.True(starting.G - failed.G > 60,
            $"Starting {starting} is too close to Failed {failed}: only {starting.G - failed.G} apart in the " +
            "green channel. A 'still starting up' icon that reads as red tells the user their network is " +
            "broken every single logon (issue #56).");
    }

    // Averages every fully-opaque glyph pixel to get the icon's dominant colour.
    private static Color DominantGlyphColor(TrayIconState state)
    {
        using var bmp = IconGenerator.RenderIcon(48, state);
        long r = 0, g = 0, b = 0, n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.A < 255) continue;
                r += p.R; g += p.G; b += p.B; n++;
            }
        Assert.True(n > 0, "No opaque glyph pixels found.");
        return Color.FromArgb(255, (int)(r / n), (int)(g / n), (int)(b / n));
    }
}
