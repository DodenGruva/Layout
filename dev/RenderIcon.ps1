<#
.SYNOPSIS
    Renders a LayoutToolIcons glyph to a PNG so it can be LOOKED AT before it ships.

.DESCRIPTION
    The icons in src/UI/LayoutToolIcons.cs are drawn with Cairo at 18-42 px and cannot be compile-checked:
    a glyph that is geometrically correct can still be an unreadable smudge at the size it actually renders,
    and the only way to find that out is normally to build a zip and look in game.

    This harness ports a glyph's drawing code to GDI+ and renders it at several true sizes plus a magnified
    copy of each, as one montage PNG. It is a REPLICA, not the real thing - it exists to answer "is this
    legible / does it read as the thing it depicts", not to prove pixel equality with Cairo.

    It earned its keep on the v0.4.9-v0.4.11 gear (SESSION_32 section 7), catching two problems a build
    could not:
      - twelve teeth put the valleys under a pixel at 18 px;
      - an eight-tooth variant had teeth half again wider than its gaps, which reads as a scalloped disc
        rather than a gear;
      - and the spoked design does not survive 18 px at all, which is why gearSize is 22 in GuideToolGui.

.PARAMETER Glyph
    Which glyph to render. Currently: gear.

.PARAMETER Sizes
    True pixel sizes to render at. Default covers the title bar (22) and the tile rows (42).

.PARAMETER Magnify
    Scale factor for the blown-up copy under each true-size render. Nearest-neighbour, so pixels stay square.

.PARAMETER OutPath
    Where to write the PNG. Defaults to the system temp folder - deliberately NOT the repo, which is
    published and should not collect scratch images.

.EXAMPLE
    .\dev\RenderIcon.ps1
    .\dev\RenderIcon.ps1 -Sizes 18,22 -Magnify 10

.NOTES
    TO ADD A GLYPH: write a scriptblock into $Glyphs that takes ($g, $canvas) and draws with $canvas.X /
    .Y / .L, exactly as the C# does with its Canvas helper. Keep the design box and zoom identical to the
    C# (`new Canvas(x, y, w, h, BOX)` -> New-Canvas -Box BOX), or the proportions will not match what ships.
#>
[CmdletBinding()]
param(
    [ValidateSet('gear', 'revealnear', 'revealall',
                 'rotatespin', 'rotatetip', 'rotatespinleft', 'rotatetipleft',
                 'mirror', 'moveground', 'movedown', 'roundover')]
    [string]$Glyph = 'gear',

    [int[]]$Sizes = @(18, 22, 28, 42),

    [int]$Magnify = 6,

    [string]$OutPath
)

Add-Type -AssemblyName System.Drawing

# ------------------------------------------------------------------------------------------------
#  Canvas - the exact mapping LayoutToolIcons.Canvas uses
# ------------------------------------------------------------------------------------------------
# design-space (0..Box) -> pixels, centred in the tile, with the same 1.12 zoom the C# defaults to.
# Anything past Box/2 / Zoom from the centre falls outside the tile; that is what bounds the radii.
function New-Canvas {
    param([double]$Size, [double]$Box = 60, [double]$Zoom = 1.12)
    $s = $Size / $Box * $Zoom
    $o = ($Size - $Box * $s) / 2.0
    [pscustomobject]@{
        S = $s; OX = $o; OY = $o
        X = { param($u) $o + $u * $s }.GetNewClosure()
        Y = { param($v) $o + $v * $s }.GetNewClosure()
        L = { param($d) $d * $s }.GetNewClosure()
    }
}

