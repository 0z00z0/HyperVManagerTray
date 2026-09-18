<#
.SYNOPSIS
    Draws the HyperVManagerTray "route fork" glyph with System.Drawing (GDI+). Dot-sourced by
    Generate-AppIcon.ps1 (application icon, Start-menu shortcut icon) and make-wizard-images.ps1
    (installer wizard images).

.DESCRIPTION
    The same 16-unit geometry Helpers\IconGenerator.cs paints for the live tray icon: a virtual
    machine above a network line that forks to a square end (physical LAN) and a round end (NAT).
    The lit branch and its solid end show the route; the other branch and a hollow end are drawn at
    38 % opacity. Unknown lights neither branch and hollows the screen; Failed replaces the fork with
    a cross. Keep this file and IconGenerator.cs in sync if either changes.

    With -Snap, every edge is rounded to a whole pixel (halves away from the glyph centre) and every
    width to the nearest whole pixel, at least one, exactly as IconGenerator.cs does, so small frames
    stay crisp. Snapping is only exact when the centre falls on a whole pixel.
#>

function New-RouteGrid([float]$OriginX, [float]$OriginY, [float]$Scale, [bool]$Snap) {
    return @{
        OX = $OriginX; OY = $OriginY; K = $Scale; Snap = $Snap
        CX = $OriginX + 8 * $Scale; CY = $OriginY + 8 * $Scale
    }
}

function Get-RouteSnapped([float]$p, [float]$c, [bool]$snap) {
    if (-not $snap) { return [float]$p }
    $d = $p - $c
    return [float]($c + [Math]::Sign($d) * [Math]::Round([Math]::Abs($d), [MidpointRounding]::AwayFromZero))
}

function Get-RouteX($u, [float]$v) { Get-RouteSnapped ($u.OX + $v * $u.K) $u.CX $u.Snap }
function Get-RouteY($u, [float]$v) { Get-RouteSnapped ($u.OY + $v * $u.K) $u.CY $u.Snap }
function Get-RouteW($u, [float]$v) {
    if (-not $u.Snap) { return [float]($v * $u.K) }
    return [float][Math]::Max(1.0, [Math]::Round($v * $u.K, [MidpointRounding]::ToEven))
}

function Add-RouteRoundedRect($path, [System.Drawing.RectangleF]$r, [float]$radius) {
    $radius = [Math]::Min($radius, [Math]::Min($r.Width, $r.Height) / 2)
    $path.StartFigure()
    if ($radius -le 0) { $path.AddRectangle($r); return }
    $d = $radius * 2
    $path.AddArc($r.X,           $r.Y,            $d, $d, 180, 90)
    $path.AddArc($r.Right - $d,  $r.Y,            $d, $d, 270, 90)
    $path.AddArc($r.Right - $d,  $r.Bottom - $d,  $d, $d,   0, 90)
    $path.AddArc($r.X,           $r.Bottom - $d,  $d, $d,  90, 90)
    $path.CloseFigure()
}

function Get-RouteEdges([float]$l, [float]$t, [float]$r, [float]$b) {
    [System.Drawing.RectangleF]::FromLTRB($l, $t, $r, $b)
}

function New-RouteRoundPen([System.Drawing.Color]$color, [float]$width) {
    $pen = New-Object System.Drawing.Pen($color, $width)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    return $pen
}

