# Draws the AiManager mark and writes src/AiManager/app.ico (PNG-compressed entries at several sizes).
# Run it again only if the mark changes; the generated .ico is checked in.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0
    $r = 14 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(2 * $s, 2 * $s, $r * 2, $r * 2, 180, 90)
    $path.AddArc(62 * $s - $r * 2, 2 * $s, $r * 2, $r * 2, 270, 90)
    $path.AddArc(62 * $s - $r * 2, 62 * $s - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(2 * $s, 62 * $s - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0x1B, 0x1F, 0x27))), $path)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single](4 * $s))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pts = @((16, 44), (16, 32), (48, 32), (48, 44)) | ForEach-Object { New-Object System.Drawing.PointF([single]($_[0] * $s), [single]($_[1] * $s)) }
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)
    $g.DrawLine($pen, [single](32 * $s), [single](22 * $s), [single](32 * $s), [single](32 * $s))
    $white = [System.Drawing.Brushes]::White
    foreach ($dot in @((25, 9), (9, 41), (41, 41))) { $g.FillEllipse($white, [single]($dot[0] * $s), [single]($dot[1] * $s), [single](14 * $s), [single](14 * $s)) }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$out = Join-Path $PSScriptRoot '..\src\AiManager\app.ico'
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter($fs)
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
$w.Dispose(); $fs.Dispose()
"wrote $out ($((Get-Item $out).Length) bytes, $($sizes.Count) sizes)"
