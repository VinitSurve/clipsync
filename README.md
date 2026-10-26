# ClipSync — Universal Cloud Clipboard

**ClipSync** is a private, cross-platform universal clipboard synchronization system designed for **macOS (MacBook Air M5)**, **Windows 10/11**, and **Android**.

Copy anything on one device and instantly paste it on another with zero-knowledge end-to-end encryption.

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

### Android Companion App
Built with Kotlin and Android SDK:
```bash
cd apps/android
./gradlew assembleDebug
```
- Uses an ongoing Foreground Service notification to maintain WebSocket connection.
- Includes a **Quick Settings Tile** ("Sync Clipboard") for instant one-tap copy from inside any app.
- QR Code scanner and 6-digit numeric pairing.

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
