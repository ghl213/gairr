# 从 icon_src.png 生成 GAIRR.exe 应用程序图标 app.ico
# 包含 16/32/48/256 多尺寸，直接以 PNG 帧写入 ICO 容器
Add-Type -AssemblyName System.Drawing

$srcPath = 'd:\work\gairr\app\GAIRR\icon_src.png'
$outPath = 'd:\work\gairr\app\GAIRR\app.ico'
$sizes = @(256, 48, 32, 16)

$src = [System.Drawing.Image]::FromFile($srcPath)
try {
    $frames = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'HighQuality'
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.PixelOffsetMode = 'HighQuality'
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($src, 0, 0, $s, $s)
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += ,@($s, $ms.ToArray())
        $g.Dispose(); $bmp.Dispose(); $ms.Dispose()
    }
} finally {
    $src.Dispose()
}

$fs = [System.IO.File]::OpenWrite($outPath)
try {
    $bw = New-Object System.IO.BinaryWriter($fs)
    # ICO 头：reserved=0, type=1, count
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = [byte]($(if ($f[0] -eq 256) { 0 } else { $f[0] }))
        $bw.Write($dim); $bw.Write($dim)          # width, height
        $bw.Write([byte]0); $bw.Write([byte]0)    # colors, reserved
        $bw.Write([UInt16]1); $bw.Write([UInt16]32) # planes, bitcount
        $bw.Write([UInt32]$f[1].Length); $bw.Write([UInt32]$offset)
        $offset += $f[1].Length
    }
    foreach ($f in $frames) { $bw.Write($f[1]) }
    $bw.Flush()
} finally {
    $fs.Dispose()
}
Write-Host "generated $outPath ($($frames.Count) frames)"
