using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace HyperVManagerTray.Helpers;

/// <summary>Three tray icon states reflected as the glyph colour.</summary>
public enum TrayIconState
{
    Unknown,  // grey  — startup / no data yet
    Bridged,  // green — VM on physical LAN
    Fallback, // blue  — VM on Default Switch / NAT
}

/// <summary>
/// Generates the tray icon at runtime (no image assets): a minimalist "virtual machine" glyph —
/// a hollow monitor/display with two content bars and a stand — drawn in a single muted colour
/// on a fully transparent background.  The colour signals state (grey = unknown, green = bridged
/// to physical LAN, blue = NAT/fallback); the transparent background lets the same icon read on
/// both light and dark taskbars.  Colours are intentionally medium-luminance, not vivid, so the
/// glyph's edges stay crisp against either backdrop.
///
/// Three multi-size .ico files are written next to the exe and swapped on state changes; writing
/// to disk lets H.NotifyIcon reload them and avoids the GDI handle leak of Bitmap.GetHicon().
///
/// Icon version: v4 — transparent background, coloured VM-monitor glyph (replaces v3's coloured
/// tile).  The version suffix forces regeneration on first run after an upgrade.
/// </summary>
internal static class IconGenerator
{
    // v4 — rename forces regeneration on first run after upgrade; old v2/v3 files are ignored.
    private const string UnknownFile  = "icon-unknown-v4.ico";
    private const string BridgedFile  = "icon-bridged-v4.ico";
    private const string FallbackFile = "icon-fallback-v4.ico";

    // Frame sizes baked into each .ico.  64/48 are picked by Windows on 4K (200 %+ DPI)
    // without upscaling; 32/24/20/16 cover 100–150 % tray DPI.
    private static readonly int[] IconSizes = [64, 48, 32, 24, 20, 16];

    // Glyph colours — one per state.  Medium luminance (not vivid) so the shape stays legible
    // on both white and dark taskbars.
    private static readonly Color GlyphUnknown  = Color.FromArgb(255, 0x8C, 0x8C, 0x8C);  // muted grey
    private static readonly Color GlyphBridged  = Color.FromArgb(255, 0x35, 0x9E, 0x6A);  // muted green
    private static readonly Color GlyphFallback = Color.FromArgb(255, 0x3B, 0x7E, 0xC4);  // muted blue

    /// <summary>
    /// Returns the path to the .ico for the given state, generating it on first call.
    /// </summary>
    internal static string GenerateAndSave(string outputDirectory, TrayIconState state)
    {
        var file = state switch
        {
            TrayIconState.Bridged  => BridgedFile,
            TrayIconState.Fallback => FallbackFile,
            _                      => UnknownFile,
        };
        var icoPath = Path.Combine(outputDirectory, file);
        if (!File.Exists(icoPath))
            SaveAsIco(icoPath, state);
        return icoPath;
    }

    private static Color ColorFor(TrayIconState state) => state switch
    {
        TrayIconState.Bridged  => GlyphBridged,
        TrayIconState.Fallback => GlyphFallback,
        _                      => GlyphUnknown,
    };

    // ── Rendering ───────────────────────────────────────────────────────────────

    // Glyph is designed in a 16-unit logical space and scaled to each frame size.  Everything is
    // drawn with filled shapes (no thin strokes) so it stays crisp down to 16 px.  Layout:
    //   ┌──────────────────────┐   ← hollow monitor frame (transparent centre)
    //   │  ▭▭▭▭▭▭▭▭▭▭▭▭▭▭▭▭▭▭  │   ← content bar 1
    //   │  ▭▭▭▭▭▭▭▭▭▭          │   ← content bar 2
    //   └──────────┬───────────┘
    //          ▭▭▭▭▭▭▭▭▭            ← stand neck + foot
    /// <summary>Renders the tray glyph for <paramref name="state"/> at the given pixel size (transparent background).</summary>
    internal static Bitmap RenderIcon(int size, TrayIconState state)
    {
        var color = ColorFor(state);
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);   // transparent background — works on light & dark taskbars
        g.ScaleTransform(size / 16f, size / 16f);

        using var fill = new SolidBrush(color);

        // ── Hollow monitor frame (outer ∖ inner via alternate fill = a ring) ──
        using (var frame = new GraphicsPath(FillMode.Alternate))
        {
            AddRoundedRect(frame, new RectangleF(1.4f, 2.2f, 13.2f, 8.8f), 1.8f);  // outer bezel
            AddRoundedRect(frame, new RectangleF(2.9f, 3.7f, 10.2f, 5.8f), 1.0f);  // screen cut-out
            g.FillPath(fill, frame);
        }

        // ── Screen content bars (inside the transparent cut-out) ─────────────
        using (var bars = new GraphicsPath())
        {
            AddRoundedRect(bars, new RectangleF(4.1f, 5.0f, 6.8f, 1.0f), 0.5f);  // long bar
            AddRoundedRect(bars, new RectangleF(4.1f, 7.0f, 4.3f, 1.0f), 0.5f);  // short bar
            g.FillPath(fill, bars);
        }

        // ── Stand: neck + foot ───────────────────────────────────────────────
        using (var stand = new GraphicsPath())
        {
            AddRoundedRect(stand, new RectangleF(7.0f, 11.0f, 2.0f, 1.5f), 0.3f);  // neck
            AddRoundedRect(stand, new RectangleF(4.8f, 12.2f, 6.4f, 1.4f), 0.7f);  // foot
            g.FillPath(fill, stand);
        }

        return bmp;
    }

    private static void AddRoundedRect(GraphicsPath path, RectangleF r, float radius)
    {
        var d = radius * 2;
        path.StartFigure();
        path.AddArc(r.X,         r.Y,          d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y,          d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90);
        path.AddArc(r.X,         r.Bottom - d, d, d,  90, 90);
        path.CloseFigure();
    }

    /// <summary>Writes a valid ICO with one PNG-compressed frame per size (Vista+ PNG-in-ICO).</summary>
    private static void SaveAsIco(string filePath, TrayIconState state)
    {
        var frames = Array.ConvertAll(IconSizes, s =>
        {
            using var bmp = RenderIcon(s, state);
            using var ms  = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        });

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write((short)0);                // reserved
        bw.Write((short)1);                // type: icon
        bw.Write((short)IconSizes.Length); // image count

        int dataOffset = 6 + IconSizes.Length * 16;
        for (int i = 0; i < IconSizes.Length; i++)
        {
            var sz = IconSizes[i];
            bw.Write((byte)(sz >= 256 ? 0 : sz)); // 0 encodes 256 in ICO format
            bw.Write((byte)(sz >= 256 ? 0 : sz));
            bw.Write((byte)0);             // colour count
            bw.Write((byte)0);             // reserved
            bw.Write((short)1);            // colour planes
            bw.Write((short)32);           // bits per pixel
            bw.Write(frames[i].Length);    // data size
            bw.Write(dataOffset);          // data offset
            dataOffset += frames[i].Length;
        }

        foreach (var frame in frames)
            bw.Write(frame);
    }
}
