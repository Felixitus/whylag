# Renders the WhyLag icon (blue rounded square with a white "lag spike" line) into a multi-size .ico.
# The result is committed as assets/whylag.ico; run again only when the design changes.
param([string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) "assets/whylag.ico"))
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $size / 32.0
    $r = 10 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(1 * $s, 1 * $s, $r, $r, 180, 90)
    $path.AddArc(31 * $s - $r, 1 * $s, $r, $r, 270, 90)
    $path.AddArc(31 * $s - $r, 31 * $s - $r, $r, $r, 0, 90)
    $path.AddArc(1 * $s, 31 * $s - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(37, 99, 235))), $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([math]::Max(1.5, 2.6 * $s))
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $pts = @(@(6, 19), @(11, 19), @(14, 8), @(18, 25), @(21, 16), @(26, 16)) | ForEach-Object { New-Object System.Drawing.PointF ($_[0] * $s), ($_[1] * $s) }
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    , $ms.ToArray()
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Host "Wrote $Out"
