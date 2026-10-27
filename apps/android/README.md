# ClipSync — Android Native Client

Native Kotlin Android client for ClipSync universal clipboard synchronization across macOS, Windows 10, and Android (Redmi 10 Prime).

---

## Architecture & Implementation Overview

### Phase 1: Native Android Foundation
- **Modern Material 3 Interface**:
  - Live connection status card with color-coded indicator (Connected, Connecting, Disconnected, Not Paired).
  - Quick action buttons ("Sync Clipboard", "Pair Device").
  - Paired Devices card with device labels, platforms, last active timestamps, and device revocation.
  - Full Clipboard History card with source badges (e.g. `MacBook Air`, `Windows 10 PC`), timestamps, and 1-tap "Copy" buttons.
  - In-app Settings dialog with server URL configuration, sync passphrase (PBKDF2), and device name.
- **Hardware-Backed Keystore Storage**:
  - Uses `androidx.security.crypto.EncryptedSharedPreferences` backed by the Android Keystore.
  - Protects `deviceId`, auth `token`, `syncSalt`, `syncPassphrase`, and `serverUrl`.
- **Resilient Connection Handling**:
  - Connects to the Cloudflare Worker WebSocket endpoint (`/v1/events?token=...`).
  - Ping heartbeat interval (20s) and exponential backoff reconnection (2s to 30s).
  - Clean lifecycle handling via `ClipSyncForegroundService`.

### Phase 2: Text Clipboard Synchronization & Android 10+ Handling
- **Android Background Clipboard Reality**:
  - Android 10 (API 29)+ strictly prohibits background services from directly reading `ClipboardManager.getPrimaryClip()` unless the app currently holds window focus or is the active Input Method Editor (IME).
  - ClipSync handles this honestly and efficiently through three mechanisms:
    1. **Active Foreground Sync**: While `MainActivity` is visible, an active `OnPrimaryClipChangedListener` captures copies and syncs them instantly.
    2. **Quick Settings Tile (`ClipSyncTileService`)**: From any app on your phone, swipe down the quick settings drawer and tap **Sync Clipboard**. It launches a 0ms transparent trampoline activity (`ClipSyncCaptureActivity`) that obtains window focus, captures the clipboard, broadcasts to Mac/Windows, and closes within 50ms without disrupting your current app.
    3. **Actionable Notification**: When text arrives from Mac or Windows, the notification displays the text preview and provides a **Copy to Clipboard** action button that writes directly to the local clipboard via the trampoline activity if direct background write was restricted by the OS.
- **Loop & Echo Suppression**:
  - Tracks `seenItemIds` to ignore duplicated messages.
  - SHA-256 hash comparison against `lastSentHash` and `lastReceivedHash` prevents circular echo loops between Mac, Windows, and Android.

### Phase 3: Device Pairing & Security
- **6-Digit Pairing Flow**:
  - Supports claiming pairing codes generated on Mac or Windows (`POST /v1/auth/pair`).
  - Supports generating a 6-digit code on Android for entry into Mac/Windows (`POST /v1/auth/pairing/session`).
  - Deep-link support (`clipsync://pair?code=123456&server=...&salt=...`).
- **End-to-End Encryption**:
  - PBKDF2-HMAC-SHA256 (100,000 iterations, 32-byte key) identical to macOS and Windows.
  - AES-256-GCM authenticated encryption with 12-byte IV and 16-byte authentication tag in URL-safe Base64.
  - Tamper detection: any modification to ciphertext or tag results in immediate cryptographic rejection (`AEADBadTagException`).

---

## Build & Test Instructions

### 1. Prerequisites
- **Java**: JDK 17 (e.g. `/opt/homebrew/opt/openjdk@17`)
- **Android SDK**: API 34 installed (at `~/Library/Android/sdk`)

### 2. Run Unit Tests
```bash
cd apps/android
JAVA_HOME=/opt/homebrew/opt/openjdk@17 ./gradlew test --info
```
Tests executed:
- `testAesGcmEncryptDecryptRoundtrip`: Verifies end-to-end PBKDF2 key derivation and AES-256-GCM encryption/decryption.
- `testAesGcmTamperDetection`: Verifies cryptographic rejection of modified ciphertext and authentication tags.
- `testWrongPassphraseRejection`: Confirms incorrect passphrases fail authentication tag validation.
- `testSha256Checksum`: Validates deterministic SHA-256 hashing.
- `testProtocolEnvelopeSerialization`: Validates JSON serialization matching `@clipsync/protocol`.
- `testDuplicateAndLoopSuppression`: Validates echo suppression and loop prevention state logic.

### 3. Build Debug APK
```bash
cd apps/android
JAVA_HOME=/opt/homebrew/opt/openjdk@17 ./gradlew assembleDebug
```
The compiled APK is placed at:
- `apps/android/app/build/outputs/apk/debug/app-debug.apk`
- Ready-to-install copy: `dist/android/ClipSync-debug.apk` (7.7 MB)

---

## Installation & Setup on Redmi 10 Prime (MIUI / HyperOS)

### Option A: Install via ADB (USB Debugging)
1. On Redmi 10 Prime, enable **Developer Options**:
   - Go to **Settings** > **About Phone** > Tap **MIUI version** 7 times.
   - Go to **Additional Settings** > **Developer options**.
   - Enable **USB Debugging** and **Install via USB**.
2. Connect phone via USB cable and run:
   ```bash
   /Users/vinit/Library/Android/sdk/platform-tools/adb install -r dist/android/ClipSync-debug.apk
   ```

### Option B: Direct File Transfer
1. Send `dist/android/ClipSync-debug.apk` to your Redmi 10 Prime (via USB cable, Google Drive, WhatsApp, or LocalSend).
2. Tap the APK file in the phone's File Manager.
3. Allow "Install from unknown sources" and tap **Install**.

### Mandatory Xiaomi (MIUI/HyperOS) Background Settings:
Xiaomi aggressively terminates background services by default. To maintain continuous synchronization:
1. Long-press the **ClipSync** app icon > tap **App info** (or ℹ️ icon).
2. **Autostart**: Turn switch **ON** (Allow app to start automatically).
3. **Battery Saver**: Change from "MIUI Battery saver" to **No restrictions**.
4. **Notifications**: Ensure "Show notifications" is enabled.
5. **Quick Settings Tile**:
   - Swipe down twice from the top of your screen to open the full Quick Settings panel.
   - Tap the edit pencil/tile editor button.
   - Find the **ClipSync** tile and drag it into your active tiles.
   - Now, you can tap **Sync Clipboard** anytime from any app to instantly sync your clipboard to Mac & Windows!
