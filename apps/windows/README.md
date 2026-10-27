# ClipSync - Windows System Tray Client

Native C# / .NET 8 system tray application providing universal end-to-end encrypted clipboard synchronization for Windows 10/11.

## Features

- **System Tray Integration**: Quietly runs in background with tray icon, context menu, and non-intrusive balloon notifications.
- **Graphical Everyday Launch**: Zero command-line or PowerShell required for normal use. Starts automatically at login or from the Windows Start Menu / Desktop shortcut.
- **Phase 2C Image & File Clipboard Synchronization**:
  - **Images**: Automatically detects copied screenshots, browser images, Paint/Photos images (PNG/DIB/Bitmap) and syncs them to macOS. Received images are written directly to the Windows clipboard.
  - **Files & Folders**: Detects files copied in File Explorer (`CF_HDROP`). Single files are synced directly; multiple files or directories are cleanly packaged into zip archives preserving folder hierarchies.
  - **Downloads Integration**: Received files are automatically saved to `%USERPROFILE%\Downloads\ClipSync\` (with filename collision prevention) and set on the clipboard as a file drop list.
  - **End-to-End Encryption**: Encrypted locally with AES-256-GCM before upload. Zero plaintext or keys ever reach Google Drive or Cloudflare.
  - **Temporary Storage Lifecycle**: Google Drive files are strictly temporary (up to 100 MB per item, maximum 24-hour retention ceiling). Files are automatically purged immediately upon delivery acknowledgement or expiration.
- **Google Drive OAuth & Settings Management**:
  - Connect/Disconnect Google Drive directly from Settings.
  - Public-client OAuth 2.0 PKCE flow via local loopback (`http://127.0.0.1:<port>/oauth2redirect`).
  - Supports sharing the exact same Google Cloud "Desktop app" client ID used on macOS.
  - One-click "Import JSON..." button to load credentials from Google's `credentials.json`.
  - Secure credential storage backed by Windows DPAPI (`ProtectedData`).
- **Live Transfers Tab in Settings**:
  - Real-time progress tracking for uploads and downloads.
  - Retry and Cancel controls for individual transfers.
  - Quick "Open Downloads" button to navigate to received files in File Explorer.
- **Automatic Reconnection**: Automatically reconnects when resuming from sleep (`PowerModes.Resume`) and when network interfaces or routes change (`NetworkChange`). Waking from sleep automatically pulls any pending transfers.
- **Win32 Clipboard Notifications**: Uses native `AddClipboardFormatListener` and `WM_CLIPBOARDUPDATE` for zero-polling instant copy detection with echo-loop prevention.
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

## Google Drive Setup for Image & File Sync

1. In ClipSync Settings -> **Google Drive & System** tab:
   - Paste your Google OAuth **Client ID** (Desktop App type).
   - If you downloaded `credentials.json` from Google Cloud Console, click **Import JSON...** and select it.
2. Click **Connect Google Drive**.
3. Your default web browser will open to Google's sign-in page. Sign in and grant permission for the `drive.file` scope (ClipSync only has access to files it creates itself).
4. The browser will display *"ClipSync Authorized!"* and the Settings UI will update to show `[Connected] Google Account: <your-email>`.

## Everyday Use

- **No commands required.**
- ClipSync starts automatically whenever you log in to Windows.
- You can also launch it at any time from your **Start Menu** or **Desktop shortcut**.
- Look for the ClipSync icon in your system tray (bottom-right next to the clock).
- Right-click the tray icon to open **Clipboard History**, access **Settings...**, trigger **Reconnect**, **Pause Sync**, or **Exit ClipSync**.
