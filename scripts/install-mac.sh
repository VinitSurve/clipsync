#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

# Build package
"$SCRIPT_DIR/package-macos.sh"

APP_SRC="$ROOT_DIR/dist/macos/ClipSync.app"
INSTALL_DIR="/Applications"
if [ ! -w "$INSTALL_DIR" ]; then
    INSTALL_DIR="$HOME/Applications"
    mkdir -p "$INSTALL_DIR"
fi

echo "==> Installing ClipSync to $INSTALL_DIR/ClipSync.app..."
killall ClipSync 2>/dev/null || true
sleep 0.5

rm -rf "$INSTALL_DIR/ClipSync.app"
cp -R "$APP_SRC" "$INSTALL_DIR/ClipSync.app"

# Register LaunchAgent for auto-login startup
LAUNCH_AGENTS_DIR="$HOME/Library/LaunchAgents"
mkdir -p "$LAUNCH_AGENTS_DIR"
PLIST_FILE="$LAUNCH_AGENTS_DIR/com.clipsync.macos.plist"

cat <<EOF > "$PLIST_FILE"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.clipsync.macos</string>
    <key>ProgramArguments</key>
    <array>
        <string>$INSTALL_DIR/ClipSync.app/Contents/MacOS/ClipSync</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>ProcessType</key>
    <string>Interactive</string>
</dict>
</plist>
EOF

echo "==> Registered LaunchAgent at $PLIST_FILE"
launchctl unload "$PLIST_FILE" 2>/dev/null || true
launchctl load "$PLIST_FILE" 2>/dev/null || true

echo "==> Launching ClipSync from $INSTALL_DIR/ClipSync.app..."
open "$INSTALL_DIR/ClipSync.app"

echo "==> Installation complete! ClipSync is now running in your menu bar and will automatically start whenever you log in."
