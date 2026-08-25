<#
    Regenerates the Teeline app icon (.ico) and splash screen (.png) from code.
    Re-run this after touching the drawing logic below; nothing here is
    consumed at build time, so the generated files are what actually ship.
#>
param(
    [string]$AssetsDir = $PSScriptRoot
)

Add-Type -AssemblyName System.Drawing

$Teal = [System.Drawing.Color]::FromArgb(255, 14, 124, 102)      # #0E7C66 - brand colour
$TealDark = [System.Drawing.Color]::FromArgb(255, 10, 92, 76)
$Steam = [System.Drawing.Color]::FromArgb(150, 14, 124, 102)

function Draw-Kettle {
    param([System.Drawing.Graphics]$g, [double]$s, [double]$ox = 0, [double]$oy = 0)

    function P([double]$x, [double]$y) {
        New-Object System.Drawing.PointF (($x * $s) + $ox), (($y * $s) + $oy)
    }
    function Rect([double]$x, [double]$y, [double]$w, [double]$h) {
        New-Object System.Drawing.RectangleF (($x * $s) + $ox), (($y * $s) + $oy), ($w * $s), ($h * $s)
    }

    $bodyBrush = New-Object System.Drawing.SolidBrush $Teal
    $knobBrush = New-Object System.Drawing.SolidBrush $TealDark

    # Body: rounded kettle silhouette (wide base tapering to a neck)
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $body.AddBezier((P 20 68), (P 20 40), (P 32 30), (P 40 30))
    $body.AddLine((P 40 30), (P 60 30))
    $body.AddBezier((P 60 30), (P 68 30), (P 80 40), (P 80 68))
    $body.AddArc((Rect 20 58 60 22), 0, 90)
    $body.AddLine((P 74 80), (P 26 80))
    $body.AddArc((Rect 20 58 60 22), 90, 90)
    $body.CloseFigure()
    $g.FillPath($bodyBrush, $body)

    # Lid
    $lid = New-Object System.Drawing.Drawing2D.GraphicsPath
    $lid.AddArc((Rect 37 22 26 12), 180, 180)
    $lid.AddLine((P 63 28), (P 63 30))
    $lid.AddLine((P 37 30), (P 37 28))
    $lid.CloseFigure()
    $g.FillPath($bodyBrush, $lid)
    $g.FillEllipse($knobBrush, (Rect 46.5 20 7 7))

    # Spout (right side)
    $spout = New-Object System.Drawing.Drawing2D.GraphicsPath
    $spout.AddBezier((P 72 46), (P 84 41), (P 91 36), (P 93 26))
    $spout.AddBezier((P 93 26), (P 89 35), (P 82 49), (P 74 64))
    $spout.CloseFigure()
    $g.FillPath($bodyBrush, $spout)

    # Handle (left side, open C shape, touching the body)
    $handlePen = New-Object System.Drawing.Pen $Teal, (7 * $s)
    $handlePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $handlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($handlePen, (Rect 6 36 28 32), 85, 190)

    # Steam (soft, wavy - doubles as a nod to sound waves)
    $steamPen = New-Object System.Drawing.Pen $Steam, (4.2 * $s)
    $steamPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $steamPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawBezier($steamPen, (P 42 21), (P 38 15), (P 46 11), (P 42 4))
    $g.DrawBezier($steamPen, (P 58 21), (P 54 14), (P 62 10), (P 58 3))
}

function New-KettleBitmap {
    param([int]$Size, [bool]$Transparent = $true)
    $bmp = New-Object System.Drawing.Bitmap $Size, $Size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    if ($Transparent) { $g.Clear([System.Drawing.Color]::Transparent) }
    else { $g.Clear([System.Drawing.Color]::White) }
    Draw-Kettle -g $g -s ($Size / 100.0)
    $g.Dispose()
    return $bmp
}

# ── Build the multi-resolution .ico ─────────────────────────────────────
function Write-Ico {
    param([int[]]$Sizes, [string]$OutPath)

    $pngBlobs = @()
    foreach ($size in $Sizes) {
        $bmp = New-KettleBitmap -Size $size
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngBlobs += ,($ms.ToArray())
        $bmp.Dispose()
        $ms.Dispose()
    }

    $fs = New-Object System.IO.FileStream $OutPath, ([System.IO.FileMode]::Create)
    $bw = New-Object System.IO.BinaryWriter $fs

    # ICONDIR
    $bw.Write([UInt16]0)      # reserved
    $bw.Write([UInt16]1)      # type: icon
    $bw.Write([UInt16]$Sizes.Count)

    $headerSize = 6 + 16 * $Sizes.Count
    $offset = $headerSize
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $size = $Sizes[$i]
        $dim = if ($size -ge 256) { 0 } else { $size }   # 0 means 256 in ICO format
        $bw.Write([byte]$dim)          # width
        $bw.Write([byte]$dim)          # height
        $bw.Write([byte]0)             # color palette
        $bw.Write([byte]0)             # reserved
        $bw.Write([UInt16]1)           # color planes
        $bw.Write([UInt16]32)          # bits per pixel
        $bw.Write([UInt32]$pngBlobs[$i].Length)
        $bw.Write([UInt32]$offset)
        $offset += $pngBlobs[$i].Length
    }
    foreach ($blob in $pngBlobs) { $bw.Write($blob) }

    $bw.Flush()
    $bw.Dispose()
    $fs.Dispose()
}

Write-Ico -Sizes @(16, 24, 32, 48, 64, 128, 256) -OutPath (Join-Path $AssetsDir "teeline.ico")

# ── Build the splash screen ──────────────────────────────────────────────
$splashW = 520
$splashH = 300
$splash = New-Object System.Drawing.Bitmap $splashW, $splashH
$g = [System.Drawing.Graphics]::FromImage($splash)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.Clear([System.Drawing.Color]::White)
$g.DrawRectangle((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255,224,224,224)), 1),
    0, 0, $splashW - 1, $splashH - 1)

$markSize = 92
Draw-Kettle -g $g -s ($markSize / 100.0) -ox (($splashW - $markSize) / 2.0) -oy 26

$titleFont = New-Object System.Drawing.Font "Segoe UI", 22, ([System.Drawing.FontStyle]::Bold)
$titleBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,30,30,30))
$titleFormat = New-Object System.Drawing.StringFormat
$titleFormat.Alignment = [System.Drawing.StringAlignment]::Center
$g.DrawString("Teeline", $titleFont, $titleBrush, (New-Object System.Drawing.RectangleF 0, 195, $splashW, 40), $titleFormat)

$subFont = New-Object System.Drawing.Font "Segoe UI", 11
$subBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,150,150,150))
$g.DrawString("Starting...", $subFont, $subBrush, (New-Object System.Drawing.RectangleF 0, 240, $splashW, 30), $titleFormat)

$g.Dispose()
$splash.Save((Join-Path $AssetsDir "splash.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$splash.Dispose()

Write-Output "Wrote teeline.ico and splash.png to $AssetsDir"
