#!/usr/bin/env bash
set -euo pipefail

# Packages the built Scribe.app into a distributable, drag-to-Applications .dmg using hdiutil
# (part of every macOS install, no Apple Developer account needed). This is the direct-download
# artifact equivalent of Windows' Velopack Setup.exe/portable zip (build/pack.ps1); Scribe for
# macOS has no auto-updater yet (see PORTING-PLAN.md), so this DMG is a plain one-time install,
# not a Velopack-style update channel.
#
# Usage: scripts/make-dmg.sh [release|debug]
# Requires scripts/build-app.sh to have produced dist/Scribe.app already, or runs it first.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
CONFIGURATION="${1:-release}"
APP_DIR="$PACKAGE_DIR/dist/Scribe.app"
APP_VERSION="$(tr -d '[:space:]' < "$PACKAGE_DIR/VERSION")"
DIST_DIR="$PACKAGE_DIR/dist"
DMG_NAME="Scribe-macOS-$APP_VERSION.dmg"
DMG_PATH="$DIST_DIR/$DMG_NAME"
STAGING_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGING_DIR"' EXIT

"$SCRIPT_DIR/build-app.sh" "$CONFIGURATION"

if [ ! -d "$APP_DIR" ]; then
    echo "error: $APP_DIR was not produced by build-app.sh" >&2
    exit 1
fi

rm -f "$DMG_PATH"

# A staging folder with just the app plus an /Applications symlink gives the familiar
# drag-to-install layout without needing a separate .dmg-authoring tool.
cp -R "$APP_DIR" "$STAGING_DIR/Scribe.app"
ln -s /Applications "$STAGING_DIR/Applications"

hdiutil create -volname "Scribe" -srcfolder "$STAGING_DIR" -ov -format UDZO "$DMG_PATH" >/dev/null

echo "Built DMG: $DMG_PATH"
echo "Note: this DMG is ad-hoc/local-dev signed only (see setup-dev-signing.sh) and is not"
echo "notarized. Gatekeeper will warn on a machine other than this one; see notarize.sh for what"
echo "real distribution requires."
