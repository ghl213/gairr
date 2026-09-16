@echo off
chcp 65001 >nul
:: ============================================
:: frpc (工作电脑) 一键启动脚本
:: ============================================

set FRP_DIR=%~dp0frp_0.58.1_windows_amd64
set CONFIG=%~dp0frpc.toml

echo === GAIRR 工作电脑 - frpc 启动 ===
echo.

:: 检查 frpc 是否存在
if not exist "%FRP_DIR%\frpc.exe" (
    echo [下载 frp...]
    powershell -Command "Invoke-WebRequest -Uri 'https://github.com/fatedier/frp/releases/download/v0.58.1/frp_0.58.1_windows_amd64.zip' -OutFile 'frp.zip'"
    powershell -Command "Expand-Archive -Path 'frp.zip' -DestinationPath '%~dp0' -Force"
    del frp.zip
)

echo [启动 frpc...]
echo 配置文件: %CONFIG%
echo.

"%FRP_DIR%\frpc.exe" -c "%CONFIG%"

:: 如果退出，暂停显示错误
if errorlevel 1 (
    echo.
    echo ❌ frpc 异常退出
    pause
)
