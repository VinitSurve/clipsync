# ClipSync — Universal Cloud Clipboard

**ClipSync** is a private, cross-platform universal clipboard synchronization system designed for **macOS (MacBook Air M5)**, **Windows 10/11**, and **Android**.

Copy anything on one device and instantly paste it on another with zero-knowledge end-to-end encryption.

---

## 📱 Download Android Client

[![Download Latest Android APK](https://img.shields.io/badge/Download-ClipSync.apk-107C41?style=for-the-badge&logo=android&logoColor=white)](https://github.com/VinitSurve/clipsync/releases/latest/download/ClipSync.apk)

- 📥 **Direct Download Link**: **[Download Latest ClipSync.apk](https://github.com/VinitSurve/clipsync/releases/latest/download/ClipSync.apk)** *(Always points to the stable latest release)*
- 📦 **All Releases & Changelogs**: [GitHub Releases](https://github.com/VinitSurve/clipsync/releases)
- 🔒 **Integrity Checksum**: [ClipSync.apk.sha256](https://github.com/VinitSurve/clipsync/releases/latest/download/ClipSync.apk.sha256)

### 🚀 1-Click Phone Installation (No ADB or Computer Required)
You do **NOT** need USB debugging, Developer Options, wireless debugging, or ADB to install ClipSync on your phone:
1. **Download**: Open the download link above directly on your Android phone (e.g. in Chrome).
2. **Permission Prompt**: If Android displays *"For your security, your phone is not allowed to install unknown apps from this source"*:
   - Tap **Settings** on the prompt.
   - Toggle **Allow from this source** to **ON** for your browser / file manager.
   - Press Back and tap **Install** (or **Update**).
3. **Open & Pair**: Open ClipSync, tap **Pair Device**, and enter the 6-digit code shown on your Mac or Windows PC.

### 🛡️ Seamless Updates & Troubleshooting
- **In-Place Updates**: All official releases are signed with a persistent release key and use the same application ID (`com.clipsync.android`). Future updates install directly over existing versions without losing your paired devices or clipboard history.
- **"App not installed as package conflicts with an existing package"**: If you previously installed a debug test build via ADB or Android Studio, Android will block upgrading because debug and release signatures differ. Simply uninstall the old debug version once, then install the official `ClipSync.apk`.
- **Verify Checksum**:
  ```bash
  # macOS / Linux
  shasum -a 256 ClipSync.apk
  # Windows PowerShell
  Get-FileHash ClipSync.apk -Algorithm SHA256
  ```

---

## 🏗 System Architecture

```
┌──────────────────────────┐    ┌──────────────────────────┐    ┌──────────────────────────┐
│   macOS (Swift/SwiftUI)  │    │    Windows 10/11 (C#)    │    │      Android (Kotlin)    │
│    Native Menu Bar App   │    │  Native System Tray App  │    │  Foreground Service App  │
└────────────┬─────────────┘    └────────────┬─────────────┘    └────────────┬─────────────┘
             │                               │                               │
             │           WebSocket (Live Sync) + HTTPS (REST API)            │
             └───────────────────────────────┼───────────────────────────────┘
                                             ▼
                             ┌───────────────────────────────┐
                             │    ClipSync Backend Server    │
                             │     (Node.js + TypeScript)    │
                             └───────┬───────────────┬───────┘
                                     │               │
                                     ▼               ▼
                        ┌──────────────────┐  ┌──────────────────┐
                        │   PostgreSQL 16  │  │  S3 / MinIO      │
                        │ (Devices/Events) │  │(Encrypted Blobs) │
                        └──────────────────┘  └──────────────────┘
```

### Monorepo Structure

```
clipsync/
├── .github/
│   └── workflows/    # Automated CI/CD (Android APK build, signing & GitHub Releases)
├── apps/
│   ├── server/       # Node.js + TypeScript REST & WebSocket relay service
│   ├── macos/        # Native Swift & SwiftUI menu bar application
│   ├── windows/      # Native C# / .NET 8 system tray application
│   └── android/      # Native Kotlin Android companion app with Foreground Service
├── packages/
│   ├── protocol/     # Shared wire protocol, types, serialization & versioning
│   ├── crypto/       # AES-256-GCM authenticated encryption & PBKDF2 key derivation
│   └── sync-client/  # Universal client engine with loop prevention & reconnects
├── infrastructure/   # Docker Compose (PostgreSQL 16, MinIO S3, ClipSync Server)
├── scripts/          # Keystore generator & platform installation scripts
└── docs/             # Security model, architecture, and production deployment guides
```

---

## ⚡ Quickstart

### Prerequisites
- Node.js 20+
- pnpm (`npm install -g pnpm`)

### 1. Install & Build All Packages
```bash
pnpm install
pnpm run build
```

### 2. Run Integration Tests
```bash
pnpm test
```

### 3. Start the Backend Server
```bash
pnpm dev:server
```
The server will start at `http://localhost:8787` with WebSockets at `ws://localhost:8787/v1/events`.
*(By default, the server uses an embedded file store and local disk blob storage, so no external Postgres/S3 daemon is required for instant local testing!)*

---

## 💻 Running the Native Clients

### macOS Menu Bar App (MacBook Air)
Built with native Swift and SwiftUI:
```bash
cd apps/macos
swift build
./.build/debug/ClipSync
```
- Appears in your system menu bar with status icon.
- Monitors `NSPasteboard` with automatic echo loop suppression.
- Hardware-accelerated Apple `CryptoKit` AES-256-GCM.
- Excludes password managers (1Password, Bitwarden, Keychain).

### Windows System Tray App (Windows 10/11 PC)
Built with C# and .NET 8:
```cmd
cd apps/windows
dotnet run
```
- Runs quietly in system tray with quick-access menu and history list.
- Zero-lag copy detection via Win32 `AddClipboardFormatListener`.
- Hardware-accelerated `System.Security.Cryptography.AesGcm`.

### Android Companion App (Redmi / Android)
- **Install APK directly**: [Download Latest ClipSync.apk](https://github.com/VinitSurve/clipsync/releases/latest/download/ClipSync.apk)
- Or build locally from source:
  ```bash
  cd apps/android
  ./gradlew assembleDebug
  ```
- Features:
  - Uses an ongoing Foreground Service notification to maintain WebSocket connection.
  - Includes a **Quick Settings Tile** ("Sync Clipboard") for instant one-tap copy from inside any app.
  - QR Code scanner and 6-digit numeric pairing.
  - Hardware-backed `EncryptedSharedPreferences` via Android Keystore.

---

## 🔐 Security & Zero-Knowledge Guarantee

1. **Client-Side Encryption**: All clipboard text, code, images, and files are encrypted with **AES-256-GCM** (256-bit key, 96-bit random nonce, 128-bit authentication tag) before leaving the device.
2. **Master Key Derivation**: Keys are derived using **PBKDF2-HMAC-SHA256** with **100,000 rounds** and per-group cryptographic salts.
3. **Zero Plaintext at Rest or in Transit**: The cloud server never receives or logs master keys or plaintext content.
4. **Presigned Expiring URLs**: Large files and images upload directly to S3 with SHA-256 integrity verification.
5. **No Sync Loops**: Monotonic sequence numbers, device tracking, and local SHA-256 content hashes prevent endless echo feedback loops.

---

## 🚢 Production Deployment

Run the complete production stack (PostgreSQL + MinIO S3 + Server) via Docker Compose:

```bash
cd infrastructure
docker compose up -d
```

See [docs/deployment.md](file:///Users/vinit/Vinit/clipsync/docs/deployment.md) and [docs/security.md](file:///Users/vinit/Vinit/clipsync/docs/security.md) for full production configuration.
