#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
CONFIGURATION="${1:-release}"
APP_DIR="$PACKAGE_DIR/dist/Scribe.app"
BIN_DIR="$(swift build --package-path "$PACKAGE_DIR" -c "$CONFIGURATION" --show-bin-path)"
EXECUTABLE="$BIN_DIR/Scribe"

# Single source of truth for the marketing version (mirrors the Windows build's
# Directory.Build.props/<VersionPrefix> convention): one file, read here for the Info.plist and
# by make-dmg.sh/notarize.sh for the DMG/artifact names, so nothing else needs editing to release
# a new version.
APP_VERSION="$(tr -d '[:space:]' < "$PACKAGE_DIR/VERSION")"
# The build number must strictly increase across releases for macOS Gatekeeper/Sparkle-style
# update checks to treat a new build as newer; the commit count is a simple, always-available,
# monotonically increasing counter that needs no manual bookkeeping.
if command -v git >/dev/null 2>&1 && BUILD_NUMBER="$(git -C "$PACKAGE_DIR" rev-list --count HEAD 2>/dev/null)" && [ -n "$BUILD_NUMBER" ]; then
    :
else
    BUILD_NUMBER="1"
fi

swift build --package-path "$PACKAGE_DIR" -c "$CONFIGURATION"

rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
cp "$EXECUTABLE" "$APP_DIR/Contents/MacOS/Scribe"
chmod +x "$APP_DIR/Contents/MacOS/Scribe"

# SwiftPM's resource-bundle accessor (`Bundle.module`) resolves relative to `Bundle.main.bundleURL`
# at run time, which for an .app is the .app bundle's own root directory (NOT Contents/Resources;
# `Bundle.main.bundleURL` is the whole "Scribe.app" folder). It carries the 11 built-in dictionary
# library CSVs declared as `resources:` in Package.swift.
RESOURCE_BUNDLE="$BIN_DIR/ScribeMac_Scribe.bundle"
if [ -d "$RESOURCE_BUNDLE" ]; then
    cp -R "$RESOURCE_BUNDLE" "$APP_DIR/"
fi

# Same brand mark as the Windows build (src/Scribe.App/Assets/scribe.ico) and the Store listing
# (docs/icon.png), so the app icon doesn't drift between platforms.
if [ -f "$PACKAGE_DIR/Resources/Scribe.icns" ]; then
    cp "$PACKAGE_DIR/Resources/Scribe.icns" "$APP_DIR/Contents/Resources/Scribe.icns"
fi

# Accessibility trust is not declared with an Info.plist usage string on modern macOS.
# The app must call AXIsProcessTrustedWithOptions so System Settings can surface the prompt or link.
# NSAppleEventsUsageDescription is not needed here because the injector uses Accessibility and
# CGEvent keyboard synthesis, not Apple Events automation.
cat > "$APP_DIR/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleExecutable</key>
    <string>Scribe</string>
    <key>CFBundleIdentifier</key>
    <string>com.scribe.macos</string>
    <key>CFBundleIconFile</key>
    <string>Scribe.icns</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>Scribe</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$APP_VERSION</string>
    <key>CFBundleVersion</key>
    <string>$BUILD_NUMBER</string>
    <key>LSMinimumSystemVersion</key>
    <string>13.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSMicrophoneUsageDescription</key>
    <string>Scribe needs microphone access to support offline push-to-talk dictation on macOS.</string>
</dict>
</plist>
PLIST

if command -v codesign >/dev/null 2>&1; then
    # Signing consistently matters here: macOS's TCC (privacy) database keys Accessibility grants
    # off the code signature, not the bundle path. Ad-hoc signing ("-") mints a fresh signature on
    # every build, so a rebuilt app looks like a brand-new binary to TCC and re-prompts for
    # Accessibility every single time, even though the user already granted it. A stable local
    # signing identity ("Scribe Local Dev", a self-signed cert created by setup-dev-signing.sh)
    # keeps the signature identical across rebuilds so one grant sticks.
    #
    # Note: `security find-identity -v` filters to identities the system CA policy *trusts*, which
    # a self-signed dev cert never is, so it always reports 0 even when the identity works fine for
    # codesign. Check with `find-identity` (no -v) instead, which lists it as CSSMERR_TP_NOT_TRUSTED
    # but still matches, and codesign accepts it regardless of that trust status.
    if security find-identity 2>/dev/null | grep -q "Scribe Local Dev"; then
        codesign --force --deep --sign "Scribe Local Dev" "$APP_DIR" >/dev/null 2>&1 || \
            codesign --force --deep --sign - "$APP_DIR" >/dev/null 2>&1 || true
    else
        codesign --force --deep --sign - "$APP_DIR" >/dev/null 2>&1 || true
    fi
fi

echo "Built app bundle: $APP_DIR (version $APP_VERSION, build $BUILD_NUMBER)"
