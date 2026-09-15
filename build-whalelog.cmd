@echo off
chcp 65001 >nul
REM ============================================================
REM  WhaleLog 鲸记 — 构建脚本（Windows 10 / 11）
REM
REM  正常环境三步：还原 → 编译 → 发布自包含单文件，
REM  最后顺手在桌面放一个快捷方式。
REM
REM  产物：Publish\WhaleLog.exe（自包含，目标机器不需要装 .NET）
REM
REM  如果卡在 restore 报 "Value cannot be null. (Parameter 'path1')"，
REM  说明当前 shell 环境缺少 NuGet 需要的机器级目录信息，
REM  改用 build-whalelog-offline.cmd。
REM ============================================================

setlocal
pushd "%~dp0"

echo [1/4] 还原 NuGet 包...
dotnet restore WhaleLog.sln
if errorlevel 1 goto :fail

echo [2/4] 编译 Release...
dotnet build WhaleLog.sln -c Release
if errorlevel 1 goto :fail

echo [3/4] 发布自包含单文件...
dotnet publish src\WhaleLog\WhaleLog.csproj -c Release -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=embedded ^
    -o Publish
if errorlevel 1 goto :fail

echo [4/4] 创建桌面快捷方式...
REM 交给程序自己创建：快捷方式里的中文名字在这里手写容易踩批处理的编码坑
Publish\WhaleLog.exe --create-shortcut --quiet
if errorlevel 1 (
    echo   快捷方式创建失败，可以稍后在托盘菜单里手动创建。
)

echo.
echo  完成。单文件程序在：%CD%\Publish\WhaleLog.exe
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
