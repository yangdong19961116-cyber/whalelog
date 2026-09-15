@echo off
chcp 65001 >nul
REM ============================================================
REM  WhaleLog 鲸记 — 离线构建脚本
REM
REM  什么时候需要它：
REM    `dotnet restore` 报 "Value cannot be null. (Parameter 'path1')"。
REM    这是 NuGet 在受限环境里拿不到机器级目录（CommonApplicationData）导致的，
REM    和项目代码无关。鲸记不依赖任何 NuGet 包，所以可以手写 restore 产物绕过。
REM
REM  这条路径会额外做两件事：
REM    1. 手工补出框架依赖的 WhaleLog.exe（GUI 子系统 + 鲸鱼图标）
REM       —— 直接 `dotnet WhaleLog.dll` 会弹一个黑控制台窗口
REM    2. 在桌面创建快捷方式
REM
REM  产物：src\WhaleLog\bin\Release\net8.0-windows\WhaleLog.exe
REM        需要目标机器装 .NET 8 Desktop Runtime
REM ============================================================

setlocal
pushd "%~dp0"

set PY=
where python >nul 2>nul && set PY=python
if not defined PY where py >nul 2>nul && set PY=py
if not defined PY if exist "%USERPROFILE%\.workbuddy\binaries\python\versions\3.13.12\python.exe" set PY="%USERPROFILE%\.workbuddy\binaries\python\versions\3.13.12\python.exe"

if not defined PY (
    echo   找不到 Python，本脚本需要它来生成 restore 产物和 apphost。
    echo   换一台能正常执行 dotnet restore 的机器用 build-whalelog.cmd 即可。
    goto :fail
)

echo [1/4] 生成离线 restore 产物...
%PY% tools\offline_restore.py
if errorlevel 1 goto :fail

echo [2/4] 编译 Release（跳过 NuGet 资产解析）...
dotnet build src\WhaleLog\WhaleLog.csproj -c Release ^
    --no-restore ^
    -p:SkipResolvePackageAssets=true ^
    -p:GenerateDependencyFile=false ^
    -p:GenerateRuntimeConfigurationFiles=false
if errorlevel 1 goto :fail

echo [3/4] 补运行时清单并生成 apphost...
%PY% tools\offline_restore.py
if errorlevel 1 goto :fail
%PY% tools\make_apphost.py
if errorlevel 1 goto :fail

echo [4/4] 创建桌面快捷方式...
src\WhaleLog\bin\Release\net8.0-windows\WhaleLog.exe --create-shortcut --quiet

echo.
echo  完成。程序在：%CD%\src\WhaleLog\bin\Release\net8.0-windows\WhaleLog.exe
echo  桌面上应该多了一个「鲸记 WhaleLog」。
popd
endlocal
exit /b 0

:fail
popd
endlocal
echo.
echo  *** 构建失败，请查看上面的错误信息。***
exit /b 1
