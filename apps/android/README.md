# ClipSync — Android Companion App

Native Kotlin Android app for cross-platform clipboard synchronization.

## Features

- **Background & Foreground Sync**: Uses an Android Foreground Service with an ongoing notification to maintain live WebSocket sync and listen for clipboard changes without OS battery-killer termination.
- **Quick Settings Tile**: Add the "ClipSync" tile to your Android Quick Settings drawer to trigger an instant clipboard capture and sync from inside any app with one tap.
- **QR Code & 6-Digit Pairing**: Fast pairing with your MacBook Air or Windows PC.
- **Hardware-Backed AES-256-GCM**: Client-side end-to-end encryption.
- **Loop & Echo Suppression**: Automatic SHA-256 deduplication to prevent endless copy loops.

## How to Build & Install

Requirements: Android Studio Hedgehog+ or JDK 17 with Android SDK.

1. Build debug APK:
   ```bash
   ./gradlew assembleDebug
   ```
2. Install onto connected Android device or emulator via ADB:
   ```bash
   adb install app/build/outputs/apk/debug/app-debug.apk
   ```
