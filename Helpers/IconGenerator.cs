using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Four tray icon states reflected as the glyph colour.
///
/// <para>The grey/red split is load-bearing (issue #37): <see cref="Unknown"/> means the app has not
/// established where the VMs are, <see cref="Failed"/> means it tried and did not get there. Both are
/// honest non-success states, but they call for different user reactions, so they are not merged —
/// and neither may ever be rendered as <see cref="Bridged"/>. See
/// <see cref="NetworkStatusUi.IconFor"/>, which is the only thing that should choose between these.</para>
///
/// <para><b>There is deliberately no "starting" state (issue #58).</b> Issue #56 added an amber one for
/// the window between the tray icon appearing and the first apply pass confirming an outcome. On a real
/// taskbar amber reads as "network degraded" — the tray's warning convention — so an app that had merely
/// not finished looking yet announced a problem at every logon. The amber was also forced rather than
/// chosen: <c>RenderIcon_EveryStateHasItsOwnColour</c> requires a new state to bring a new hue.
///
/// <para>The resolution is that "the first pass is in flight" was never a distinct ICON state to begin
/// with. <see cref="Unknown"/> means <i>no claim about the network</i>, and that is exactly true while the
/// app is still looking. Same claim, same pixel. What genuinely differs between the two is the REASON
/// there is nothing to claim, and that is a sentence, not a colour — it lives on the tray tooltip, where
/// <c>NetworkStatusUi.SwitchApplyStatus.Starting</c> still distinguishes "starting up" from "not applied
/// yet". Deleting the state rather than exempting it from the one-colour-per-state rule is the point:
/// there is now nothing to exempt, so the guard stays intact and honest.</para></para>
/// </summary>
public enum TrayIconState
{
    Unknown,  // grey  — no claim about the network: not established yet, or the first pass is still in flight (#58)
    Bridged,  // green — CONFIRMED: VM on physical LAN
    Fallback, // blue  — CONFIRMED: VM on Default Switch / NAT
    Failed,   // red   — the switch bind or a VM-NIC reconnect FAILED (issue #37)
}

/// <summary>
/// Generates the tray icon at runtime (no image assets): the "route fork" glyph — a virtual machine
/// above a network line that forks to a square end (physical LAN) and a round end (NAT) — drawn in a
/// single muted colour on a fully transparent background.  State is carried twice, by colour and by
/// shape: grey with a hollow screen and no lit branch = no claim about the network, green with the
/// square end lit = bridged to the physical LAN, blue with the round end lit = NAT/fallback, red with a
/// cross in place of the fork = the apply failed.  The transparent background lets the same icon read
/// on both light and dark taskbars.  Colours are intentionally medium-luminance, not vivid, so the
/// glyph's edges stay crisp against either backdrop.
///
/// One colour per state is a rule, not a coincidence: colour is the channel that reads at a glance, and
/// two states sharing one would be told apart only by a few pixels at 16 px
/// (RenderIcon_EveryStateHasItsOwnColour enumerates it).  Issue #58 removed the amber "starting" state
/// rather than weakening that rule — see TrayIconState.
///
/// Four multi-size .ico files are written next to the exe and swapped on state changes; writing
/// to disk lets H.NotifyIcon reload them and avoids the GDI handle leak of Bitmap.GetHicon().
///
/// Icon version: v6 — the route-fork glyph.  installer\RouteGlyph.ps1 draws the same 16-unit geometry,
/// white on a blue plate, for AppIcon.ico, and flat blue for the Start-menu shortcut's TrayBlue.ico, so
/// the tray glyphs and the app icon are one family.  The version suffix forces regeneration on first
/// run after an upgrade.
/// </summary>
internal static class IconGenerator
{
    // v6 — rename forces regeneration on first run after upgrade; older files are ignored.
    private const string UnknownFile  = "icon-unknown-v6.ico";
    private const string BridgedFile  = "icon-bridged-v6.ico";
    private const string FallbackFile = "icon-fallback-v6.ico";
    private const string FailedFile   = "icon-failed-v6.ico";   // issue #37

