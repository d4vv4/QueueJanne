# Draws the QueueJanne icon (a green "QJ" terminal prompt) and writes Assets\queuejanne.ico and queuejanne.png.
# Windows only (System.Drawing). Usage: powershell -File tools\Icon\New-Icon.ps1
param([string]$OutputFolder = (Join-Path $PSScriptRoot "..\..\src\QueueJanne.App\Assets"))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutputFolder | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$size) {
    $detailed = $size -ge 48
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $g.TextRenderingHint = "AntiAliasGridFit"
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Terminal window: dark rounded square with a thin border.
    $pad = [Math]::Max(1, 8 * $s)
    $frame = New-RoundedPath $pad $pad ($size - 2 * $pad) ($size - 2 * $pad) ([Math]::Max(2, 36 * $s))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 13, 17, 23))), $frame)
    if ($detailed) {
        $g.SetClip($frame)
        $scan = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(18, 57, 211, 83)), ([Math]::Max(1, 2 * $s))
        for ($y = $pad; $y -lt $size; $y += 6 * $s) { $g.DrawLine($scan, 0, $y, $size, $y) }
        $g.ResetClip()

        # Window buttons.
        $dot = 14 * $s
        $colors = @(@(255, 95, 86), @(255, 189, 46), @(39, 201, 63))
        for ($i = 0; $i -lt 3; $i++) {
            $c = $colors[$i]
            $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, $c[0], $c[1], $c[2]))), (34 + $i * 24) * $s, 30 * $s, $dot, $dot)
        }
    }
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 48, 54, 61)), ([Math]::Max(1, 3 * $s))), $frame)

    $green = [System.Drawing.Color]::FromArgb(255, 57, 211, 83)
    $format = New-Object System.Drawing.StringFormat
    $format.FormatFlags = "NoClip"
    if ($detailed) {
        $font = New-Object System.Drawing.Font "Consolas", (118 * $s), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $prompt = New-Object System.Drawing.Font "Consolas", (70 * $s), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $g.DrawString(">", $prompt, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 57, 211, 83))), 22 * $s, 108 * $s, $format)
        $textX = 62 * $s; $textY = 70 * $s
        # Glow: the text drawn faintly a few times around its position.
        foreach ($o in @(-4, -2, 2, 4)) {
            $glow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(28, 57, 211, 83))
            $g.DrawString("QJ", $font, $glow, $textX + $o * $s, $textY, $format)
            $g.DrawString("QJ", $font, $glow, $textX, $textY + $o * $s, $format)
        }
        $g.DrawString("QJ", $font, (New-Object System.Drawing.SolidBrush $green), $textX, $textY, $format)
        # Block cursor after the text.
        $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(220, 57, 211, 83))), 200 * $s, 170 * $s, 26 * $s, 12 * $s)
    }
    else {
        $font = New-Object System.Drawing.Font "Consolas", ($size * 0.56), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $center = New-Object System.Drawing.StringFormat
        $center.Alignment = "Center"; $center.LineAlignment = "Center"
        $rect = New-Object System.Drawing.RectangleF 0, ($size * 0.02), $size, $size
        $g.DrawString("QJ", $font, (New-Object System.Drawing.SolidBrush $green), $rect, $center)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $OutputFolder "queuejanne.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $stream.ToArray()
}

# ICO file: header, one directory entry per size, then the PNG images.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$images[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
[System.IO.File]::WriteAllBytes((Join-Path $OutputFolder "queuejanne.ico"), $out.ToArray())
"Wrote $OutputFolder\queuejanne.ico and queuejanne.png"
