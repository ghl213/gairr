

@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
echo ==========================================
echo   GAIRR 编译打包发布脚本（含自检补齐）
echo ==========================================
echo.

:: [1/6] 关闭正在运行的 GAIRR.exe（不关闭会占用文件，导致发布中止）
taskkill /F /IM GAIRR.exe 2>nul
if %errorlevel% equ 0 (
    echo [1/6] 已关闭正在运行的 GAIRR.exe
) else (
    echo [1/6] GAIRR.exe 未运行，跳过
)
timeout /t 2 /nobreak >nul

:: [2/6] 设置 .NET SDK 路径并检查
set "DOTNET_ROOT=%USERPROFILE%\AppData\Local\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [错误] 未找到 dotnet 命令，请确认 .NET SDK 已安装。
    pause
    exit /b 1
)
echo [2/6] 使用 .NET SDK:
dotnet --version

:: [3/6] 发布前快照：备份 publish 的自定义/运行文件（用于出问题时回滚）
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set TS=%%i
set "BK=%~dp0back\pub_pre_%TS%"
mkdir "%BK%" >nul 2>&1
if exist "%~dp0publish\config.ini"      copy /y "%~dp0publish\config.ini" "%BK%\" >nul
if exist "%~dp0publish\system.ini"      copy /y "%~dp0publish\system.ini" "%BK%\" >nul
if exist "%~dp0publish\plugins"         xcopy /e /i /y "%~dp0publish\plugins" "%BK%\plugins\"  >nul
if exist "%~dp0publish\prompts"         xcopy /e /i /y "%~dp0publish\prompts" "%BK%\prompts\"  >nul
if exist "%~dp0publish\skills"          xcopy /e /i /y "%~dp0publish\skills"  "%BK%\skills\"   >nul
if exist "%~dp0publish\market"          xcopy /e /i /y "%~dp0publish\market"  "%BK%\market\"   >nul
if exist "%~dp0publish\data"            xcopy /e /i /y "%~dp0publish\data"    "%BK%\data\"     >nul
if exist "%~dp0publish\session.json"    copy /y "%~dp0publish\session.json"   "%BK%\"          >nul
echo [3/6] 发布前快照完成: %BK%



:: [4/6] 编译发布（单文件）
echo [4/6] 开始编译发布...
dotnet publish "%~dp0app\GAIRR\GAIRR.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%~dp0publish"
if %errorlevel% neq 0 (
    echo.
    echo [错误] 发布失败！publish 目录可能不完整。
    echo        可对照发布前快照检查: %BK%
    pause
    exit /b 1
)

:: ============================================================
:: [4b/6] 发布 gairr-cli（headless 命令行宿主，供脚本/计划任务/无头验证）
:: framework-dependent 版（需 .NET 8 运行时），体积小；产物在 publish\cli
:: ============================================================
echo [4b/6] 编译发布 gairr-cli...
dotnet publish "%~dp0app\GAIRR.Cli\GAIRR.Cli.csproj" -c Release -o "%~dp0publish\cli"
if %errorlevel% neq 0 (
    echo     [警告] gairr-cli 发布失败（不影响 GAIRR.exe）
) else (
    :: CLI 配置跟随 GUI 发布目录的同一份（用户在 publish 的修改优先，覆盖 csproj 从源复制的默认版）
    if exist "%~dp0publish\config.ini" copy /y "%~dp0publish\config.ini" "%~dp0publish\cli\config.ini" >nul
    if exist "%~dp0publish\system.ini" copy /y "%~dp0publish\system.ini" "%~dp0publish\cli\system.ini" >nul
    if exist "%~dp0publish\prompts" xcopy /e /i /y "%~dp0publish\prompts" "%~dp0publish\cli\prompts\" >nul
    if exist "%~dp0publish\skills"  xcopy /e /i /y "%~dp0publish\skills"  "%~dp0publish\cli\skills\"  >nul
    echo     OK  gairr-cli.exe 发布成功: %~dp0publish\cli\gairr-cli.exe
)

