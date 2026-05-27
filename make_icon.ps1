Add-Type -AssemblyName System.Drawing

function Add-RoundedRect {
    param(
        [System.Drawing.Drawing2D.GraphicsPath]$Path,
        [single]$X, [single]$Y, [single]$W, [single]$H, [single]$R
    )
    $R = [Math]::Min($R, [Math]::Min($W, $H) / 2)
    $d = $R * 2
    if ($R -le 0.01) {
        $Path.AddRectangle((New-Object System.Drawing.RectangleF $X, $Y, $W, $H))
        return
    }
    $Path.AddArc($X,           $Y,           $d, $d, 180, 90)
    $Path.AddArc($X + $W - $d, $Y,           $d, $d, 270, 90)
    $Path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d,   0, 90)
    $Path.AddArc($X,           $Y + $H - $d, $d, $d,  90, 90)
    $Path.CloseFigure()
}

# Returns: roundedrect path snapped to whole pixels at size $s
function Get-BgPath {
    param([int]$Size, [single]$Pad, [single]$RadiusFrac)
    $w   = $Size - $Pad * 2
    $rad = [single]([Math]::Round($w * $RadiusFrac))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundedRect -Path $path -X $Pad -Y $Pad -W $w -H $w -R $rad
    return $path
}

# Snap to nearest whole pixel — keeps small icons crisp
function Px { param($v) [single]([Math]::Round([double]$v)) }

function New-EdiIconPng {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.Clear([System.Drawing.Color]::Transparent)

    $isSmall = $Size -le 32        # tiny: taskbar territory — strip detail
    $isMed   = $Size -le 64        # no cyan accent at medium either, looks muddy

    # ===== Background =====
    if ($isSmall) {
        # Solid magenta, smaller corner radius, no inner highlight, no padding
        $pad = 0
        $bg = Get-BgPath -Size $Size -Pad $pad -RadiusFrac 0.14
        $bgBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 222, 38, 124))
        $g.FillPath($bgBrush, $bg)
        $bgBrush.Dispose()
    } else {
        $pad = [single]([Math]::Max(1, [Math]::Round($Size * 0.03)))
        $bg = Get-BgPath -Size $Size -Pad $pad -RadiusFrac 0.20
        $bgRect = New-Object System.Drawing.RectangleF $pad, $pad, ($Size - $pad * 2), ($Size - $pad * 2)
        $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $bgRect, ([System.Drawing.Color]::FromArgb(255, 255, 77, 159)), ([System.Drawing.Color]::FromArgb(255, 199, 31, 110)), ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $g.FillPath($bgBrush, $bg)
        $hi = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), ([single][Math]::Max(1, $Size * 0.012))
        $g.DrawPath($hi, $bg)
        $bgBrush.Dispose(); $hi.Dispose()
    }
    $bg.Dispose()

    # ===== The "E" =====
    # At tiny sizes, snap everything to whole pixels and use a thicker bar
    # for legibility. At larger sizes, use the proportional layout with
    # rounded ends and a cyan accent dot.
    $ePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $ePath.FillMode = [System.Drawing.Drawing2D.FillMode]::Winding

    if ($isSmall) {
        # Pixel-aligned, beefier stroke, smaller end-radius
        $eX = Px ($Size * 0.28)
        $eY = Px ($Size * 0.22)
        $eW = Px ($Size * 0.46)
        $eH = Px ($Size * 0.56)
        $stroke = Px ([Math]::Max(2, $Size * 0.155))
        $rBar   = [single]([Math]::Min($stroke * 0.32, 2))
        $midW   = Px ($eW * 0.74)

        Add-RoundedRect -Path $ePath -X $eX -Y $eY -W $stroke -H $eH -R $rBar
        Add-RoundedRect -Path $ePath -X $eX -Y $eY -W $eW     -H $stroke -R $rBar
        Add-RoundedRect -Path $ePath -X $eX -Y (Px ($eY + ($eH - $stroke) / 2)) -W $midW -H $stroke -R $rBar
        Add-RoundedRect -Path $ePath -X $eX -Y (Px ($eY + $eH - $stroke))       -W $eW   -H $stroke -R $rBar
    } else {
        $eX = $Size * 0.27
        $eY = $Size * 0.21
        $eW = $Size * 0.50
        $eH = $Size * 0.58
        $stroke = $Size * 0.105
        $rBar   = $stroke * 0.42
        $midW   = $eW * 0.74

        Add-RoundedRect -Path $ePath -X $eX -Y $eY -W $stroke -H $eH -R $rBar
        Add-RoundedRect -Path $ePath -X $eX -Y $eY -W $eW     -H $stroke -R $rBar
        Add-RoundedRect -Path $ePath -X $eX -Y ($eY + ($eH - $stroke) / 2) -W $midW -H $stroke -R $rBar
        Add-RoundedRect -Path $ePath -X $eX -Y ($eY + $eH - $stroke)       -W $eW   -H $stroke -R $rBar
    }

    $whiteBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $g.FillPath($whiteBrush, $ePath)
    $whiteBrush.Dispose()
    $ePath.Dispose()

    # ===== Cyan accent dot — large sizes only =====
    if (-not $isMed) {
        $stroke = $Size * 0.105
        $eX = $Size * 0.27
        $eY = $Size * 0.21
        $eW = $Size * 0.50
        $dotR = $stroke * 0.38
        $dotCx = $eX + $eW
        $dotCy = $eY + $stroke / 2
        $dotBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 229, 255))
        $g.FillEllipse($dotBrush, ($dotCx - $dotR), ($dotCy - $dotR), $dotR * 2, $dotR * 2)
        $halo = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(180, 255, 255, 255)), ([single][Math]::Max(1, $Size * 0.008))
        $g.DrawEllipse($halo, ($dotCx - $dotR), ($dotCy - $dotR), $dotR * 2, $dotR * 2)
        $dotBrush.Dispose(); $halo.Dispose()
    }

    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$pngs  = @{}
foreach ($s in $sizes) { $pngs[$s] = (New-EdiIconPng -Size $s) }

$out = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)
$dirSize = 6 + 16 * $sizes.Count
$offset  = $dirSize
$entries = @()
foreach ($s in $sizes) {
    $entries += [pscustomobject]@{ Size = $s; Length = $pngs[$s].Length; Offset = $offset }
    $offset += $pngs[$s].Length
}
foreach ($e in $entries) {
    $w = if ($e.Size -ge 256) { 0 } else { [byte]$e.Size }
    $h = if ($e.Size -ge 256) { 0 } else { [byte]$e.Size }
    $bw.Write([byte]$w); $bw.Write([byte]$h); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$e.Length); $bw.Write([uint32]$e.Offset)
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Flush()
$icoBytes = $out.ToArray()
$bw.Dispose(); $out.Dispose()

$target = Join-Path $PSScriptRoot 'Edi.Wpf\Edi.ico'
[System.IO.File]::WriteAllBytes($target, $icoBytes)
"Wrote $target ($($icoBytes.Length) bytes, $($sizes.Count) sizes)"
