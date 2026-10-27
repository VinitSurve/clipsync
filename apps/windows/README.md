# ClipSync — Windows System Tray Client

Native C# / .NET 8 system tray application providing universal end-to-end encrypted clipboard synchronization for Windows 10/11.

## Features

- **System Tray Integration**: Quietly runs in background with tray icon, context menu, and balloon notifications.
- **Graphical Everyday Launch**: Zero command-line or PowerShell required for normal use. Starts automatically at login or from the Windows Start Menu / Desktop shortcut.
- **Native Settings Window**:
  - Live connection status indicator (Connected, Connecting, Disconnected, Paused) with diagnostic details.
  - Quick server presets for Cloudflare Worker (`https://clipsync-server-cloudflare.survevinit56.workers.dev`) and Localhost (`http://localhost:8787`).
  - Master sync passphrase configuration with visibility toggle.
  - Device label management and registered sync group device list.
  - Windows startup toggle with live registry verification.
  - Clear Google Drive Phase 2C status indicator.
  - Reconnect and pause/resume controls.
- **Automatic Reconnection**: Automatically reconnects when resuming from sleep (`PowerModes.Resume`) and when network interfaces or routes change (`NetworkChange`).
- **Win32 Clipboard Notifications**: Uses native `AddClipboardFormatListener` and `WM_CLIPBOARDUPDATE` for zero-polling instant copy detection.
- **End-to-End Encryption**: Hardware-accelerated AES-256-GCM authenticated encryption using `System.Security.Cryptography.AesGcm` and PBKDF2.
- **Pairing View**: Enter 6-digit numeric pairing code from MacBook Air or mobile device.
- **Clipboard History**: Clean double-click-to-copy history window.

## Requirements

- Windows 10 (version 1809+) or Windows 11 (64-bit)
- .NET 8.0 SDK (only needed once to package/build the single-file executable; the packaged build is self-contained)

## One-Click Setup (Perform Once)

Double-click `install-windows.cmd` in the repository root, or open PowerShell and run:

```powershell
.\scripts\install-windows.ps1
```

This automatically:
1. Builds a standalone self-contained executable at `dist\windows\ClipSync.exe`.
2. Installs the application to `%LOCALAPPDATA%\Programs\ClipSync\ClipSync.exe`.
3. Creates a **Start Menu** shortcut (`ClipSync`).
4. Creates a **Desktop** shortcut (`ClipSync`).
5. Configures automatic startup in Windows Registry (`HKCU\...\Run`).
6. Launches ClipSync silently in your system tray.

## Everyday Use

- **No commands required.**
- ClipSync starts automatically whenever you log in to Windows.
- You can also launch it at any time from your **Start Menu** or **Desktop shortcut**.
- Look for the ClipSync icon in your system tray (bottom-right next to the clock).
- Right-click the tray icon to open **Clipboard History**, access **Settings...**, trigger **Reconnect**, **Pause Sync**, or **Exit ClipSync**.
- Double-click the tray icon to quickly open Clipboard History.
