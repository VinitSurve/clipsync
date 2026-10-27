@echo off
setlocal
cd /d "%~dp0"
title ClipSync Windows Installer

echo ==========================================================================
echo  ClipSync for Windows — One-Click Setup
echo ==========================================================================
echo.
echo Installing ClipSync to your user programs folder, creating Start Menu
echo and Desktop shortcuts, and enabling launch at Windows login...
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-windows.ps1"
if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Installation encountered an error.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Setup completed successfully! You can close this window.
timeout /t 5