# ------------------------------------------------------------------------------------------------
#  Glyphs
# ------------------------------------------------------------------------------------------------
$Glyphs = @{

    # Mirrors LayoutToolIcons.DrawRoundover. Coordinates are scaled from its 76-unit shape canvas into
    # this harness's 60-unit canvas so the final pixels match the real mapping.
    roundover = {
        param($g, $c)
        $f = 60.0 / 76.0
        $solid = & $Script:NewPen $c (3.0 * $f)
        $dashed = & $Script:NewPen $c (3.0 * $f)
        $dashed.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Custom
        $dashed.DashCap = [System.Drawing.Drawing2D.DashCap]::Round
        $dashed.DashPattern = [single[]]@(0.2, 2.2)
        $pt = { param($u, $v) New-Object System.Drawing.PointF(
            [float](& $c.X ($u * $f)), [float](& $c.Y ($v * $f))) }

        $g.DrawLines($solid, @(
            (& $pt 12 40), (& $pt 12 64), (& $pt 64 64), (& $pt 64 12), (& $pt 40 12)))

        $arc = New-Object System.Drawing.Drawing2D.GraphicsPath
        $arc.AddBezier((& $pt 40 12), (& $pt 24.536 12), (& $pt 12 24.536), (& $pt 12 40))
        $g.DrawPath($dashed, $arc)
        $arc.Dispose()
        $dashed.Dispose()
        $solid.Dispose()
    }

    # Mirrors LayoutToolIcons.DrawGear as shipped in v0.4.11: a spoked wheel-gear in THREE separate fill
    # passes. Rim, spokes and hub overlap, and under one even-odd path every overlap cancels - the spokes
    # would punch holes through the hub instead of joining it. Separate fills just paint twice.
    gear = {
        param($g, $c)

        $cx = 30.0; $cy = 30.0
        $teeth = 8; $spokes = 6
        $rTip = 25.5; $rRoot = 19.5; $rRimIn = 16.0; $rHub = 7.6; $rBore = 4.6; $spokeHalf = 1.9
        $pitch = [Math]::PI * 2 / $teeth
        $tipHalf = $pitch * 0.13
        $rootHalf = $pitch * 0.32
        $phase = $pitch * 0.5      # a VALLEY at twelve and six o'clock, not a tooth

        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        $pt = { param($r, $a)
            New-Object System.Drawing.PointF(
                [float](& $c.X ($cx + [Math]::Cos($a) * $r)),
                [float](& $c.Y ($cy + [Math]::Sin($a) * $r)))
        }
        $ellipse = { param($path, $r)
            $d = $r * 2 * $c.S
            $path.AddEllipse([float]((& $c.X $cx) - $d / 2), [float]((& $c.Y $cy) - $d / 2), [float]$d, [float]$d)
        }

        # --- 1. toothed rim, with the rim bore taken out of it ---
        $p1 = New-Object System.Drawing.Drawing2D.GraphicsPath
        $p1.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
        $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
        for ($i = 0; $i -lt $teeth; $i++) {
            $a = $i * $pitch + $phase
            $pts.Add((& $pt $rRoot ($a - $rootHalf)))
            $pts.Add((& $pt $rTip  ($a - $tipHalf)))
            $pts.Add((& $pt $rTip  ($a + $tipHalf)))
            $pts.Add((& $pt $rRoot ($a + $rootHalf)))
            # the valley is a real arc of the root circle; sampled here, ctx.Arc in the C#
            $a0 = $a + $rootHalf; $a1 = $a + $pitch - $rootHalf
            for ($k = 1; $k -le 8; $k++) { $pts.Add((& $pt $rRoot ($a0 + ($a1 - $a0) * $k / 8.0))) }
        }
        $p1.AddPolygon($pts.ToArray())
        & $ellipse $p1 $rRimIn
        $g.FillPath($brush, $p1)

        # --- 2. spokes: their own fill, wound the same way, overlapping hub and rim at both ends ---
        for ($i = 0; $i -lt $spokes; $i++) {
            $a = $i * [Math]::PI * 2 / $spokes
            $ca = [Math]::Cos($a); $sa = [Math]::Sin($a)
            $sp = New-Object System.Collections.Generic.List[System.Drawing.PointF]
            foreach ($corner in @(@(($rBore + 0.5), -$spokeHalf), @(($rRimIn + 0.6), -$spokeHalf),
                                  @(($rRimIn + 0.6), $spokeHalf), @(($rBore + 0.5), $spokeHalf))) {
                $u = $corner[0]; $v = $corner[1]
                $sp.Add((New-Object System.Drawing.PointF(
                    [float](& $c.X ($cx + $u * $ca - $v * $sa)),
                    [float](& $c.Y ($cy + $u * $sa + $v * $ca)))))
            }
            $g.FillPolygon($brush, $sp.ToArray())
        }

        # --- 3. hub annulus ---
        $p3 = New-Object System.Drawing.Drawing2D.GraphicsPath
        $p3.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
        & $ellipse $p3 $rHub
        & $ellipse $p3 $rBore
        $g.FillPath($brush, $p3)
    }

    # Mirrors LayoutToolIcons.DrawRevealNear (v0.4.18): a small eye over six separated blocks. The
    # question this render answers is whether SIX blocks stay countable at 42 px, or blur into a bar.
    revealnear = {
        param($g, $c)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float](& $c.L 2.4))
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)

        & $Script:DrawSmallEye $g $c $pen 22
        $r = & $c.L 4
        $g.FillEllipse($brush, [float]((& $c.X 30) - $r), [float]((& $c.Y 22) - $r), [float]($r * 2), [float]($r * 2))

        $count = 6; $blockW = 6.8; $gap = 1.4; $blockH = 9.0
        $runW = $count * $blockW + ($count - 1) * $gap
        $bx = (60 - $runW) / 2.0
        for ($i = 0; $i -lt $count; $i++) {
            $g.FillRectangle($brush,
                [float](& $c.X ($bx + $i * ($blockW + $gap))), [float](& $c.Y 40),
                [float](& $c.L $blockW), [float](& $c.L $blockH))
        }
    }

    # Mirrors LayoutToolIcons.DrawRevealAll (v0.4.18): a small eye with rays. The question here is
    # whether the rays stay separate from the eye at 42 px rather than fusing into a blob.
    revealall = {
        param($g, $c)
        $rayPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float](& $c.L 2.2))
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float](& $c.L 2.4))
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)

        $inner = 19.0; $outer = 27.0
        for ($i = 0; $i -lt 8; $i++) {
            $deg = 22.5 + $i * 45.0
            if ([Math]::Abs([Math]::Sin($deg * [Math]::PI / 180.0)) -lt 0.2) { continue }
            $a = $deg * [Math]::PI / 180.0
            $dx = [Math]::Cos($a); $dy = [Math]::Sin($a)
            $g.DrawLine($rayPen,
                [float](& $c.X (30 + $dx * $inner)), [float](& $c.Y (30 + $dy * $inner)),
                [float](& $c.X (30 + $dx * $outer)), [float](& $c.Y (30 + $dy * $outer)))
        }

        & $Script:DrawSmallEye $g $c $pen 30
        $r = & $c.L 4
        $g.FillEllipse($brush, [float]((& $c.X 30) - $r), [float]((& $c.Y 30) - $r), [float]($r * 2), [float]($r * 2))
    }
    # Mirrors LayoutToolIcons.DrawRotate with upright = false: the turntable seen at an angle, used by
    # RotateSpinLeft/Right. The question is whether a 16 x 8.5 ellipse still reads as a ring at 42 px, and
    # whether its arrowhead is distinguishable from the upright variant's at a glance.
    rotatespin = { param($g, $c) & $Script:DrawRotateGlyph $g $c $false $true }

    # The same glyph with upright = true: a true circle, used by RotateTipLeft/Right.
    rotatetip  = { param($g, $c) & $Script:DrawRotateGlyph $g $c $true  $true }

    # The anticlockwise halves of each pair. Rendered because the left and right buttons sit side by side
    # in the pad, and "are these two obviously opposite" is a question only the pair can answer.
    rotatespinleft = { param($g, $c) & $Script:DrawRotateGlyph $g $c $false $false }
    rotatetipleft  = { param($g, $c) & $Script:DrawRotateGlyph $g $c $true  $false }

    # Mirrors LayoutToolIcons.DrawActionMirror. The question: does it read as one form and its reflection,
    # or as two arrows pointing outward?
    mirror = {
        param($g, $c)
        $pen = & $Script:NewPen $c 2.4

        for ($v = 10.0; $v -lt 50.0; $v += 7.0) {
            $g.DrawLine($pen, [float](& $c.X 30), [float](& $c.Y $v), [float](& $c.X 30), [float](& $c.Y ($v + 3.6)))
        }
        & $Script:Poly $g $c $pen $true  @(25, 16, 25, 44, 12, 44)
        & $Script:Poly $g $c $pen $true  @(35, 16, 35, 44, 48, 44)
    }

    # Mirrors LayoutToolIcons.DrawMoveGround (v0.4.28) - the double chevron falling onto the floor bar.
    # The question: does it stay separable from movedown below, which shares the bar?
    moveground = {
        param($g, $c)
        $pen = & $Script:NewPen $c 3.2
        & $Script:Poly $g $c $pen $false @(19, 15, 30, 26, 41, 15)
        & $Script:Poly $g $c $pen $false @(19, 28, 30, 39, 41, 28)
        $g.DrawLine($pen, [float](& $c.X 14), [float](& $c.Y 48), [float](& $c.X 46), [float](& $c.Y 48))
    }

    # Mirrors LayoutToolIcons.DrawMoveArrow rotated a half turn with groundBar - the Down tile, rendered
    # only so the pair above can be compared side by side.
    movedown = {
        param($g, $c)
        $pen = & $Script:NewPen $c 3.2
        $half = & $c.L 12; $spread = & $c.L 9; $drop = & $c.L 11
        $ox = & $c.X 30; $oy = & $c.Y 25
        # rotation = PI, so (u, v) -> (-u, -v) about the origin
        $p = { param($u, $v) New-Object System.Drawing.PointF([float]($ox - $u), [float]($oy - $v)) }
        $g.DrawLine($pen, (& $p 0 $half), (& $p 0 (-$half)))
        $g.DrawLines($pen, @((& $p (-$spread) (-$half + $drop)), (& $p 0 (-$half)), (& $p $spread (-$half + $drop))))
        $g.DrawLine($pen, [float](& $c.X 14), [float](& $c.Y 48), [float](& $c.X 46), [float](& $c.Y 48))
    }
}

