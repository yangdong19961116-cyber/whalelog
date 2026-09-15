# WhaleLog 鲸记 — PowerShell 构建脚本
# 用法：  .\build-whalelog.ps1

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Step($msg) {
    Write-Host ""
    Write-Host "==> " -NoNewline -ForegroundColor Cyan
    Write-Host $msg -ForegroundColor White
}

Push-Location $root
try {
    Step "1/3 还原 NuGet 包"
    dotnet restore WhaleLog.sln

    Step "2/3 编译 Release"
    dotnet build WhaleLog.sln -c Release

    Step "3/3 发布自包含单文件"
    dotnet publish src/WhaleLog/WhaleLog.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=embedded `
        -o Publish

    Write-Host ""
    Write-Host "完成。单文件程序在：$root\Publish\WhaleLog.exe" -ForegroundColor Green
}
finally {
    Pop-Location
}
