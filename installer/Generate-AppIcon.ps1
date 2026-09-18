<#
.SYNOPSIS
    Generates the whole HyperVManagerTray product-icon family from ONE glyph geometry:
      • Assets\AppIcon.ico  — plated, high-contrast product app icon (exe ApplicationIcon,
                              installer SetupIconFile). Muted-blue plate + white route-fork glyph
                              with the physical-LAN branch lit.
      • Assets\app.ico      — same plated product icon, kept so build-installer.ps1's presence
                              check has something to find (framework-dependent legacy path).
      • Assets\TrayBlue.ico — the flat blue (Fallback state) tray glyph on a transparent background,
                              used by the Start-Menu shortcut so it matches the runtime tray icon.
      • Assets\AppIcon.png  — the SAME plated product icon as AppIcon.ico, at 256×256, for XAML
                              (issue #35). WinUI's <Image>/ms-appx:/// cannot load an .ico, so the
                              Settings pane-footer identity block needs a PNG. Emitted here rather
                              than hand-made so it can never drift from the .ico family.

.DESCRIPTION
    The 0z0-guideline PRODUCT icon set (issue #26). Per 0z0-design/logo/GUIDE.md the studio [Ø]
    mark must NOT appear on an app's own icon, and the icon uses the app's OWN muted palette — not
    ChargeKeeper's SteelBlue/Sage/Terracotta. So this icon is a flat, no-gradient, geometric route
    fork drawn in HyperVManagerTray's own tray blue (#3B7EC4).

    The glyph geometry is the SAME 16-unit layout Helpers\IconGenerator.cs paints for the live
    tray icons (v6), drawn by installer\RouteGlyph.ps1, so the app icon and the tray glyphs read as
    one family. On the plate the glyph is scaled to 72 % about the centre. Rendered natively with
    System.Drawing (GDI+) at each frame size — there is no SVG rasteriser on the build machine (the
    same constraint the 0z0-design asset scripts work around).

    Run manually after any icon design change and commit the regenerated Assets\*.ico. Also called
    automatically by build-installer.ps1 if Assets\app.ico is absent.
#>
param([string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\..").Path)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot "RouteGlyph.ps1")

# ── Product palette (HyperVManagerTray's own muted tray tones — see IconGenerator.cs) ──
$cPlate = [System.Drawing.Color]::FromArgb(255, 0x3B, 0x7E, 0xC4)   # muted steel-blue plate
$cGlyph = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xFF, 0xFF)   # white glyph on the plate
$cBlue  = [System.Drawing.Color]::FromArgb(255, 0x3B, 0x7E, 0xC4)   # flat blue glyph (tray Fallback)

function New-Surface([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    return @($bmp, $g)
}

# Plated product-icon frame: flat muted-blue rounded-square plate + white route-fork glyph, scaled to
# 72 % about the centre, with the physical-LAN branch lit.
function New-PlatedBitmap([int]$size) {
    $pair = New-Surface $size; $bmp = $pair[0]; $g = $pair[1]
    try {
        [float]$k = $size / 16.0
        $plate = New-Object System.Drawing.Drawing2D.GraphicsPath
        Add-RouteRoundedRect $plate (New-Object System.Drawing.RectangleF((0.5 * $k), (0.5 * $k), (15.0 * $k), (15.0 * $k))) (3.2 * $k)
        $pb    = New-Object System.Drawing.SolidBrush($cPlate)
        try { $g.FillPath($pb, $plate) } finally { $pb.Dispose(); $plate.Dispose() }
        $u = New-RouteGrid (2.24 * $k) (2.24 * $k) (0.72 * $k) $true
        Draw-RouteGlyph $g $u $cGlyph 'Bridged'
    } finally { $g.Dispose() }
    return $bmp
}

# Flat tray-style frame: transparent background, the tray glyph in the Fallback state.
function New-FlatBitmap([int]$size, [System.Drawing.Color]$glyphColor) {
    $pair = New-Surface $size; $bmp = $pair[0]; $g = $pair[1]
    try {
        $u = New-RouteGrid 0 0 ($size / 16.0) $true
        Draw-RouteGlyph $g $u $glyphColor 'Fallback'
    } finally { $g.Dispose() }
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
$appPng   = Join-Path $assetsDir "AppIcon.png"

Write-Ico $appIco   $platedSizes { param($s) New-PlatedBitmap $s }
Write-Ico $legacyIco $platedSizes { param($s) New-PlatedBitmap $s }
Write-Ico $trayIco  $traySizes   { param($s) New-FlatBitmap $s $cBlue }

# XAML-consumable copy of the plated product icon (issue #35). Same New-PlatedBitmap renderer and
# therefore the same geometry and $cPlate/$cGlyph palette as AppIcon.ico's 256px frame —
# regenerating the family keeps the two in lockstep by construction. 256px so the Settings footer's
# 36-DIP <Image> still downsamples cleanly at 175 % DPI.
$pngBmp = New-PlatedBitmap 256
try { $pngBmp.Save($appPng, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $pngBmp.Dispose() }

Write-Host "Generated product-icon family:" -ForegroundColor Green
Write-Host "    $appIco"
Write-Host "    $legacyIco"
Write-Host "    $trayIco"
Write-Host "    $appPng"
