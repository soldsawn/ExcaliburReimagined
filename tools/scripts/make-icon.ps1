# Draws the app icon (docs/09 visual language: near-black square, red corner brackets, a slanted red "E")
# and writes src\Excalibur.App\Assets\app.ico (PNG frames 16..256) plus app-256.png for a look.
# Usage: powershell -ExecutionPolicy Bypass -File tools\scripts\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\..\src\Excalibur.App\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $red = [System.Drawing.Color]::FromArgb(255, 226, 28, 52)
    $dark = [System.Drawing.Color]::FromArgb(255, 12, 12, 14)
    $edge = [System.Drawing.Color]::FromArgb(255, 44, 44, 50)

    # plate
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $dark), 8 * $s, 8 * $s, 240 * $s, 240 * $s)
    $g.DrawRectangle((New-Object System.Drawing.Pen $edge, ([Math]::Max(1, 2 * $s))), 8 * $s, 8 * $s, 240 * $s, 240 * $s)

    # corner brackets, top left and bottom right
    $t = [Math]::Max(2, 12 * $s); $arm = 70 * $s
    $pen = New-Object System.Drawing.Pen $red, $t
    $pen.LineJoin = 'Miter'
    $o = 8 * $s + $t / 2
    $g.DrawLines($pen, @(
        (New-Object System.Drawing.PointF $o, ($o + $arm)),
        (New-Object System.Drawing.PointF $o, $o),
        (New-Object System.Drawing.PointF ($o + $arm), $o)))
    $e = 248 * $s - $t / 2
    $g.DrawLines($pen, @(
        (New-Object System.Drawing.PointF $e, ($e - $arm)),
        (New-Object System.Drawing.PointF $e, $e),
        (New-Object System.Drawing.PointF ($e - $arm), $e)))

    # slanted "E": a spine and three arms, every bar sheared the same way
    $skew = 0.22
    $bar = {
        param($x, $y, $w, $h)
        $dx = $skew * $h
        $pts = @(
            (New-Object System.Drawing.PointF (($x + $dx) * $s), ($y * $s)),
            (New-Object System.Drawing.PointF (($x + $w + $dx) * $s), ($y * $s)),
            (New-Object System.Drawing.PointF (($x + $w) * $s), (($y + $h) * $s)),
            (New-Object System.Drawing.PointF ($x * $s), (($y + $h) * $s)))
        $g.FillPolygon((New-Object System.Drawing.SolidBrush $red), $pts)
    }
    & $bar 64 64 112 30     # top arm
    & $bar 64 113 92 30     # middle arm
    & $bar 64 162 112 30    # bottom arm
    # spine as one sheared parallelogram on the left
    $sp = @(
        (New-Object System.Drawing.PointF ((64 + $skew * 128) * $s), (64 * $s)),
        (New-Object System.Drawing.PointF ((92 + $skew * 128) * $s), (64 * $s)),
        (New-Object System.Drawing.PointF (92 * $s), (192 * $s)),
        (New-Object System.Drawing.PointF (64 * $s), (192 * $s)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $red), $sp)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($n in $sizes) {
    $bmp = Draw $n
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($n -eq 256) { $bmp.Save((Join-Path $out 'app-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $n; Bytes = $ms.ToArray() }
}

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$f.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $out 'app.ico'), $ico.ToArray())
Write-Host "wrote $(Join-Path $out 'app.ico')"
