# 解压 frp.zip
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = "d:\work\gairr\site\gairr-mobile\deploy\frp.zip"
$extractPath = "d:\work\gairr\site\gairr-mobile\deploy"

if (Test-Path $zipPath) {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extractPath)
    Write-Host "解压完成"
} else {
    Write-Host "找不到 frp.zip"
}
