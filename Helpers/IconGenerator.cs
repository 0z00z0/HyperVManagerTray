using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace HyperVNetworkSwitcher.Helpers;

/// <summary>
/// Generates the tray icon at runtime (no image assets): a small network-switch glyph on a
/// rounded background — blue when the VM is bridged to a physical LAN, grey for NAT/fallback.
/// Two multi-size .ico files are written next to the exe and swapped on state changes; writing
/// to disk lets H.NotifyIcon reload them and avoids the GDI handle leak of <c>Bitmap.GetHicon()</c>.
/// </summary>
internal static class IconGenerator
{
    // Frame sizes baked into each .ico — covers 100/125/150/200 % tray DPI without upscaling.
    private static readonly int[] IconSizes = [32, 24, 20, 16];

    private static readonly Color BridgedAccent  = Color.FromArgb(0, 120, 215);   // Windows blue
    private static readonly Color FallbackAccent  = Color.FromArgb(90, 95, 107);   // neutral grey

    /// <summary>
    /// Generates (once) and returns the path to the tray icon for the given state.
    /// <paramref name="bridged"/> selects the blue icon; otherwise the grey one.
    /// </summary>
    internal static string GenerateAndSave(string outputDirectory, bool bridged)
    {
        var name    = bridged ? "switch-blue.ico" : "switch-grey.ico";
        var icoPath = Path.Combine(outputDirectory, name);
        if (!File.Exists(icoPath))
            SaveAsIco(icoPath, bridged ? BridgedAccent : FallbackAccent);
        return icoPath;
    }

    // ── Rendering ───────────────────────────────────────────────────────────────

    // The glyph is authored in a 16-unit coordinate space (from the original WinForms icon)
    // and scaled to each native frame size so it stays crisp down to 16 px.
    private static Bitmap RenderIconBitmap(int size, Color accent)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 16f, size / 16f);

        // Rounded background
        using (var bgBrush = new SolidBrush(accent))
        using (var bgPath  = RoundedRect(new RectangleF(0.5f, 0.5f, 14.5f, 14.5f), 3f))
            g.FillPath(bgBrush, bgPath);

        using var white    = new SolidBrush(Color.White);
        using var thinPen  = new Pen(Color.White, 1.0f);
        using var thickPen = new Pen(Color.White, 2.0f);

        // Three port dots across the top
        g.FillEllipse(white,  1.5f, 1.5f, 3f, 3f);
        g.FillEllipse(white,  6.5f, 1.5f, 3f, 3f);
        g.FillEllipse(white, 11.0f, 1.5f, 3f, 3f);

        // Vertical stubs from ports down to backplane
        g.DrawLine(thinPen,  3.0f, 4.5f,  3.0f, 7.0f);
        g.DrawLine(thinPen,  8.0f, 4.5f,  8.0f, 7.0f);
        g.DrawLine(thinPen, 12.5f, 4.5f, 12.5f, 7.0f);

        // Switch backplane (thick horizontal bar)
        g.DrawLine(thickPen, 1.5f, 7.5f, 14.5f, 7.5f);

        // Uplink cable: backplane → VM
        g.DrawLine(thinPen, 8.0f, 8.5f, 8.0f, 11.0f);

        // VM box at the bottom + screen-divider line
        g.DrawRectangle(thinPen, 4.0f, 11.0f, 8.0f, 3.5f);
        g.DrawLine(thinPen, 4.5f, 12.5f, 11.5f, 12.5f);

        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X,         r.Y,          d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y,          d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90);
        path.AddArc(r.X,         r.Bottom - d, d, d,  90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Writes a valid ICO with one PNG-compressed frame per size (Vista+ supports PNG-in-ICO).</summary>
    private static void SaveAsIco(string filePath, Color accent)
    {
        var frames = Array.ConvertAll(IconSizes, s =>
        {
            using var bmp = RenderIconBitmap(s, accent);
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
            bw.Write((byte)IconSizes[i]);  // width (0 = 256)
            bw.Write((byte)IconSizes[i]);  // height
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
