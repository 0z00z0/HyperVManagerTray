<#
.SYNOPSIS
    Generates the whole HyperVManagerTray product-icon family from ONE glyph geometry:
      • Assets\AppIcon.ico  — plated, high-contrast product app icon (exe ApplicationIcon,
                              installer SetupIconFile). Muted-blue plate + white VM-monitor glyph
                              + green connection dot.
      • Assets\app.ico      — same plated product icon, kept so build-installer.ps1's presence
                              check has something to find (framework-dependent legacy path).
      • Assets\TrayBlue.ico — the flat blue (Fallback state) VM glyph on a transparent background,
                              used by the Start-Menu shortcut so it matches the runtime tray icon.

.DESCRIPTION
    The 0z0-guideline PRODUCT icon set (issue #26). Per 0z0-design/logo/GUIDE.md the studio [Ø]
    mark must NOT appear on an app's own icon, and the icon uses the app's OWN muted palette — not
    ChargeKeeper's SteelBlue/Sage/Terracotta. So this icon is a flat, no-gradient, geometric VM
    monitor drawn in HyperVManagerTray's own tray tones (blue #3B7EC4 / green #359E6A).

    The glyph geometry is the SAME 16-unit layout Helpers\IconGenerator.cs paints for the live
    tray icons (v5), so the app icon and the tray glyphs read as one family. Rendered natively with
    System.Drawing (GDI+) at each frame size — there is no SVG rasteriser on the build machine (the
    same constraint the 0z0-design asset scripts work around).

    Run manually after any icon design change and commit the regenerated Assets\*.ico. Also called
    automatically by build-installer.ps1 if Assets\app.ico is absent.
#>
param([string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\..").Path)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# ── Product palette (HyperVManagerTray's own muted tray tones — see IconGenerator.cs) ──
$cPlate = [System.Drawing.Color]::FromArgb(255, 0x3B, 0x7E, 0xC4)   # muted steel-blue plate
$cGlyph = [System.Drawing.Color]::FromArgb(255, 0xF4, 0xF7, 0xFB)   # near-white glyph on the plate
$cDot   = [System.Drawing.Color]::FromArgb(255, 0x35, 0x9E, 0x6A)   # muted green connection dot
$cBlue  = [System.Drawing.Color]::FromArgb(255, 0x3B, 0x7E, 0xC4)   # flat blue glyph (tray Fallback)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $r = [Math]::Min($r, [Math]::Min($w, $h) / 2); $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x,           $y,           $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y,           $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $path.AddArc($x,           $y + $h - $d, $d, $d,  90, 90)
    $path.CloseFigure()
    return $path
}

# Add a rounded rect to an EXISTING path (used to build the hollow monitor ring as one
# Alternate-fill path — outer bezel minus screen cut-out).
function Add-RoundedRect($path, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $r = [Math]::Min($r, [Math]::Min($w, $h) / 2); $d = $r * 2
    $path.StartFigure()
    $path.AddArc($x,           $y,           $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y,           $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $path.AddArc($x,           $y + $h - $d, $d, $d,  90, 90)
    $path.CloseFigure()
}

# Draws the v5 VM-monitor product glyph in $glyphColor on the current 16-unit-scaled surface,
# using the SAME coordinates as Helpers\IconGenerator.cs.RenderIcon. When $withDot is set, a green
# connection dot is added (plated app-icon variant only; the monochrome tray glyphs omit it).
function Draw-Glyph($g, [System.Drawing.Color]$glyphColor, [bool]$withDot) {
    $fill = New-Object System.Drawing.SolidBrush($glyphColor)
    try {
        # Hollow monitor frame (outer bezel ∖ screen cut-out = a ring).
        $frame = New-Object System.Drawing.Drawing2D.GraphicsPath([System.Drawing.Drawing2D.FillMode]::Alternate)
        try {
            Add-RoundedRect $frame 1.5 2.3 13.0 8.4 1.9   # outer bezel
            Add-RoundedRect $frame 3.0 3.8 10.0 5.4 1.1   # screen cut-out
            $g.FillPath($fill, $frame)
        } finally { $frame.Dispose() }

        # Screen content bars.
        $bars = New-Object System.Drawing.Drawing2D.GraphicsPath
        try {
            Add-RoundedRect $bars 4.2 5.2 6.6 1.0 0.5     # long bar
            Add-RoundedRect $bars 4.2 7.0 4.2 1.0 0.5     # short bar
            $g.FillPath($fill, $bars)
        } finally { $bars.Dispose() }

        # Stand: neck + wider foot.
        $stand = New-Object System.Drawing.Drawing2D.GraphicsPath
        try {
            Add-RoundedRect $stand 7.0 10.7 2.0 1.4 0.3   # neck
            Add-RoundedRect $stand 4.6 12.0 6.8 1.5 0.75  # foot
            $g.FillPath($fill, $stand)
        } finally { $stand.Dispose() }
    } finally { $fill.Dispose() }

    if ($withDot) {
        # Green connection dot in the lower-right of the screen — the "networked VM" cue.
        $dotBrush = New-Object System.Drawing.SolidBrush($cDot)
        try { $g.FillEllipse($dotBrush, [float]9.9, [float]7.1, [float]2.6, [float]2.6) }
        finally { $dotBrush.Dispose() }
    }
}

function New-Surface([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 16.0, $size / 16.0)
    return @($bmp, $g)
}

# Plated product-icon frame: flat muted-blue rounded-square plate + white glyph + green dot.
function New-PlatedBitmap([int]$size) {
    $pair = New-Surface $size; $bmp = $pair[0]; $g = $pair[1]
    try {
        $plate = New-RoundedPath 0.5 0.5 15.0 15.0 3.2
        $pb    = New-Object System.Drawing.SolidBrush($cPlate)
        try { $g.FillPath($pb, $plate) } finally { $pb.Dispose(); $plate.Dispose() }
        Draw-Glyph $g $cGlyph $true
    } finally { $g.Dispose() }
    return $bmp
}

# Flat tray-style frame: transparent background, single-colour glyph, no plate, no dot.
function New-FlatBitmap([int]$size, [System.Drawing.Color]$glyphColor) {
    $pair = New-Surface $size; $bmp = $pair[0]; $g = $pair[1]
    try { Draw-Glyph $g $glyphColor $false } finally { $g.Dispose() }
    return $bmp
}

# Writes a Vista+ PNG-in-ICO file from a list of frame sizes, using $renderer (a scriptblock
# taking a size and returning a Bitmap).
function Write-Ico([string]$icoPath, [int[]]$sizes, [scriptblock]$renderer) {
    $frames = @()
    foreach ($sz in $sizes) {
        $bmp = & $renderer $sz
        $ms  = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $frames += , $ms.ToArray()
    }

    $fs = [System.IO.File]::Open($icoPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([int16]0)               # reserved
        $bw.Write([int16]1)               # type: icon
        $bw.Write([int16]$sizes.Length)   # image count

        $dataOffset = 6 + $sizes.Length * 16
        for ($i = 0; $i -lt $sizes.Length; $i++) {
            $sz = $sizes[$i]
            $wh = [byte]($(if ($sz -ge 256) { 0 } else { $sz }))  # 0 encodes 256 in ICO format
            $bw.Write($wh)                # width
            $bw.Write($wh)                # height
            $bw.Write([byte]0)           # colour count (0 = true-colour)
            $bw.Write([byte]0)           # reserved
            $bw.Write([int16]1)          # colour planes
            $bw.Write([int16]32)         # bits per pixel
            $bw.Write([int]$frames[$i].Length)  # data size
            $bw.Write([int]$dataOffset)         # data offset in file
            $dataOffset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $bw.Write($frame) }
    } finally { $bw.Dispose(); $fs.Dispose() }
}

$assetsDir = Join-Path $ProjectRoot "Assets"
if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir | Out-Null }

# Plated app icon: extra 256/128 frames for taskbar / Alt-Tab / installer header at high DPI.
$platedSizes = @(256, 128, 64, 48, 32, 24, 20, 16)
$traySizes   = @(64, 48, 32, 24, 20, 16)

$appIco   = Join-Path $assetsDir "AppIcon.ico"
$legacyIco = Join-Path $assetsDir "app.ico"
$trayIco  = Join-Path $assetsDir "TrayBlue.ico"

Write-Ico $appIco   $platedSizes { param($s) New-PlatedBitmap $s }
Write-Ico $legacyIco $platedSizes { param($s) New-PlatedBitmap $s }
Write-Ico $trayIco  $traySizes   { param($s) New-FlatBitmap $s $cBlue }

Write-Host "Generated product-icon family:" -ForegroundColor Green
Write-Host "    $appIco"
Write-Host "    $legacyIco"
Write-Host "    $trayIco"