# $State: Unknown, Bridged, Fallback or Failed.
function Draw-RouteGlyph($g, $u, [System.Drawing.Color]$color, [string]$State) {
    $dim   = [System.Drawing.Color]::FromArgb(97, $color)
    $solid = New-Object System.Drawing.SolidBrush($color)
    $faint = New-Object System.Drawing.SolidBrush($dim)
    try {
        # Virtual machine: frame with a slot, or a hollow screen while nothing is known.
        $vm = New-Object System.Drawing.Drawing2D.GraphicsPath([System.Drawing.Drawing2D.FillMode]::Alternate)
        try {
            Add-RouteRoundedRect $vm (Get-RouteEdges (Get-RouteX $u 3.4) (Get-RouteY $u 0.5) (Get-RouteX $u 12.6) (Get-RouteY $u 6.9)) (1.5 * $u.K)
            if ($State -eq 'Unknown') {
                Add-RouteRoundedRect $vm (Get-RouteEdges (Get-RouteX $u 4.8) (Get-RouteY $u 1.9) (Get-RouteX $u 11.2) (Get-RouteY $u 5.5)) (0.5 * $u.K)
            } else {
                Add-RouteRoundedRect $vm (Get-RouteEdges (Get-RouteX $u 5.0) (Get-RouteY $u 2.5) (Get-RouteX $u 11.0) (Get-RouteY $u 3.9)) (0.7 * $u.K)
            }
            $g.FillPath($solid, $vm)
        } finally { $vm.Dispose() }

        if ($State -eq 'Failed') {
            $g.FillRectangle($solid, (Get-RouteEdges (Get-RouteX $u 7.1) (Get-RouteY $u 6.9) (Get-RouteX $u 8.9) (Get-RouteY $u 9.2)))
            $cross = New-RouteRoundPen $color (Get-RouteW $u 2.0)
            try {
                $g.DrawLine($cross, (Get-RouteX $u 5.4),  (Get-RouteY $u 10.2), (Get-RouteX $u 10.6), (Get-RouteY $u 14.5))
                $g.DrawLine($cross, (Get-RouteX $u 10.6), (Get-RouteY $u 10.2), (Get-RouteX $u 5.4),  (Get-RouteY $u 14.5))
            } finally { $cross.Dispose() }
            return
        }

        # Stem down to the fork.
        $g.FillRectangle($solid, (Get-RouteEdges (Get-RouteX $u 7.1) (Get-RouteY $u 6.9) (Get-RouteX $u 8.9) (Get-RouteY $u 9.6)))

        $lan = $State -eq 'Bridged'
        $nat = $State -eq 'Fallback'
        $forkX = Get-RouteX $u 8.0; $forkY = Get-RouteY $u 9.2; $endY = Get-RouteY $u 12.4
        $ring  = Get-RouteW $u 1.3

        # Left branch to the square end (physical LAN).
        $pen = New-RouteRoundPen $(if ($lan) { $color } else { $dim }) (Get-RouteW $u $(if ($lan) { 1.8 } else { 1.6 }))
        try { $g.DrawLine($pen, $forkX, $forkY, (Get-RouteX $u 3.2), $endY) } finally { $pen.Dispose() }
        $sqBottom = Get-RouteY $u 15.5
        $square = Get-RouteEdges (Get-RouteX $u 0.6) ($sqBottom - (Get-RouteW $u 4.2)) (Get-RouteX $u 5.2) $sqBottom
        $end = New-Object System.Drawing.Drawing2D.GraphicsPath([System.Drawing.Drawing2D.FillMode]::Alternate)
        try {
            Add-RouteRoundedRect $end $square (0.8 * $u.K)
            if (-not $lan) {
                $inner = [System.Drawing.RectangleF]::Inflate($square, -$ring, -$ring)
                Add-RouteRoundedRect $end $inner ([Math]::Max(0, 0.8 * $u.K - $ring))
            }
            $g.FillPath($(if ($lan) { $solid } else { $faint }), $end)
        } finally { $end.Dispose() }

        # Right branch to the round end (NAT fallback).
        $pen = New-RouteRoundPen $(if ($nat) { $color } else { $dim }) (Get-RouteW $u $(if ($nat) { 1.8 } else { 1.6 }))
        try { $g.DrawLine($pen, $forkX, $forkY, (Get-RouteX $u 12.8), $endY) } finally { $pen.Dispose() }
        $dia    = Get-RouteW $u 4.5
        $right  = Get-RouteX $u 15.45
        $bottom = Get-RouteY $u 15.5
        $disc   = New-Object System.Drawing.RectangleF(($right - $dia), ($bottom - $dia), $dia, $dia)
        $end = New-Object System.Drawing.Drawing2D.GraphicsPath([System.Drawing.Drawing2D.FillMode]::Alternate)
        try {
            $end.AddEllipse($disc)
            if (-not $nat) { $end.AddEllipse([System.Drawing.RectangleF]::Inflate($disc, -$ring, -$ring)) }
            $g.FillPath($(if ($nat) { $solid } else { $faint }), $end)
        } finally { $end.Dispose() }
    } finally { $solid.Dispose(); $faint.Dispose() }
}
