#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
MACOS_DIR="$ROOT_DIR/apps/macos"
DIST_DIR="$ROOT_DIR/dist/macos"
APP_NAME="ClipSync.app"
APP_BUNDLE="$DIST_DIR/$APP_NAME"

echo "==> Building ClipSync for macOS (Release)..."
cd "$MACOS_DIR"
swift build -c release

# Locate release binary
RELEASE_BIN="$(find "$MACOS_DIR/.build" -type f -name ClipSync -perm +111 | grep -i release | head -n 1)"
if [ -z "$RELEASE_BIN" ] || [ ! -f "$RELEASE_BIN" ]; then
    RELEASE_BIN="$(find "$MACOS_DIR/.build" -type f -name ClipSync -perm +111 | head -n 1)"
fi

echo "==> Packaging macOS Application Bundle at $APP_BUNDLE..."
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# Copy binary
cp "$RELEASE_BIN" "$APP_BUNDLE/Contents/MacOS/ClipSync"
chmod +x "$APP_BUNDLE/Contents/MacOS/ClipSync"

# Copy Info.plist
cp "$MACOS_DIR/Info.plist" "$APP_BUNDLE/Contents/Info.plist"

# Copy Icon
if [ -f "$MACOS_DIR/Resources/AppIcon.icns" ]; then
    cp "$MACOS_DIR/Resources/AppIcon.icns" "$APP_BUNDLE/Contents/Resources/AppIcon.icns"
fi

# Ad-hoc code sign for seamless local execution on macOS
echo "==> Applying ad-hoc codesign signature..."
codesign --force --deep --sign - "$APP_BUNDLE"

echo "==> ClipSync macOS Application successfully packaged at:"
echo "    $APP_BUNDLE"
