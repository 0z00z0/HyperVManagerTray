using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Five tray icon states reflected as the glyph colour.
///
/// <para>The grey/red split is load-bearing (issue #37): <see cref="Unknown"/> means the app has not
/// established where the VMs are, <see cref="Failed"/> means it tried and did not get there. Both are
/// honest non-success states, but they call for different user reactions, so they are not merged —
/// and neither may ever be rendered as <see cref="Bridged"/>. See
/// <see cref="NetworkStatusUi.IconFor"/>, which is the only thing that should choose between these.</para>
///
/// <para><b><see cref="Starting"/> splits a pixel that was doing two jobs (issue #56).</b> #37's honesty
/// rule is right and stays: for the ~8 s between the tray icon appearing and the first apply pass
/// confirming an outcome, the app genuinely does not know where the VMs are, so it may not claim.
/// But it rendered that as <see cref="Unknown"/> — the same grey as "we looked and could not establish
/// it" — so a correct answer was indistinguishable from a hang, and the honest icon read as a broken
/// one. <see cref="Starting"/> says the app's first pass is still in flight. That is a fact about the
/// APP, not a claim about the host: it is true by construction the moment the tray icon is created, and
/// it is the only state here that needs no confirmation from anything. It claims nothing about the
/// network and it may never be a success colour — <see cref="NetworkStatusUi.IconFor"/>'s
/// enum-enumerating guard covers it exactly as it covers every other member.</para>
/// </summary>
public enum TrayIconState
{
    Unknown,  // grey  — no data: the state is not established (and we are no longer merely starting)
    Starting, // amber — issue #56: the app's FIRST evaluation is still running. Claims nothing about the host.
    Bridged,  // green — CONFIRMED: VM on physical LAN
    Fallback, // blue  — CONFIRMED: VM on Default Switch / NAT
    Failed,   // red   — the switch bind or a VM-NIC reconnect FAILED (issue #37)
}

/// <summary>
/// Generates the tray icon at runtime (no image assets): a minimalist "virtual machine" glyph —
/// a hollow monitor/display with two content bars and a stand — drawn in a single muted colour
/// on a fully transparent background.  The colour signals state (grey = unknown, amber = starting up,
/// green = bridged to physical LAN, blue = NAT/fallback, red = the apply failed); the transparent
/// background lets the same icon read on both light and dark taskbars.  Colours are intentionally
/// medium-luminance, not vivid, so the glyph's edges stay crisp against either backdrop.
///
/// One colour per state is a rule, not a coincidence: the geometry is identical across all five, so the
/// colour is the ONLY channel carrying the state, and two states sharing one would be indistinguishable
/// rather than merely similar (RenderIcon_EveryStateHasItsOwnColour enumerates it).  That is what ruled
/// out a second grey for Starting in issue #56 — see GlyphStarting.
///
/// Five multi-size .ico files are written next to the exe and swapped on state changes; writing
/// to disk lets H.NotifyIcon reload them and avoids the GDI handle leak of Bitmap.GetHicon().
///
/// Icon version: v5 — the restyled HyperVManagerTray product glyph (the 0z0-guideline product
/// icon set, issue #26): a rounder hollow monitor, cleaner content bars and a wider grounded
/// stand.  The exact same 16-unit geometry is drawn — plated white-on-blue with a green
/// connection dot — by installer\Generate-AppIcon.ps1 for AppIcon.ico / TrayBlue.ico, so the tray
/// glyphs and the app icon are one consistent family.  The version suffix forces regeneration on
/// first run after an upgrade.
/// </summary>
internal static class IconGenerator
{
    // v5 — rename forces regeneration on first run after upgrade; old v2/v3/v4 files are ignored.
    private const string UnknownFile  = "icon-unknown-v5.ico";
    private const string StartingFile = "icon-starting-v5.ico";  // issue #56
    private const string BridgedFile  = "icon-bridged-v5.ico";
    private const string FallbackFile = "icon-fallback-v5.ico";
    private const string FailedFile   = "icon-failed-v5.ico";   // issue #37

    // Frame sizes baked into each .ico.  64/48 are picked by Windows on 4K (200 %+ DPI)
    // without upscaling; 32/24/20/16 cover 100–150 % tray DPI.
    private static readonly int[] IconSizes = [64, 48, 32, 24, 20, 16];

