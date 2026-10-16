# LlamaLauncher 应用图标生成
# 用法：pwsh -File scripts/make-icon.ps1 [-Preview]
# 产出：
#   src/LlamaLauncher.App/Assets/LlamaLauncher.ico   多尺寸应用图标（16/24/32/48/64/128/256，PNG 压缩条目）
#   src/LlamaLauncher.App/Assets/LlamaLauncher.png   512×512 母版（透明背景）
#   artifacts/icon-preview/icon-preview.png          各尺寸效果预览（仅 -Preview）
# 设计：品牌蓝渐变圆角方块（延续原托盘配色）+ 白色终端提示符 “>_”（启动器 / 命令行工具的通用符号）。
# 笔画为圆角粗线；小尺寸自动加粗笔画并轻微放大，保证 16px 下仍清晰可辨。

param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$Preview
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$TileTop = [System.Drawing.Color]::FromArgb(74, 111, 198)      # #4A6FC6
$TileBottom = [System.Drawing.Color]::FromArgb(49, 84, 158)    # #31549E
$White = [System.Drawing.Color]::White

# ---- 造型参数（1024 坐标系，y 轴向下）----
$TileInset = 64.0
$TileRadius = 206.0
$StrokeWidth = 92.0
$ChevronTop = [System.Drawing.PointF]::new(352, 330)
$ChevronTip = [System.Drawing.PointF]::new(522, 512)
$ChevronBottom = [System.Drawing.PointF]::new(352, 694)
$CursorStart = [System.Drawing.PointF]::new(600, 694)
$CursorEnd = [System.Drawing.PointF]::new(676, 694)
$MarkPivotX = 512.0
$MarkPivotY = 512.0

function New-RoundedRectPath {
    param([float]$X, [float]$Y, [float]$W, [float]$H, [float]$Radius)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [float](2 * [Math]::Min($Radius, [Math]::Min($W, $H) / 2 - 0.5))
    $path.AddArc($X, $Y, $d, $d, 180.0, 90.0)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270.0, 90.0)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0.0, 90.0)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90.0, 90.0)
    $path.CloseFigure()
    return $path
}

# 终端提示符：> 折线 + 下划线光标（圆角粗笔画，围绕中心缩放）
function Add-PromptMark {
    param([System.Drawing.Graphics]$Graphics, [float]$Scale, [float]$StrokeBoost)

    $pen = [System.Drawing.Pen]::new($White, [float]($StrokeWidth + $StrokeBoost))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    try {
        $state = $Graphics.Save()
        try {
            $Graphics.TranslateTransform([float]$MarkPivotX, [float]$MarkPivotY)
            $Graphics.ScaleTransform($Scale, $Scale)
            $Graphics.TranslateTransform([float](-$MarkPivotX), [float](-$MarkPivotY))
            $Graphics.DrawLines($pen, [System.Drawing.PointF[]]@($ChevronTop, $ChevronTip, $ChevronBottom))
            $Graphics.DrawLine($pen, $CursorStart, $CursorEnd)
        }
        finally {
            $Graphics.Restore($state)
        }
    }
    finally {
        $pen.Dispose()
    }
}

function New-IconBitmap {
    param([int]$Size)

    # 小尺寸：加粗笔画并轻微放大，保证可辨识
    $scale = if ($Size -ge 64) { 1.0 } elseif ($Size -ge 32) { 1.02 } else { 1.06 }
    $strokeBoost = if ($Size -ge 64) { 0.0 } elseif ($Size -ge 32) { 6.0 } else { 12.0 }

    # 8 倍超采样，最后高质量降采样
    $supersample = 8
    $pixels = $Size * $supersample
    $canvas = [System.Drawing.Bitmap]::new(
        $pixels,
        $pixels,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.ScaleTransform([float]($pixels / 1024.0), [float]($pixels / 1024.0))

        # 圆角方块底（垂直渐变）
        $tileRect = [System.Drawing.RectangleF]::new(
            [float]$TileInset,
            [float]$TileInset,
            [float](1024 - 2 * $TileInset),
            [float](1024 - 2 * $TileInset))
        $tileBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            $tileRect,
            $TileTop,
            $TileBottom,
            [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        try {
            $tilePath = New-RoundedRectPath `
                -X $TileInset -Y $TileInset `
                -W (1024 - 2 * $TileInset) -H (1024 - 2 * $TileInset) -Radius $TileRadius
            $graphics.FillPath($tileBrush, $tilePath)
            $tilePath.Dispose()
        }
        finally {
            $tileBrush.Dispose()
        }

        Add-PromptMark -Graphics $graphics -Scale $scale -StrokeBoost $strokeBoost
    }
    finally {
        $graphics.Dispose()
    }

    $final = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $scaler = [System.Drawing.Graphics]::FromImage($final)
    try {
        $scaler.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $scaler.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scaler.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $scaler.DrawImage($canvas, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))
    }
    finally {
        $scaler.Dispose()
        $canvas.Dispose()
    }

    return $final
}

