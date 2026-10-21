param(
    [string]$Source = "D:\Projects\art\leaf.png",
    [string]$Ico = "D:\Projects\Rou\src\Rou\assets\rou.ico",
    [string]$Png = "D:\Projects\Rou\docs\logo.png"
)

Add-Type -AssemblyName System.Drawing

$bg = [System.Drawing.ColorTranslator]::FromHtml("#F1F8F3")
$sizes = @(256, 64, 48, 32, 16)
$blobs = @()

$src = [System.Drawing.Image]::FromFile($Source)

# 统一底色：把圆角卡片外的白角填平，避免缩放后出现白边
$flat = New-Object System.Drawing.Bitmap(1024, 1024, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$fg = [System.Drawing.Graphics]::FromImage($flat)
$fg.Clear($bg)
$fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$fg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
# 先裁掉外围 4%（圆角卡片白边），再放大铺满画布
$inset = [int]($src.Width * 0.105)
$rect = New-Object System.Drawing.Rectangle($inset, $inset, ($src.Width - 2 * $inset), ($src.Height - 2 * $inset))
$crop = $src.Clone($rect, $src.PixelFormat)
$fg.DrawImage($crop, 0, 0, 1024, 1024)
$crop.Dispose()
$fg.Dispose()
$src.Dispose()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.Clear($bg)
    $g.DrawImage($flat, 0, 0, $s, $s)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += , @{ Size = $s; Data = $ms.ToArray() }
    $bmp.Dispose()
    $ms.Dispose()
}

# 文档用 logo：256px
$logo = New-Object System.Drawing.Bitmap(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$lg = [System.Drawing.Graphics]::FromImage($logo)
$lg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$lg.Clear($bg)
$lg.DrawImage($flat, 0, 0, 256, 256)
$lg.Dispose()
$logo.Save($Png, [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
$flat.Dispose()

$dir = Split-Path $Ico -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$fs = New-Object System.IO.FileStream($Ico, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$blobs.Count)
$offset = 6 + 16 * $blobs.Count
foreach ($b in $blobs) {
    $dim = if ($b.Size -ge 256) { 0 } else { $b.Size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$b.Data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $b.Data.Length
}
foreach ($b in $blobs) { $bw.Write($b.Data) }
$bw.Flush()
$bw.Close()
$fs.Close()

"ico: " + [math]::Round((Get-Item $Ico).Length / 1KB, 1) + " KB"
"png: " + [math]::Round((Get-Item $Png).Length / 1KB, 1) + " KB"