:: ============================================================
:: [5/6] 发布后自检与补齐：缺什么补什么，补不上才报警
:: 补齐源顺序：构建中间目录(同批产物) -^> publish2 -^> 报警
:: ============================================================
echo [5/6] 自检并补齐发布产物...
set NEED_OK=1
set "MID=%~dp0app\GAIRR\bin\Release\net8.0-windows\win-x64"
set "P2=%~dp0publish2"

:: 外置原生 dll（单文件发布的必备外置件）
for %%f in (e_sqlite3.dll D3DCompiler_47_cor3.dll PresentationNative_cor3.dll wpfgfx_cor3.dll vcruntime140_cor3.dll PenImc_cor3.dll) do (
    if exist "%~dp0publish\%%f" (
        echo     OK  %%f
    ) else if exist "%MID%\%%f" (
        copy /y "%MID%\%%f" "%~dp0publish\%%f" >nul
        if errorlevel 1 ( set NEED_OK=0 & echo     [警告] 复制失败: %%f ) else ( echo     补齐 %%f ^(构建中间目录^) )
    ) else if exist "%P2%\%%f" (
        copy /y "%P2%\%%f" "%~dp0publish\%%f" >nul
        if errorlevel 1 ( set NEED_OK=0 & echo     [警告] 复制失败: %%f ) else ( echo     补齐 %%f ^(publish2^) )
    ) else (
        set NEED_OK=0
        echo     [警告] 缺失且无补齐源: %%f
    )
)

:: runtimes 目录
if exist "%~dp0publish\runtimes" (
    echo     OK  runtimes\
) else if exist "%MID%\runtimes" (
    xcopy /e /i /y "%MID%\runtimes" "%~dp0publish\runtimes\" >nul
    if errorlevel 1 ( set NEED_OK=0 & echo     [警告] runtimes 复制失败 ) else ( echo     补齐 runtimes\ ^(构建中间目录^) )
) else if exist "%P2%\runtimes" (
    xcopy /e /i /y "%P2%\runtimes" "%~dp0publish\runtimes\" >nul
    if errorlevel 1 ( set NEED_OK=0 & echo     [警告] runtimes 复制失败 ) else ( echo     补齐 runtimes\ ^(publish2^) )
) else (
    set NEED_OK=0
    echo     [警告] 缺失且无补齐源: runtimes\
)

:: GAIRR.exe（本次发布刚生成；若发布异常缺失则从 publish2 补）
if not exist "%~dp0publish\GAIRR.exe" (
    if exist "%P2%\GAIRR.exe" (
        copy /y "%P2%\GAIRR.exe" "%~dp0publish\GAIRR.exe" >nul
        if errorlevel 1 ( set NEED_OK=0 & echo     [警告] GAIRR.exe 复制失败 ) else ( echo     补齐 GAIRR.exe ^(publish2^) )
    ) else (
        set NEED_OK=0
        echo     [警告] GAIRR.exe 缺失且无补齐源
    )
)

:: deps / runtimeconfig（单文件发布的配套清单）
for %%f in (GAIRR.deps.json GAIRR.runtimeconfig.json) do (
    if exist "%~dp0publish\%%f" (
        echo     OK  %%f
    ) else if exist "%MID%\%%f" (
        copy /y "%MID%\%%f" "%~dp0publish\%%f" >nul
        if errorlevel 1 ( set NEED_OK=0 & echo     [警告] 复制失败: %%f ) else ( echo     补齐 %%f ^(构建中间目录^) )
    ) else if exist "%P2%\%%f" (
        copy /y "%P2%\%%f" "%~dp0publish\%%f" >nul
        if errorlevel 1 ( set NEED_OK=0 & echo     [警告] 复制失败: %%f ) else ( echo     补齐 %%f ^(publish2^) )
    ) else (
        set NEED_OK=0
        echo     [警告] %%f 缺失且无补齐源
    )
)

