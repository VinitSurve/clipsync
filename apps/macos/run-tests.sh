#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "==> Building and running macOS native unit tests..."
TMP_BIN="/tmp/clipsync_mac_tests"

swiftc -O -o "$TMP_BIN" \
  Tests/MacUnitTests.swift \
  Sources/Crypto/ClipSyncCrypto.swift \
  Sources/Models/TransferModels.swift \
  Sources/Models/SyncModels.swift

"$TMP_BIN"
rm -f "$TMP_BIN"
