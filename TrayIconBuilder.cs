using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HyperVNetworkSwitcher;

internal static class TrayIconBuilder
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);

    // Blue  = bridged to physical LAN
    // Grey  = NAT / Default Switch
    internal static Icon Build(bool bridged)
    {
        var accent = bridged
            ? Color.FromArgb(0, 120, 215)    // Windows blue
            : Color.FromArgb(90, 95, 107);   // neutral grey

        using var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode    = SmoothingMode.AntiAlias;
            g.PixelOffsetMode  = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // Rounded background
            using (var bgBrush = new SolidBrush(accent))
            using (var bgPath  = RoundedRect(new RectangleF(0.5f, 0.5f, 14.5f, 14.5f), 3f))
                g.FillPath(bgBrush, bgPath);

            using var white    = new SolidBrush(Color.White);
            using var thinPen  = new Pen(Color.White, 1.0f);
            using var thickPen = new Pen(Color.White, 2.0f);

            // ── Three port dots across the top ──────────────────────────
            g.FillEllipse(white,  1.5f, 1.5f, 3f, 3f);
            g.FillEllipse(white,  6.5f, 1.5f, 3f, 3f);
            g.FillEllipse(white, 11.0f, 1.5f, 3f, 3f);

            // ── Vertical stubs from ports down to backplane ─────────────
            g.DrawLine(thinPen,  3.0f, 4.5f,  3.0f, 7.0f);
            g.DrawLine(thinPen,  8.0f, 4.5f,  8.0f, 7.0f);
            g.DrawLine(thinPen, 12.5f, 4.5f, 12.5f, 7.0f);

            // ── Switch backplane (thick horizontal bar) ──────────────────
            g.DrawLine(thickPen, 1.5f, 7.5f, 14.5f, 7.5f);

            // ── Uplink cable: backplane → VM ─────────────────────────────
            g.DrawLine(thinPen, 8.0f, 8.5f, 8.0f, 11.0f);

            // ── VM box at the bottom ─────────────────────────────────────
            g.DrawRectangle(thinPen, 4.0f, 11.0f, 8.0f, 3.5f);
            // Screen-divider line inside box
            g.DrawLine(thinPen, 4.5f, 12.5f, 11.5f, 12.5f);
        }

        var hIcon = bmp.GetHicon();
        try   { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X,              r.Y,               d, d, 180, 90);
        path.AddArc(r.Right - d,      r.Y,               d, d, 270, 90);
        path.AddArc(r.Right - d,      r.Bottom - d,      d, d,   0, 90);
        path.AddArc(r.X,              r.Bottom - d,      d, d,  90, 90);
        path.CloseFigure();
        return path;
    }
}
