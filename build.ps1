# AudioRoute — PowerShell build helper
# Run from PowerShell:  .\build.ps1

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Step($msg) {
    Write-Host ""
    Write-Host "==> " -NoNewline -ForegroundColor Cyan
    Write-Host $msg -ForegroundColor White
}

Push-Location $root
try {
    Step "1/3 Restore packages"
    dotnet restore AudioRoute.sln

    Step "2/3 Build (Release, x64)"
    dotnet build AudioRoute.sln -c Release -p:Platform=x64

    Step "3/3 Publish self-contained single file"
    dotnet publish src/AudioRoute/AudioRoute.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=embedded `
        -o Publish

    Write-Host ""
    Write-Host "Done. Single-file binary at: $($root)\Publish\AudioRoute.exe" -ForegroundColor Green
}
finally {
    Pop-Location
}
