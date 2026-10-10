# Draws the MicBridge icon and writes every size both apps need:
#   pc\app.ico                                   (16-256 px, used for the .exe, window and tray)
#   android\res\mipmap-*\ic_launcher.png         (legacy launcher icon)
#   android\res\mipmap-*\ic_launcher_fg.png      (foreground layer of the adaptive icon, Android 8+)
# Run it again only if you change the drawing below; the generated files are kept in the project.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot

function New-Canvas([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    return $bmp, $g
}

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# The microphone glyph, drawn inside a square of side $s whose top-left corner is ($ox, $oy).
function Draw-Mic($g, [single]$ox, [single]$oy, [single]$s) {
    $white = [System.Drawing.Color]::White
    $brush = New-Object System.Drawing.SolidBrush($white)
    $stroke = [Math]::Max(1.0, $s * 0.075)
    $pen = New-Object System.Drawing.Pen($white, [single]$stroke)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'

    $capW = $s * 0.30; $capH = $s * 0.50
    $capX = $ox + ($s - $capW) / 2; $capY = $oy + $s * 0.06
    $capsule = New-RoundRect $capX $capY $capW $capH ($capW / 2)
    $g.FillPath($brush, $capsule)

    $arcW = $s * 0.56; $arcX = $ox + ($s - $arcW) / 2
    $arcY = $oy + $s * 0.40 - $arcW / 2
    $g.DrawArc($pen, [single]$arcX, [single]$arcY, [single]$arcW, [single]$arcW, 0, 180)

    $cx = $ox + $s / 2
    $stemTop = $oy + $s * 0.40 + $arcW / 2
    $stemBottom = $oy + $s * 0.92
    $g.DrawLine($pen, [single]$cx, [single]$stemTop, [single]$cx, [single]$stemBottom)
    $g.DrawLine($pen, [single]($cx - $s * 0.16), [single]$stemBottom, [single]($cx + $s * 0.16), [single]$stemBottom)

    $capsule.Dispose(); $pen.Dispose(); $brush.Dispose()
}

function New-Background($g, [single]$size, [single]$radius) {
    $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::FromArgb(255, 14, 165, 233), [System.Drawing.Color]::FromArgb(255, 79, 70, 229), 55.0)
    if ($radius -gt 0) { $p = New-RoundRect 0 0 $size $size $radius; $g.FillPath($grad, $p); $p.Dispose() }
    else { $g.FillRectangle($grad, $rect) }
    $grad.Dispose()
}

# Full icon: rounded gradient tile with the microphone on it.
function New-Icon([int]$size) {
    $bmp, $g = New-Canvas $size
    New-Background $g $size ($size * 0.22)
    $glyph = $size * 0.62
    Draw-Mic $g (($size - $glyph) / 2) (($size - $glyph) / 2) $glyph
    $g.Dispose()
    return $bmp
}

# Adaptive-icon foreground: the glyph alone, inside the centre "safe zone" (the launcher adds the shape).
function New-Foreground([int]$size) {
    $bmp, $g = New-Canvas $size
    $glyph = $size * 0.44
    Draw-Mic $g (($size - $glyph) / 2) (($size - $glyph) / 2) $glyph
    $g.Dispose()
    return $bmp
}

function Save-Png($bmp, [string]$path) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

# ---- Windows .ico: classic 32-bit bitmaps for the small sizes, PNG for 256 ----
function Get-DibBytes($bmp) {
    $n = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$n); $bw.Write([int]($n * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) { $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) }
    }
    $maskRow = [int]([Math]::Ceiling($n / 32.0) * 4)        # 1-bit AND mask, all zero: alpha does the work
    $bw.Write((New-Object byte[] ($maskRow * $n)))
    $bw.Flush()
    return ,$ms.ToArray()
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 256
$images = @()
foreach ($s in $sizes) {
    $bmp = New-Icon $s
    if ($s -eq 256) { $images += ,(Get-PngBytes $bmp) } else { $images += ,(Get-DibBytes $bmp) }
    $bmp.Dispose()
}
$icoPath = Join-Path $root 'pc\app.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$images[$i].Length); $bw.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush(); $fs.Close()
"wrote $icoPath"

# ---- Android launcher icons ----
$res = Join-Path $root 'android\res'
$densities = @{ 'mdpi' = 1.0; 'hdpi' = 1.5; 'xhdpi' = 2.0; 'xxhdpi' = 3.0; 'xxxhdpi' = 4.0 }
foreach ($d in $densities.Keys) {
    $legacy = New-Icon ([int](48 * $densities[$d]))
    Save-Png $legacy (Join-Path $res "mipmap-$d\ic_launcher.png"); $legacy.Dispose()
    $fg = New-Foreground ([int](108 * $densities[$d]))
    Save-Png $fg (Join-Path $res "mipmap-$d\ic_launcher_fg.png"); $fg.Dispose()
}
"wrote Android mipmaps to $res"

# A large preview so the drawing can be checked by eye.
$preview = New-Icon 256
Save-Png $preview (Join-Path $PSScriptRoot 'icon-preview.png'); $preview.Dispose()
