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
    [ValidateSet('gear')]
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

    # Mirrors LayoutToolIcons.DrawGear as shipped in v0.4.11: a spoked wheel-gear in THREE separate fill
    # passes. Rim, spokes and hub overlap, and under one even-odd path every overlap cancels - the spokes
    # would punch holes through the hub instead of joining it. Separate fills just paint twice.
    gear = {
        param($g, $c)

        $cx = 30.0; $cy = 30.0
        $teeth = 8; $spokes = 6
        $rTip = 25.5; $rRoot = 21.5; $rRimIn = 17.8; $rHub = 7.6; $rBore = 4.6; $spokeHalf = 1.9
        $pitch = [Math]::PI * 2 / $teeth
        $tipHalf = $pitch * 0.22
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
