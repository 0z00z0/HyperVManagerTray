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
    [InlineData(TrayIconState.Unknown,  "icon-unknown-v6.ico")]
    [InlineData(TrayIconState.Bridged,  "icon-bridged-v6.ico")]
    [InlineData(TrayIconState.Fallback, "icon-fallback-v6.ico")]
    [InlineData(TrayIconState.Failed,   "icon-failed-v6.ico")]     // issue #37
    [InlineData(TrayIconState.BridgeNoUplink, "icon-bridge-no-uplink-v6.ico")]
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
    [InlineData(TrayIconState.Bridged)]
    [InlineData(TrayIconState.Fallback)]
    [InlineData(TrayIconState.Failed)]
    [InlineData(TrayIconState.BridgeNoUplink)]
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
    [InlineData(TrayIconState.Bridged)]
    [InlineData(TrayIconState.Fallback)]
    [InlineData(TrayIconState.Failed)]
    [InlineData(TrayIconState.BridgeNoUplink)]
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
    [InlineData(TrayIconState.Bridged)]
    [InlineData(TrayIconState.Fallback)]
    [InlineData(TrayIconState.Failed)]
    [InlineData(TrayIconState.BridgeNoUplink)]
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
        var noUplink = DominantGlyphColor(TrayIconState.BridgeNoUplink);

        Assert.True(bridged.G  > bridged.R  && bridged.G  > bridged.B,  $"Bridged should be green-dominant, was {bridged}");
        Assert.True(fallback.B > fallback.R && fallback.B > fallback.G, $"Fallback should be blue-dominant, was {fallback}");
        // Amber: warm like Failed, but with far more green in it. Both clauses matter — the first keeps
        // it off the cool "working" side of the palette, the second keeps a degraded bridge from reading
        // at a glance as the red that means something failed and needs looking at now.
        Assert.True(noUplink.R > noUplink.B && noUplink.G > noUplink.B,
                    $"A bridge with no uplink should be amber, was {noUplink}");
        Assert.True(noUplink.G > failed.G,
                    $"Amber should be clearly warmer-yellow than red ({noUplink} vs {failed})");
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

    // ── "Starting" is deliberately NOT an icon state (issue #58) ─────────────────
    //
    // Issue #56 gave the startup window its own amber icon; on a real taskbar amber reads as
    // "network degraded", so the app announced a fault at every logon. #58 deleted the state and maps
    // the startup window to grey Unknown instead — the icon makes no claim about the network either way,
    // and the "starting up" distinction now lives on the tooltip (NetworkStatusUiTests covers that).
    //
    // Amber exists again, and this is not that decision coming back. #58's objection was that the colour
    // announced a degraded network during a window in which nothing was wrong; it now marks a bridge
    // whose uplink is confirmed down, which is a degraded network and nothing else. The test above pins
    // the hue, and the enumerating tests (EveryState_ProducesADifferentFile, EveryStateHasItsOwnColour)
    // keep every state on its own file and its own colour.

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