    // Frame sizes baked into each .ico.  64/48 are picked by Windows on 4K (200 %+ DPI)
    // without upscaling; 32/24/20/16 cover 100–150 % tray DPI.
    private static readonly int[] IconSizes = [64, 48, 32, 24, 20, 16];

    // Glyph colours — one per state.  Medium luminance (not vivid) so the shape stays legible
    // on both white and dark taskbars.
    private static readonly Color GlyphUnknown  = Color.FromArgb(255, 0x8C, 0x8C, 0x8C);  // muted grey
    private static readonly Color GlyphBridged  = Color.FromArgb(255, 0x35, 0x9E, 0x6A);  // muted green
    private static readonly Color GlyphFallback = Color.FromArgb(255, 0x3B, 0x7E, 0xC4);  // muted blue
    // Muted red (issue #37) — same medium-luminance treatment as the other three so the glyph edges
    // stay crisp on a white taskbar, but unmistakably a different hue from the green/blue "confirmed"
    // colours AND from the grey "don't know yet".
    private static readonly Color GlyphFailed   = Color.FromArgb(255, 0xC4, 0x45, 0x3B);

    /// <summary>
    /// Returns the path to the .ico for the given state, generating it on first call.
    /// </summary>
    internal static string GenerateAndSave(string outputDirectory, TrayIconState state)
    {
        var file = state switch
        {
            TrayIconState.Bridged  => BridgedFile,
            TrayIconState.Fallback => FallbackFile,
            TrayIconState.Failed   => FailedFile,
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
        TrayIconState.Failed   => GlyphFailed,
        _                      => GlyphUnknown,
    };

    // ── Rendering ───────────────────────────────────────────────────────────────

    // Opacity of the branch and end that are NOT the current route (38 %), so the lit route reads first.
    private const int DimAlpha = 97;

    // "Route fork" glyph, designed in a 16-unit logical space with a half-unit margin on every side:
    //
    //        ┌─────────┐        ← virtual machine: rounded frame with a slot (a hollow screen while Unknown)
    //        └────┬────┘
    //            ╱ ╲            ← the network line forks
    //         ■       ●         ← square end = physical LAN (Bridged), round end = NAT (Fallback)
    //
    // The lit branch and its solid end show the current route; the other branch and a hollow end are
    // drawn at 38 % opacity.  Unknown lights neither branch and hollows the screen; Failed replaces the
    // fork with a cross.  Every edge is snapped to the pixel grid of the frame being drawn, so the
    // horizontal and vertical edges stay one colour at 16 px instead of smearing across two pixels.
    //
    // installer\RouteGlyph.ps1 draws the identical geometry for the application icon, the Start-menu
    // shortcut icon and the installer wizard images; keep the two in sync if either changes.
    /// <summary>Renders the tray glyph for <paramref name="state"/> at the given pixel size (transparent background).</summary>
    internal static Bitmap RenderIcon(int size, TrayIconState state)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;   // pixel i spans [i, i+1], so integer edges are crisp
        g.Clear(Color.Transparent);   // transparent background — works on light & dark taskbars

        DrawRouteGlyph(g, new Grid(0f, size / 16f, size / 2f), ColorFor(state), state);
        return bmp;
    }

    /// <summary>
    /// Maps the 16-unit design space onto pixels: <c>origin + unit × scale</c>, with every coordinate
    /// rounded to a whole pixel, halves rounded away from the canvas centre so the glyph stays
    /// symmetric about its stem.  Widths round to the nearest whole pixel, at least one.
    /// </summary>
    private readonly record struct Grid(float Origin, float Scale, float Centre)
    {
        public float P(float unit)
        {
            var d = Origin + unit * Scale - Centre;
            return Centre + MathF.Sign(d) * MathF.Round(MathF.Abs(d), MidpointRounding.AwayFromZero);
        }

        public float W(float units) => MathF.Max(1f, MathF.Round(units * Scale, MidpointRounding.ToEven));
    }

