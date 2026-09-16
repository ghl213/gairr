@echo off
chcp 65001 >nul
:: ============================================
:: GAIRR 工作电脑 - 一键启动所有服务
:: ============================================

set GAIRR_DIR=d:\work\gairr
set FRP_DIR=%~dp0frp_0.58.1_windows_amd64

echo === GAIRR 工作电脑启动 ===
echo.

:: 1. 启动 GAIRR.Server
echo [1/3] 启动 GAIRR.Server...
start "GAIRR Server" cmd /c "cd /d %GAIRR_DIR%\app\GAIRR.Server\bin\Debug\net8.0-windows && gairr-agent-server.exe --host 0.0.0.0 --port 8123"
timeout /t 3 >nul

:: 2. 启动 frpc
echo [2/3] 启动 frpc (内网穿透)...
if exist "%FRP_DIR%\frpc.exe" (
    start "frpc" cmd /c "\"%FRP_DIR%\frpc.exe\" -c \"%~dp0frpc.toml\""
) else (
    echo ⚠️ frpc 未找到，请先运行 start-frpc.bat 下载
)
timeout /t 2 >nul

:: 3. 检查状态
echo [3/3] 检查服务状态...
echo.
curl -s http://localhost:8123/health >nul 2>&1
if %errorlevel% == 0 (
    echo ✅ GAIRR.Server 本地运行正常
) else (
    echo ❌ GAIRR.Server 未响应
)

echo.
echo === 服务状态 ===
echo GAIRR.Server: http://localhost:8123
echo frpc 管理:   http://localhost:7400
echo.
echo 公网访问:    http://8.136.112.142:8123 (通过 frp)
echo.
pause
