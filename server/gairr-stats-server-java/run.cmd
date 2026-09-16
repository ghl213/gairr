@echo off
rem gairr-stats-server-java 启动脚本：java -jar（Ctrl+C 优雅退出）
setlocal
cd /d %~dp0
java -jar build\gairr-stats-server.jar %*
endlocal
