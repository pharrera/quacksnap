#!/usr/bin/env bash
# Builds the QuackSnap Windows installer (Setup.exe) + portable zip.
# Works on macOS/Linux (cross-compiles to Windows) and on Windows.
#
#   ./windows/build-installer.sh [version]
#
# Output: artifacts/releases/QuackSnap-win-Setup.exe
set -euo pipefail

VERSION="${1:-1.0.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUB="$ROOT/artifacts/publish"
REL="$ROOT/artifacts/releases"
ICON="$ROOT/windows/QuackSnap/Assets/QuackSnap.ico"

echo "==> Restoring build tools (vpk)…"
dotnet tool restore

echo "==> Publishing self-contained win-x64…"
rm -rf "$PUB"
dotnet publish "$ROOT/windows/QuackSnap" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=false -o "$PUB"

# Velopack needs the [win] cross-compile directive when the host isn't Windows.
DIRECTIVE=""
case "$(uname -s 2>/dev/null || echo Windows)" in
  MINGW*|MSYS*|CYGWIN*|Windows*) DIRECTIVE="" ;;
  *) DIRECTIVE="[win]" ;;
esac

echo "==> Packaging installer ${VERSION}..."
dotnet vpk $DIRECTIVE pack \
  -u QuackSnap -v "$VERSION" -p "$PUB" -e QuackSnap.exe -r win-x64 \
  --packTitle "QuackSnap" --packAuthors "Peter Herrera" --icon "$ICON" \
  -o "$REL" -y

echo ""
echo "Done. Installer: $REL/QuackSnap-win-Setup.exe"
echo "Portable:        $REL/QuackSnap-win-Portable.zip"