# ------------------------------------------------------------------------------------------------
#  Shared helpers - the PowerShell counterparts of LayoutToolIcons.Pen / .Poly
# ------------------------------------------------------------------------------------------------
$Script:NewPen = {
    param($c, $width)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float][Math]::Max(1.4, (& $c.L $width)))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen
}

# uv is a flat design-space coordinate list, exactly as the C# Poly takes it.
$Script:Poly = {
    param($g, $c, $pen, $close, $uv)
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    for ($i = 0; $i + 1 -lt $uv.Count; $i += 2) {
        $pts.Add((New-Object System.Drawing.PointF([float](& $c.X $uv[$i]), [float](& $c.Y $uv[$i + 1]))))
    }
    if ($close) { $pts.Add($pts[0]) }
    $g.DrawLines($pen, $pts.ToArray())
}

# Mirrors LayoutToolIcons.DrawRotate exactly: the ring walked as a polyline (so the pen stays even where a
# squashed ellipse is steepest) plus a symmetric chevron on the true tangent at the end of the sweep.
$Script:DrawRotateGlyph = {
    param($g, $c, $upright, $clockwise)
    $pen = & $Script:NewPen $c 3.0

    $cx = & $c.X 30; $cy = & $c.Y 30
    $rx = & $c.L 16
    $ry = if ($upright) { & $c.L 16 } else { & $c.L 8.5 }
    $m = if ($clockwise) { 1.0 } else { -1.0 }

    $start = -0.32 * [Math]::PI
    $end = 1.32 * [Math]::PI
    $headArc = 24.0 * [Math]::PI / 180.0

    $steps = 56
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    for ($i = 0; $i -le $steps; $i++) {
        $t = $start + ($end - $headArc - $start) * $i / $steps
        $pts.Add((New-Object System.Drawing.PointF(
            [float]($cx + $m * $rx * [Math]::Cos($t)), [float]($cy + $ry * [Math]::Sin($t)))))
    }
    $g.DrawLines($pen, $pts.ToArray())

    $hx = $cx + $m * $rx * [Math]::Cos($end); $hy = $cy + $ry * [Math]::Sin($end)
    $dx = -$m * $rx * [Math]::Sin($end); $dy = $ry * [Math]::Cos($end)
    $len = [Math]::Sqrt($dx * $dx + $dy * $dy)
    if ($len -lt 1e-9) { return }
    $dx /= $len; $dy /= $len
    $nx = -$dy; $ny = $dx
    $back = & $c.L 8.5; $half = & $c.L 4.5; $over = & $c.L 1.0
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPolygon($brush, @(
        (New-Object System.Drawing.PointF([float]($hx + $dx * $over), [float]($hy + $dy * $over))),
        (New-Object System.Drawing.PointF([float]($hx - $dx * $back + $nx * $half), [float]($hy - $dy * $back + $ny * $half))),
        (New-Object System.Drawing.PointF([float]($hx - $dx * $back - $nx * $half), [float]($hy - $dy * $back - $ny * $half)))))
}

