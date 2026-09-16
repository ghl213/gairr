@echo off
rem gairr-stats-server-java 一键验证：build.cmd 编译打包 + test\Smoke.java 冒烟（启动服务打端点后自动退出）
rem 本工程是纯 Java（javac/jar），与仓库根目录的 dotnet build 无关：根目录无 .sln，dotnet build 会报 MSB1003。
setlocal
cd /d %~dp0

call build.cmd
if errorlevel 1 (echo VERIFY FAILED: build & exit /b 1)

echo.
echo [smoke] java test\Smoke.java build\gairr-stats-server.jar
java test\Smoke.java build\gairr-stats-server.jar
if errorlevel 1 (echo VERIFY FAILED: smoke & exit /b 1)

echo VERIFY OK
endlocal