    private static void DrawRouteGlyph(Graphics g, Grid u, Color color, TrayIconState state)
    {
        var dim = Color.FromArgb(DimAlpha, color);
        using var solid = new SolidBrush(color);
        using var faint = new SolidBrush(dim);

        // ── Virtual machine: frame with a slot, or a hollow screen while nothing is known ──
        using (var vm = new GraphicsPath(FillMode.Alternate))
        {
            AddRoundedRect(vm, Edges(u.P(3.4f), u.P(0.5f), u.P(12.6f), u.P(6.9f)), 1.5f * u.Scale);
            if (state == TrayIconState.Unknown)
                AddRoundedRect(vm, Edges(u.P(4.8f), u.P(1.9f), u.P(11.2f), u.P(5.5f)), 0.5f * u.Scale);
            else
                AddRoundedRect(vm, Edges(u.P(5.0f), u.P(2.5f), u.P(11.0f), u.P(3.9f)), 0.7f * u.Scale);
            g.FillPath(solid, vm);
        }

        if (state == TrayIconState.Failed)
        {
            // Short neck, then a cross where the fork would be.
            g.FillRectangle(solid, Edges(u.P(7.1f), u.P(6.9f), u.P(8.9f), u.P(9.2f)));
            using var cross = RoundPen(color, u.W(2.0f));
            g.DrawLine(cross, u.P(5.4f), u.P(10.2f), u.P(10.6f), u.P(14.5f));
            g.DrawLine(cross, u.P(10.6f), u.P(10.2f), u.P(5.4f), u.P(14.5f));
            return;
        }

        // ── Stem down to the fork ──
        g.FillRectangle(solid, Edges(u.P(7.1f), u.P(6.9f), u.P(8.9f), u.P(9.6f)));

        bool lan = state == TrayIconState.Bridged;
        bool nat = state == TrayIconState.Fallback;
        float forkX = u.P(8f), forkY = u.P(9.2f), endY = u.P(12.4f);

        // ── Left branch → square end (physical LAN) ──
        using (var pen = RoundPen(lan ? color : dim, u.W(lan ? 1.8f : 1.6f)))
            g.DrawLine(pen, forkX, forkY, u.P(3.2f), endY);
        float sqBottom = u.P(15.5f);
        var square = Edges(u.P(0.6f), sqBottom - u.W(4.2f), u.P(5.2f), sqBottom);
        using (var end = new GraphicsPath(FillMode.Alternate))
        {
            AddRoundedRect(end, square, 0.8f * u.Scale);
            if (!lan)
            {
                var ring = u.W(1.3f);
                var inner = RectangleF.Inflate(square, -ring, -ring);
                AddRoundedRect(end, inner, MathF.Max(0f, 0.8f * u.Scale - ring));
            }
            g.FillPath(lan ? solid : faint, end);
        }

        // ── Right branch → round end (NAT fallback) ──
        using (var pen = RoundPen(nat ? color : dim, u.W(nat ? 1.8f : 1.6f)))
            g.DrawLine(pen, forkX, forkY, u.P(12.8f), endY);
        var diameter = u.W(4.5f);
        float right = u.P(15.45f), bottom = u.P(15.5f);
        var disc = new RectangleF(right - diameter, bottom - diameter, diameter, diameter);
        using (var end = new GraphicsPath(FillMode.Alternate))
        {
            end.AddEllipse(disc);
            if (!nat)
            {
                var ring = u.W(1.3f);
                end.AddEllipse(RectangleF.Inflate(disc, -ring, -ring));
            }
            g.FillPath(nat ? solid : faint, end);
        }
    }

    private static RectangleF Edges(float left, float top, float right, float bottom) =>
        RectangleF.FromLTRB(left, top, right, bottom);

    private static Pen RoundPen(Color color, float width) =>
        new(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };

    private static void AddRoundedRect(GraphicsPath path, RectangleF r, float radius)
    {
        radius = MathF.Min(radius, MathF.Min(r.Width, r.Height) / 2f);
        path.StartFigure();
        if (radius <= 0f)
        {
            path.AddRectangle(r);
            return;
        }
        var d = radius * 2;
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
