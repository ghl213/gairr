@echo off
rem gairr-stats-server-java build script: javac + jar (no Maven/Gradle)
setlocal
cd /d %~dp0

if not exist out mkdir out
if not exist build mkdir build

echo [1/2] javac compile src\*.java -^> out\
javac -encoding UTF-8 -d out src\*.java || (echo BUILD FAILED & exit /b 1)

echo [2/2] package build\gairr-stats-server.jar ^(Main-Class: Main^)
jar cfe build\gairr-stats-server.jar Main -C out .
if errorlevel 1 (echo PACK FAILED & exit /b 1)

echo BUILD OK: build\gairr-stats-server.jar
echo RUN: java -jar build\gairr-stats-server.jar [--port 8300] [--token xxx]
endlocal