:: 自定义文件：config.ini 按修改时间双向同步（哪边新用哪边，防止覆盖用户运行时修改）
:: publish 较新 → 用户修改版，保留并回写源；否则源覆盖 publish；复制后两端 mtime 对齐避免下次误判
for /f "delims=" %%R in ('powershell -NoProfile -Command "& { $s=Get-Item -LiteralPath '%~dp0app\GAIRR\config.ini'; $p=Get-Item -LiteralPath '%~dp0publish\config.ini' -ErrorAction SilentlyContinue; if(-not $p){ Copy-Item -LiteralPath $s.FullName -Destination '%~dp0publish\config.ini'; 'config.ini 全新部署' } elseif($p.LastWriteTime -gt $s.LastWriteTime){ Copy-Item -LiteralPath $p.FullName -Destination $s.FullName -Force; (Get-Item -LiteralPath $s.FullName).LastWriteTime = $p.LastWriteTime; '保留 publish 修改版并回写源（publish 较新）' } else { Copy-Item -LiteralPath $s.FullName -Destination $p.FullName -Force; (Get-Item -LiteralPath $p.FullName).LastWriteTime = $s.LastWriteTime; '同步 config.ini（源最新）' } }"') do set SYNC_MSG=%%R
if errorlevel 1 (
    set NEED_OK=0
    echo     [警告] config.ini 同步失败
) else (
    echo     !SYNC_MSG!
)
if not exist "%~dp0publish\config.ini" (
    set NEED_OK=0
    echo     [警告] config.ini 缺失（源目录也没有）
)
if exist "%~dp0app\GAIRR\plugins\*" (
    xcopy /e /i /y "%~dp0app\GAIRR\plugins\*" "%~dp0publish\plugins\" >nul
    if errorlevel 1 ( echo     [警告] plugins 同步失败 ) else ( echo     同步 plugins\ ^(源最新^) )
)
if exist "%~dp0app\GAIRR\skills\*" (
    xcopy /e /i /y "%~dp0app\GAIRR\skills\*" "%~dp0publish\skills\" >nul
    if errorlevel 1 ( echo     [警告] skills 同步失败 ) else ( echo     同步 skills\  ^(源最新^) )
)
if exist "%~dp0app\GAIRR\market\*" (
    xcopy /e /i /y "%~dp0app\GAIRR\market\*" "%~dp0publish\market\" >nul
    if errorlevel 1 ( echo     [警告] market 同步失败 ) else ( echo     同步 market\  ^(源最新^) )
)

:: 定制运行文件：prompts\agent-deep.md / system.ini 缺失时从 publish2 补（保留定制内容，程序会生成默认模板）
if not exist "%~dp0publish\prompts\agent-deep.md" (
    if exist "%P2%\prompts\agent-deep.md" (
        xcopy /e /i /y "%P2%\prompts" "%~dp0publish\prompts\" >nul
        if errorlevel 1 ( echo     [警告] prompts 复制失败 ) else ( echo     补齐 prompts\agent-deep.md ^(publish2^) )
    ) else (
        echo     [提示] prompts\agent-deep.md 缺失，程序将自动生成默认模板
    )
)
if not exist "%~dp0publish\system.ini" (
    if exist "%P2%\system.ini" (
        copy /y "%P2%\system.ini" "%~dp0publish\system.ini" >nul
        if errorlevel 1 ( echo     [警告] system.ini 复制失败 ) else ( echo     补齐 system.ini ^(publish2^) )
    ) else (
        echo     [提示] system.ini 缺失，程序将自动生成默认模板
    )
)

:: [6/6] 汇总结果
echo.
echo [6/6] 校验结果:
if %NEED_OK% equ 1 (
    echo     全部必需文件齐备，发布成功！
) else (
    echo     [警告] 仍有必需文件缺失（见上方 [警告] 行）！
    echo     发布目录: %~dp0publish
    echo     对照快照恢复: %BK%
)
echo     输出目录: %~dp0publish
echo     可执行文件: %~dp0publish\GAIRR.exe
echo     命令行宿主: %~dp0publish\cli\gairr-cli.exe  （用法: gairr-cli -p ^<项目根^> --ask ^"任务文本^"）
echo.

echo 正在启动 GAIRR...
start "" "%~dp0publish\GAIRR.exe"