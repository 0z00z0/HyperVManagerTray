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
    [InlineData(TrayIconState.Bridged,  "icon-bridged-v5.ico")]
    [InlineData(TrayIconState.Fallback, "icon-fallback-v5.ico")]
    public void GenerateAndSave_CreatesExpectedFile(TrayIconState state, string expectedFileName)
    {
        var path = IconGenerator.GenerateAndSave(_dir, state);

        Assert.Equal(Path.Combine(_dir, expectedFileName), path);
        Assert.True(File.Exists(path), $"Expected icon file was not created: {path}");
        Assert.True(new FileInfo(path).Length > 0, "Icon file is empty");
    }

    // ── The three states write to three distinct files ───────────────────────────

    [Fact]
    public void GenerateAndSave_ThreeStates_ProduceDifferentFiles()
    {
        var p1 = IconGenerator.GenerateAndSave(_dir, TrayIconState.Unknown);
        var p2 = IconGenerator.GenerateAndSave(_dir, TrayIconState.Bridged);
        var p3 = IconGenerator.GenerateAndSave(_dir, TrayIconState.Fallback);

        Assert.NotEqual(p1, p2);
        Assert.NotEqual(p2, p3);
        Assert.NotEqual(p1, p3);
    }

    // ── Second call with the same state returns the same path without recreating ─

    [Theory]
    [InlineData(TrayIconState.Unknown)]
    [InlineData(TrayIconState.Bridged)]
    [InlineData(TrayIconState.Fallback)]
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
    public void RenderIcon_DrawsOpaqueGlyph(TrayIconState state)
    {
        using var bmp = IconGenerator.RenderIcon(32, state);

        bool anyOpaque = false;
        for (int y = 0; y < bmp.Height && !anyOpaque; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).A == 255) { anyOpaque = true; break; }

        Assert.True(anyOpaque, "Expected the glyph to draw at least one fully-opaque pixel.");
    }

    // Bridged is green-dominant, Fallback blue-dominant — the colour encodes the state.
    [Fact]
    public void RenderIcon_StateColoursAreDistinctAndCorrectHue()
    {
        var bridged  = DominantGlyphColor(TrayIconState.Bridged);
        var fallback = DominantGlyphColor(TrayIconState.Fallback);

        Assert.True(bridged.G  > bridged.R  && bridged.G  > bridged.B,  $"Bridged should be green-dominant, was {bridged}");
        Assert.True(fallback.B > fallback.R && fallback.B > fallback.G, $"Fallback should be blue-dominant, was {fallback}");
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
