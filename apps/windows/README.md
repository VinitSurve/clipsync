# ClipSync — Windows System Tray Client

Native C# / .NET 8 system tray application providing universal clipboard synchronization for Windows 10/11.

## Features

- **System Tray Integration**: Quietly runs in background with tray icon and context menu.
- **Win32 Clipboard Notifications**: Uses `AddClipboardFormatListener` and `WM_CLIPBOARDUPDATE` for zero-lag copy detection.
- **End-to-End Encryption**: Hardware-accelerated AES-256-GCM authenticated encryption using `System.Security.Cryptography.AesGcm`.
- **Sync Loop & Echo Prevention**: Prevents duplicate broadcast loops when remote clips are received.
- **Pairing View**: Enter 6-digit numeric pairing code from MacBook Air or Android phone.
- **Clipboard History**: Clean double-click-to-copy history window.
- **Pause/Resume**: Toggle sync on demand.

## Requirements

- Windows 10 (version 1809+) or Windows 11
- .NET 8.0 SDK / Runtime

## How to Run

1. Open a terminal in `apps/windows`:
   ```cmd
   dotnet run
   ```

2. To build a standalone, self-contained single-file executable for Windows:
   ```cmd
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
   ```
   The generated `.exe` will be located in `bin/Release/net8.0-windows/win-x64/publish/ClipSync.exe`.
