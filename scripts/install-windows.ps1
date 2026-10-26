<#
.SYNOPSIS
    Installs ClipSync Windows system tray application, sets up auto-startup at login, and launches the app.
#>
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$DistExe = Join-Path $RootDir "dist\windows\ClipSync.exe"

# If not packaged yet, run package script first
if (-not (Test-Path $DistExe)) {
    & (Join-Path $ScriptDir "package-windows.ps1")
}

$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\ClipSync"
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

$TargetExe = Join-Path $InstallDir "ClipSync.exe"

Write-Host "==> Stopping any running instances of ClipSync..." -ForegroundColor Yellow
Get-Process -Name "ClipSync" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

Write-Host "==> Installing ClipSync to $TargetExe..." -ForegroundColor Cyan
Copy-Item -Path $DistExe -Destination $TargetExe -Force

# Create Start Menu shortcut
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut((Join-Path $StartMenuDir "ClipSync.lnk"))
$Shortcut.TargetPath = $TargetExe
$Shortcut.WorkingDirectory = $InstallDir
$Shortcut.Description = "ClipSync — Universal Clipboard"
$Shortcut.Save()

# Configure Automatic Startup on Windows login (Registry)
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $RunKey -Name "ClipSync" -Value "`"$TargetExe`"" -Force
Write-Host "==> Configured auto-startup at Windows login (Registry: $RunKey\ClipSync)" -ForegroundColor Green

# Launch the app in the system tray
Write-Host "==> Launching ClipSync in system tray..." -ForegroundColor Green
Start-Process -FilePath $TargetExe

Write-Host ""
Write-Host "==> ClipSync installation complete!" -ForegroundColor Green
Write-Host "    ClipSync is now running in your system tray and will automatically start when Windows turns on or logs in." -ForegroundColor White
