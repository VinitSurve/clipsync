<#
.SYNOPSIS
    Installs ClipSync Windows system tray application, sets up Start Menu and Desktop shortcuts,
    configures automatic startup at Windows login, and launches the application.
#>
param (
    [switch]$NoDesktopShortcut = $false,
    [switch]$NoLaunch = $false,
    [switch]$SkipBuild = $false
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$DistExe = Join-Path $RootDir "dist\windows\ClipSync.exe"

# By default, ALWAYS rebuild from source to guarantee the latest code is installed.
# Only skip if explicitly requested via -SkipBuild AND the dist executable already exists.
if (-not $SkipBuild) {
    Write-Host "==> Rebuilding ClipSync from source to ensure latest features..." -ForegroundColor Cyan
    & (Join-Path $ScriptDir "package-windows.ps1")
} elseif (-not (Test-Path $DistExe)) {
    Write-Host "==> Packaged binary not found. Building now..." -ForegroundColor Cyan
    & (Join-Path $ScriptDir "package-windows.ps1")
}

if (-not (Test-Path $DistExe)) {
    Write-Error "Could not find packaged ClipSync.exe at $DistExe. Please build the project first."
    exit 1
}

$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\ClipSync"
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

$TargetExe = Join-Path $InstallDir "ClipSync.exe"

# Stop any currently running ClipSync processes
Write-Host "==> Checking for running ClipSync processes..." -ForegroundColor Yellow
$running = Get-Process -Name "ClipSync" -ErrorAction SilentlyContinue
if ($running) {
    $count = @($running).Count
    Write-Host "    Found $count running ClipSync process(es). Terminating..." -ForegroundColor Yellow
    foreach ($p in $running) {
        try {
            Write-Host "    Stopping PID $($p.Id)..." -ForegroundColor Gray
            $p | Stop-Process -Force -ErrorAction SilentlyContinue
        } catch {}
    }
    
    # Wait for processes to exit and release file locks
    $waitCount = 0
    while ((Get-Process -Name "ClipSync" -ErrorAction SilentlyContinue) -and ($waitCount -lt 50)) {
        Start-Sleep -Milliseconds 100
        $waitCount++
    }
}

# Ensure destination file is not locked
if (Test-Path $TargetExe) {
    try {
        Remove-Item -Path $TargetExe -Force -ErrorAction Stop
    } catch {
        Write-Warning "Could not immediately remove $($TargetExe): $($_.Exception.Message)"
        Write-Host "Waiting 2 seconds and attempting taskkill..." -ForegroundColor Yellow
        Start-Sleep -Seconds 2
        taskkill /F /IM ClipSync.exe /T 2>$null
        Start-Sleep -Milliseconds 500
        Remove-Item -Path $TargetExe -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "==> Installing ClipSync to $TargetExe..." -ForegroundColor Cyan
Copy-Item -Path $DistExe -Destination $TargetExe -Force

# Verify installation
if (-not (Test-Path $TargetExe)) {
    Write-Error "Failed to install ClipSync.exe to $TargetExe."
    exit 1
}

$TargetItem = Get-Item $TargetExe
$TargetSizeMB = [math]::Round(($TargetItem.Length / 1MB), 1)
$TargetDate = $TargetItem.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
Write-Host "    Verified target: $TargetExe ($TargetSizeMB MB, modified: $TargetDate)" -ForegroundColor Green

# Create Start Menu shortcut
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
if (-not (Test-Path $StartMenuDir)) {
    New-Item -ItemType Directory -Path $StartMenuDir -Force | Out-Null
}

$WshShell = New-Object -ComObject WScript.Shell

$StartShortcut = $WshShell.CreateShortcut((Join-Path $StartMenuDir "ClipSync.lnk"))
$StartShortcut.TargetPath = $TargetExe
$StartShortcut.WorkingDirectory = $InstallDir
$StartShortcut.Description = "ClipSync - Universal Clipboard"
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
        $DesktopShortcut.Description = "ClipSync - Universal Clipboard"
        $DesktopShortcut.IconLocation = "$TargetExe,0"
        $DesktopShortcut.Save()
        Write-Host "==> Created Desktop shortcut at: $DesktopDir\ClipSync.lnk" -ForegroundColor Green
    }
}

# Configure Automatic Startup on Windows login (HKCU Run Registry Key)
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$RegistryValue = '"' + $TargetExe + '"'
Set-ItemProperty -Path $RunKey -Name "ClipSync" -Value $RegistryValue -Force
Write-Host "==> Configured auto-startup at Windows login (Registry: $RunKey\ClipSync)" -ForegroundColor Green

# Launch the app in the system tray
if (-not $NoLaunch) {
    Write-Host "==> Launching updated ClipSync in system tray..." -ForegroundColor Green
    Start-Process -FilePath $TargetExe
}

Write-Host ""
Write-Host "==========================================================================" -ForegroundColor Green
Write-Host "[OK] ClipSync Windows Installation Complete!" -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
Write-Host "- Application:      $TargetExe" -ForegroundColor White
Write-Host "- Size / Modified:  $TargetSizeMB MB ($TargetDate)" -ForegroundColor White
Write-Host "- Start Menu:       ClipSync" -ForegroundColor White
Write-Host "- Start at Login:   Enabled (silent background tray launch)" -ForegroundColor White
Write-Host "- Everyday Use:     Launch from Start Menu or Desktop. No commands required!" -ForegroundColor White
Write-Host "==========================================================================" -ForegroundColor Green