# The SmallEye helper, shared by both reveal glyphs exactly as it is in the C#.
$Script:DrawSmallEye = {
    param($g, $c, $pen, $cy)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p = { param($u, $v) New-Object System.Drawing.PointF([float](& $c.X $u), [float](& $c.Y $v)) }
    $path.AddBezier((& $p 17 $cy), (& $p 23 ($cy - 9)), (& $p 37 ($cy - 9)), (& $p 43 $cy))
    $path.AddBezier((& $p 43 $cy), (& $p 37 ($cy + 9)), (& $p 23 ($cy + 9)), (& $p 17 $cy))
    $g.DrawPath($pen, $path)
}

# ------------------------------------------------------------------------------------------------
#  Render
# ------------------------------------------------------------------------------------------------
function New-GlyphBitmap {
    param([int]$Size, [scriptblock]$Draw)
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    # A mid-dark tile, roughly the tool panel's own backdrop - white-on-white proves nothing.
    $g.Clear([System.Drawing.Color]::FromArgb(255, 58, 58, 58))
    & $Draw $g (New-Canvas -Size $Size)
    $g.Dispose()
    return $bmp
}

$draw = $Glyphs[$Glyph]
if (-not $draw) { throw "No glyph named '$Glyph'. Known: $($Glyphs.Keys -join ', ')" }