    // Glyph colours — one per state.  Medium luminance (not vivid) so the shape stays legible
    // on both white and dark taskbars.
    private static readonly Color GlyphUnknown  = Color.FromArgb(255, 0x8C, 0x8C, 0x8C);  // muted grey
    private static readonly Color GlyphBridged  = Color.FromArgb(255, 0x35, 0x9E, 0x6A);  // muted green
    private static readonly Color GlyphFallback = Color.FromArgb(255, 0x3B, 0x7E, 0xC4);  // muted blue
    // Muted amber (issue #56) — "still working on it", the one convention a tray user already reads
    // without being told. Its constraints are tighter than they look, and each one excludes an option:
    //   • NOT a second grey. A darker/lighter grey is the honest colour (grey is this app's "no claim"
    //     hue) but it is not a DISTINGUISHABLE one — mid-grey vs dark-grey at 16 px is the same pixel
    //     problem #56 was filed about, only harder to describe. The palette here is one colour per
    //     state by design (see RenderIcon_EveryStateHasItsOwnColour), so the state needs its own hue.
    //   • NOT green- or blue-leaning. Those are the two CONFIRMED colours; a yellow-green that drifted
    //     toward Bridged would be #37's original defect wearing a new coat. Amber is deliberately
    //     R-dominant — it sits on the "not a success colour" side of the palette by construction.
    //   • FAR from GlyphFailed. Amber and red are the closest pair here (both warm, both R-dominant), so
    //     the separation is in the GREEN channel (0x9A vs Failed's 0x45) — a gold, not an orange. Tested.
    private static readonly Color GlyphStarting = Color.FromArgb(255, 0xC9, 0x9A, 0x2E);
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
            TrayIconState.Starting => StartingFile,
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
        TrayIconState.Starting => GlyphStarting,
        TrayIconState.Bridged  => GlyphBridged,
        TrayIconState.Fallback => GlyphFallback,
        TrayIconState.Failed   => GlyphFailed,
        _                      => GlyphUnknown,
    };

    // ── Rendering ───────────────────────────────────────────────────────────────

    // Glyph is designed in a 16-unit logical space and scaled to each frame size.  Everything is
    // drawn with filled shapes (no thin strokes) so it stays crisp down to 16 px.  These are the
    // canonical v5 product-glyph coordinates — installer\Generate-AppIcon.ps1 draws the identical
    // geometry for AppIcon.ico / TrayBlue.ico, so keep the two in sync if either changes.  Layout:
    //   ┌──────────────────────┐   ← hollow monitor frame (transparent centre)
    //   │  ▭▭▭▭▭▭▭▭▭▭▭▭▭▭▭▭▭▭  │   ← content bar 1
    //   │  ▭▭▭▭▭▭▭▭▭▭          │   ← content bar 2
    //   └──────────┬───────────┘
    //          ▭▭▭▭▭▭▭▭▭            ← stand neck + wider foot
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
            AddRoundedRect(frame, new RectangleF(1.5f, 2.3f, 13.0f, 8.4f), 1.9f);  // outer bezel
            AddRoundedRect(frame, new RectangleF(3.0f, 3.8f, 10.0f, 5.4f), 1.1f);  // screen cut-out
            g.FillPath(fill, frame);
        }

        // ── Screen content bars (inside the transparent cut-out) ─────────────
        using (var bars = new GraphicsPath())
        {
            AddRoundedRect(bars, new RectangleF(4.2f, 5.2f, 6.6f, 1.0f), 0.5f);  // long bar
            AddRoundedRect(bars, new RectangleF(4.2f, 7.0f, 4.2f, 1.0f), 0.5f);  // short bar
            g.FillPath(fill, bars);
        }

        // ── Stand: neck + wider foot ─────────────────────────────────────────
        using (var stand = new GraphicsPath())
        {
            AddRoundedRect(stand, new RectangleF(7.0f, 10.7f, 2.0f, 1.4f),  0.3f);   // neck
            AddRoundedRect(stand, new RectangleF(4.6f, 12.0f, 6.8f, 1.5f),  0.75f);  // foot
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
