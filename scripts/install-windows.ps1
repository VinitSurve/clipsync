<#
.SYNOPSIS
    Installs ClipSync Windows system tray application, sets up Start Menu & Desktop shortcuts,
    configures automatic startup at Windows login, and launches the application.
#>
param (
    [switch]$NoDesktopShortcut = $false,
    [switch]$NoLaunch = $false
)

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
Start-Sleep -Milliseconds 600

Write-Host "==> Installing ClipSync to $TargetExe..." -ForegroundColor Cyan
Copy-Item -Path $DistExe -Destination $TargetExe -Force

# Create Start Menu shortcut
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$WshShell = New-Object -ComObject WScript.Shell

$StartShortcut = $WshShell.CreateShortcut((Join-Path $StartMenuDir "ClipSync.lnk"))
$StartShortcut.TargetPath = $TargetExe
$StartShortcut.WorkingDirectory = $InstallDir
$StartShortcut.Description = "ClipSync — Universal Clipboard"
$StartShortcut.IconLocation = "$TargetExe,0"
$StartShortcut.Save()
Write-Host "==> Created Start Menu shortcut at: $StartMenuDir\ClipSync.lnk" -ForegroundColor Green

# Create Desktop shortcut (unless -NoDesktopShortcut is specified)
if (-not $NoDesktopShortcut) {
    $DesktopDir = [Environment]::GetFolderPath("Desktop")
    if (Test-Path $DesktopDir) {
        $DesktopShortcut = $WshShell.CreateShortcut((Join-Path $DesktopDir "ClipSync.lnk"))
        $DesktopShortcut.TargetPath = $TargetExe
        $DesktopShortcut.WorkingDirectory = $InstallDir
        $DesktopShortcut.Description = "ClipSync — Universal Clipboard"
        $DesktopShortcut.IconLocation = "$TargetExe,0"
        $DesktopShortcut.Save()
        Write-Host "==> Created Desktop shortcut at: $DesktopDir\ClipSync.lnk" -ForegroundColor Green
    }
}

# Configure Automatic Startup on Windows login (HKCU Run Registry Key)
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $RunKey -Name "ClipSync" -Value "`"$TargetExe`"" -Force
Write-Host "==> Configured auto-startup at Windows login (Registry: $RunKey\ClipSync)" -ForegroundColor Green

# Launch the app in the system tray
if (-not $NoLaunch) {
    Write-Host "==> Launching ClipSync in system tray..." -ForegroundColor Green
    Start-Process -FilePath $TargetExe
}

Write-Host ""
Write-Host "==========================================================================" -ForegroundColor Green
Write-Host "🎉 ClipSync Windows Installation Complete!" -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
Write-Host "• Application:      $TargetExe" -ForegroundColor White
Write-Host "• Start Menu:       ClipSync" -ForegroundColor White
Write-Host "• Start at Login:   Enabled (silent background tray launch)" -ForegroundColor White
Write-Host "• Everyday Use:     Launch from Start Menu or Desktop. No commands required!" -ForegroundColor White
Write-Host "==========================================================================" -ForegroundColor Green
