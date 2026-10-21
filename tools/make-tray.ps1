param(
    [string]$Ico = "D:\Projects\Rou\src\Rou\assets\rou-tray.ico"
)

Add-Type -AssemblyName System.Drawing

# 托盘专用图标：高对比度 —— 实心绿底 + 白色叶片，16px 下也能一眼认出
$green = [System.Drawing.Color]::FromArgb(255, 63, 158, 112)
$white = [System.Drawing.Color]::White
$sizes = @(16, 20, 24, 32, 48)
$blobs = @()

function New-LeafPath([single]$s) {
    # 两端尖的叶片轮廓（两条二次曲线围成）
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $inset = $s * 0.22
    $cx = $s / 2
    $top = $inset
    $bottom = $s - $inset
    $half = ($bottom - $top) / 2
    $wide = $s * 0.30
    # 右侧更饱满、左侧收拢，形成叶形而非对称的眼形
    $p.AddBezier($cx, $top, ($cx + $wide * 1.06), ($top + $half * 0.30), ($cx + $wide * 0.92), ($bottom - $half * 0.26), ($cx - $wide * 0.12), $bottom)
    $p.AddBezier(($cx - $wide * 0.12), $bottom, ($cx - $wide * 0.92), ($bottom - $half * 0.34), ($cx - $wide * 0.62), ($top + $half * 0.30), $cx, $top)
    $p.CloseFigure()
    return $p
}

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # 绿底圆角方块
    $pad = [single]($s * 0.06)
    $r = [single]($s * 0.24)
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad))
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $tile.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $tile.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $tile.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $tile.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush($green)
    $g.FillPath($brush, $tile)

    # 白色叶片
    $leaf = New-LeafPath $s
    $leafBrush = New-Object System.Drawing.SolidBrush($white)
    $g.FillPath($leafBrush, $leaf)

    if ($s -ge 24) {
        # 大尺寸加一条叶脉，小尺寸省略避免糊成一团
        $pen = New-Object System.Drawing.Pen($green, [single][Math]::Max(1, $s * 0.055))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($pen, ($s * 0.48), ($s * 0.32), ($s * 0.40), ($s * 0.70))
        $pen.Dispose()
    }

    $g.Dispose()
    $brush.Dispose()
    $leafBrush.Dispose()
    $tile.Dispose()
    $leaf.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += , @{ Size = $s; Data = $ms.ToArray() }
    $bmp.Dispose()
    $ms.Dispose()
}

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

"tray ico: " + [math]::Round((Get-Item $Ico).Length / 1KB, 1) + " KB"
