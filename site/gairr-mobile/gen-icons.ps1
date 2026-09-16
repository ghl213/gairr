# GAIRR Mobile PWA 图标生成脚本
# 从源图标 icon_src.png 缩放生成 192/512 PNG
# 如需回到旧的绘制方案（品牌深蓝底 + 红色 G），可恢复旧实现：用背景 #1a1a2e、圆环 #0f3460、字母 G #e94560
Add-Type -AssemblyName System.Drawing

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcPath = 'd:\work\gairr\app\GAIRR\icon_src.png'
if (-not (Test-Path $srcPath)) {
    Write-Error "source icon not found: $srcPath"
    exit 1
}

function New-GairrIcon([System.Drawing.Image]$src, [int]$size, [string]$outPath) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'HighQuality'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $size, $size)
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "generated $outPath ($size x $size)"
}

try {
    $src = [System.Drawing.Image]::FromFile($srcPath)
    New-GairrIcon $src 192 (Join-Path $dir 'icon-192.png')
    New-GairrIcon $src 512 (Join-Path $dir 'icon-512.png')
} finally {
    if ($src) { $src.Dispose() }
}
