@echo off
chcp 65001 >nul
:: ============================================
:: GAIRR 工作电脑 - SSH 反向隧道启动脚本
:: 无需安装 frp，使用 SSH 反向隧道
:: ============================================

set GAIRR_DIR=d:\work\gairr
set SERVER=root@8.136.112.142

echo === GAIRR SSH 隧道启动 ===
echo.

:: 1. 启动 GAIRR.Server
echo [1/2] 启动 GAIRR.Server...
start "GAIRR Server" cmd /c "cd /d %GAIRR_DIR%\app\GAIRR.Server\bin\Debug\net8.0-windows && gairr-agent-server.exe --host 0.0.0.0 --port 8123"
timeout /t 3 >nul

:: 2. 建立 SSH 反向隧道
echo [2/2] 建立 SSH 反向隧道...
echo 命令: ssh -R 8123:localhost:8123 %SERVER% -N
echo.
echo ⚠️  请保持此窗口运行，隧道才能保持连接
echo.

ssh -R 8123:localhost:8123 -o ServerAliveInterval=60 -o ServerAliveCountMax=3 %SERVER% -N

:: 如果 SSH 断开，提示用户
echo.
echo ❌ SSH 隧道已断开
echo 按任意键重新连接...
pause >nul
goto :eof
