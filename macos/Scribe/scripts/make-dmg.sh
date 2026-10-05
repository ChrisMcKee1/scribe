#!/usr/bin/env bash
set -euo pipefail

# Packages the built Scribe.app into a distributable, drag-to-Applications .dmg using hdiutil
# (part of every macOS install, no Apple Developer account needed). This is the direct-download
# artifact equivalent of Windows' Velopack Setup.exe/portable zip (build/pack.ps1); Scribe for
# macOS has no auto-updater yet (see PORTING-PLAN.md), so this DMG is a plain one-time install,
# not a Velopack-style update channel.
#
# Usage: scripts/make-dmg.sh [--no-build] [release|debug]
# By default this runs scripts/build-app.sh first. Release packaging passes --no-build after
# notarizing the app, so the DMG contains the exact stapled bundle.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BUILD_APP=1
if [ "${1:-}" = "--no-build" ]; then
    BUILD_APP=0
    shift
fi
CONFIGURATION="${1:-release}"
APP_DIR="$PACKAGE_DIR/dist/Scribe.app"
APP_VERSION="$(tr -d '[:space:]' < "$PACKAGE_DIR/VERSION")"
DIST_DIR="$PACKAGE_DIR/dist"
DMG_NAME="Scribe-macOS-$APP_VERSION.dmg"
DMG_PATH="$DIST_DIR/$DMG_NAME"
STAGING_DIR="$DIST_DIR/dmg-staging"
trap 'rm -rf "$STAGING_DIR"' EXIT

if [ "$BUILD_APP" -eq 1 ]; then
    "$SCRIPT_DIR/build-app.sh" "$CONFIGURATION"
fi

if [ ! -d "$APP_DIR" ]; then
    echo "error: $APP_DIR was not produced by build-app.sh" >&2
    exit 1
fi

rm -f "$DMG_PATH"
rm -rf "$STAGING_DIR"
mkdir -p "$STAGING_DIR"

# A staging folder with just the app plus an /Applications symlink gives the familiar
# drag-to-install layout without needing a separate .dmg-authoring tool.
cp -R "$APP_DIR" "$STAGING_DIR/Scribe.app"
ln -s /Applications "$STAGING_DIR/Applications"

hdiutil create -volname "Scribe" -srcfolder "$STAGING_DIR" -ov -format UDZO "$DMG_PATH" >/dev/null

echo "Built DMG: $DMG_PATH"
echo "For public distribution, use scripts/release.sh so the app and DMG are Developer ID signed,"
echo "notarized, stapled, and verified."
