#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "==> Building and running Windows native unit tests..."
dotnet run --project Tests/ClipSync.Windows.Tests.csproj