$pad = 16
$cellW = ($Sizes | ForEach-Object { [Math]::Max(110, $_ * $Magnify + 28) } | Measure-Object -Sum).Sum
$sheetW = $cellW + $pad
$sheetH = ($Sizes | Measure-Object -Maximum).Maximum * $Magnify + 80

$sheet = New-Object System.Drawing.Bitmap([int]$sheetW, [int]$sheetH)
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.Clear([System.Drawing.Color]::FromArgb(255, 30, 30, 30))
$sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$font = New-Object System.Drawing.Font('Consolas', 10)
$label = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::LightGray)

$x = $pad
foreach ($size in $Sizes) {
    $bmp = New-GlyphBitmap -Size $size -Draw $draw
    $sg.DrawImage($bmp, $x, $pad, $size, $size)                       # true size, for the honest verdict
    $sg.DrawImage($bmp, $x, $pad + 34, $size * $Magnify, $size * $Magnify)   # magnified, to see the pixels
    $sg.DrawString("$size px  x$Magnify", $font, $label, $x, $sheetH - 26)
    $x += [Math]::Max(110, $size * $Magnify + 28)
    $bmp.Dispose()
}
$sg.Dispose()

if (-not $OutPath) { $OutPath = Join-Path ([System.IO.Path]::GetTempPath()) "layout-icon-$Glyph.png" }
$sheet.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
Write-Output $OutPath
