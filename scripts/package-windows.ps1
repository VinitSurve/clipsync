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

Push-Location $WindowsDir
try {
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $DistDir
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
