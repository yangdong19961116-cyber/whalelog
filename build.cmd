@echo off
REM ============================================================
REM AudioRoute — quick build helper (Windows 10 / 11)
REM Run from a "x64 Native Tools Command Prompt for VS 2022"
REM or from any shell that has `dotnet` on PATH.
REM ============================================================

setlocal
pushd "%~dp0"

echo [1/3] Restoring NuGet packages...
dotnet restore AudioRoute.sln
if errorlevel 1 goto :fail

echo [2/3] Building Release...
dotnet build AudioRoute.sln -c Release -p:Platform=x64
if errorlevel 1 goto :fail

echo [3/3] Publishing self-contained single file...
dotnet publish src\AudioRoute\AudioRoute.csproj -c Release -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=embedded ^
    -o Publish
if errorlevel 1 goto :fail

echo.
echo  Done. Single-file binary at: %CD%\Publish\AudioRoute.exe
popd
endlocal
exit /b 0

:fail
popd
endlocal
echo.
echo  *** Build failed. See messages above. ***
exit /b 1
