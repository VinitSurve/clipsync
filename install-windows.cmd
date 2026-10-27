@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"
title ClipSync Windows Installer

echo ==========================================================================
echo  ClipSync for Windows - One-Click Setup
echo ==========================================================================
echo.
echo Installing ClipSync to your user programs folder, creating Start Menu
echo and Desktop shortcuts, and enabling launch at Windows login...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-windows.ps1"
set "PS_EXIT=%ERRORLEVEL%"

if %PS_EXIT% neq 0 (
    echo.
    echo ==========================================================================
    echo [ERROR] Installation failed with exit code %PS_EXIT%.
    echo ==========================================================================
    echo Please review the error message above.
    pause
    exit /b %PS_EXIT%
)

echo.
echo ==========================================================================
echo Setup completed successfully! You can close this window.
echo ==========================================================================
pause