function Write-IcoFile {
    param([string]$Path, [System.Drawing.Bitmap[]]$Bitmaps)

    $stream = [System.IO.File]::Create($Path)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        # ICONDIR
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$Bitmaps.Count)

        $blobs = [System.Collections.Generic.List[byte[]]]::new()
        $offset = 6 + 16 * $Bitmaps.Count
        foreach ($bitmap in $Bitmaps) {
            $memory = [System.IO.MemoryStream]::new()
            $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            $bytes = $memory.ToArray()
            $memory.Dispose()
            $blobs.Add($bytes)

            $dimension = if ($bitmap.Width -ge 256) { 0 } else { $bitmap.Width }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $bytes.Length
        }

        foreach ($bytes in $blobs) {
            $writer.Write($bytes)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function New-PreviewSheet {
    param([hashtable]$Bitmaps, [string]$Path)

    $width = 1240
    $height = 760
    $sheet = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($sheet)
    try {
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        # 深色带：大尺寸（512/256/128/64）
        $graphics.Clear([System.Drawing.Color]::FromArgb(255, 43, 43, 43))
        $x = 24
        foreach ($size in @(512, 256, 128, 64)) {
            $graphics.DrawImage($Bitmaps[$size], [System.Drawing.Rectangle]::new($x, 32, $size, $size))
            $x += $size + 24
        }

        # 浅色带：中小尺寸 1:1（48/32/24/16）
        $bandTop = 600
        $bandBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 242, 242, 242))
        $graphics.FillRectangle($bandBrush, [System.Drawing.Rectangle]::new(0, $bandTop, $width, $height - $bandTop))
        $bandBrush.Dispose()
        $x = 24
        foreach ($size in @(48, 32, 24, 16)) {
            $graphics.DrawImage($Bitmaps[$size], [System.Drawing.Rectangle]::new($x, $bandTop + 24, $size, $size))
            $x += $size + 24
        }

        # 放大检查（最近邻）：32/24/16
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        $x = 220
        foreach ($size in @(32, 24, 16)) {
            $zoom = 8
            $graphics.DrawImage(
                $Bitmaps[$size],
                [System.Drawing.Rectangle]::new($x, $bandTop + 24, $size * $zoom, $size * $zoom))
            $x += $size * $zoom + 32
        }
    }
    finally {
        $graphics.Dispose()
    }

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }

    $sheet.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
}

$assetsDirectory = Join-Path $RepoRoot 'src\LlamaLauncher.App\Assets'
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$bitmaps = @{}
foreach ($size in $sizes) {
    $bitmaps[$size] = New-IconBitmap -Size $size
}

$icoPath = Join-Path $assetsDirectory 'LlamaLauncher.ico'
Write-IcoFile -Path $icoPath -Bitmaps ($sizes | ForEach-Object { $bitmaps[$_] })
Write-Host "已写入 $icoPath（$($sizes -join ', ')）" -ForegroundColor Cyan

$masterPath = Join-Path $assetsDirectory 'LlamaLauncher.png'
$master = New-IconBitmap -Size 512
$bitmaps[512] = $master
$master.Save($masterPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "已写入 $masterPath（512×512）" -ForegroundColor Cyan

if ($Preview) {
    $previewPath = Join-Path $RepoRoot 'artifacts\icon-preview\icon-preview.png'
    New-PreviewSheet -Bitmaps $bitmaps -Path $previewPath
    Write-Host "已写入 $previewPath" -ForegroundColor Cyan
}

foreach ($bitmap in $bitmaps.Values) {
    $bitmap.Dispose()
}
