<#
.SYNOPSIS
    Packages ClipSync for Windows into a single self-contained executable.
#>
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$WindowsDir = Join-Path $RootDir "apps\windows"
$DistDir = Join-Path $RootDir "dist\windows"

Write-Host "==> Packaging ClipSync for Windows (Self-Contained Single-File)..." -ForegroundColor Cyan

if (Test-Path $DistDir) {
    Remove-Item -Recurse -Force $DistDir
}
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    Write-Error "The .NET 8.0 SDK was not found in PATH. Please install the .NET 8.0 SDK (https://dotnet.microsoft.com/download/dotnet/8.0) to package ClipSync."
    exit 1
}

Push-Location $WindowsDir
try {
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$DistDir"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}

$ExePath = Join-Path $DistDir "ClipSync.exe"
if (Test-Path $ExePath) {
    Write-Host "==> ClipSync Windows Application successfully packaged at:" -ForegroundColor Green
    Write-Host "    $ExePath" -ForegroundColor White
} else {
    Write-Error "Failed to find published ClipSync.exe at $ExePath"
}
