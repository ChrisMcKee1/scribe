#!/usr/bin/env bash
set -euo pipefail

# Builds a signed, notarized, stapled Scribe.app and a signed, notarized, stapled DMG.
#
# Usage:
#   SCRIBE_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" \
#   SCRIBE_NOTARY_PROFILE=scribe-notary \
#     scripts/release.sh [release|debug]

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
CONFIGURATION="${1:-release}"
APP_DIR="$PACKAGE_DIR/dist/Scribe.app"
APP_VERSION="$(tr -d '[:space:]' < "$PACKAGE_DIR/VERSION")"
DMG_PATH="$PACKAGE_DIR/dist/Scribe-macOS-$APP_VERSION.dmg"
ENTITLEMENTS="$SCRIPT_DIR/Scribe.Release.entitlements"
NOTARY_PROFILE="${SCRIBE_NOTARY_PROFILE:-scribe-notary}"

fail() {
    echo "error: $*" >&2
    exit 1
}

developer_id_identities() {
    security find-identity -v -p codesigning 2>/dev/null |
        sed -n 's/.*"\(Developer ID Application: [^"]*\)".*/\1/p'
}

resolve_signing_identity() {
    local override="${SCRIBE_SIGN_IDENTITY:-}"
    if [ -n "$override" ]; then
        case "$override" in
            "Developer ID Application: "*)
                ;;
            *)
                fail "SCRIBE_SIGN_IDENTITY must be a Developer ID Application identity, got: $override"
                ;;
        esac

        if ! security find-identity -v -p codesigning 2>/dev/null | grep -Fq "\"$override\""; then
            fail "Developer ID identity from SCRIBE_SIGN_IDENTITY was not found in the default keychain search list: $override"
        fi

        printf '%s\n' "$override"
        return
    fi

    local identities
    identities="$(developer_id_identities)"
    local count
    count="$(printf '%s\n' "$identities" | sed '/^[[:space:]]*$/d' | wc -l | tr -d '[:space:]')"

    case "$count" in
        0)
            fail "no Developer ID Application signing identity was found. Create the certificate in Xcode or set SCRIBE_SIGN_IDENTITY after security find-identity -v -p codesigning lists it."
            ;;
        1)
            printf '%s\n' "$identities" | sed '/^[[:space:]]*$/d'
            ;;
        *)
            printf '%s\n' "$identities" >&2
            fail "multiple Developer ID Application identities were found. Set SCRIBE_SIGN_IDENTITY to the one to use."
            ;;
    esac
}

require_notary_profile() {
    if ! xcrun notarytool history --keychain-profile "$NOTARY_PROFILE" --output-format json >/dev/null 2>&1; then
        fail "notarytool profile '$NOTARY_PROFILE' is missing or unusable. Create it with xcrun notarytool store-credentials ${NOTARY_PROFILE} --apple-id ... --team-id ..."
    fi
}

sign_nested_code() {
    local identity="$1"
    local app_dir="$2"
    local nested

    while IFS= read -r nested; do
        codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$identity" "$nested"
    done < <(
        find "$app_dir/Contents" -depth \
            \( -type d \( -name "*.app" -o -name "*.framework" -o -name "*.xpc" -o -name "*.appex" \) \
            -o -type f \( -perm -111 -o -name "*.dylib" \) \) \
            ! -path "$app_dir/Contents/MacOS/Scribe" \
            -print
    )
}

sign_app() {
    local identity="$1"

    [ -f "$ENTITLEMENTS" ] || fail "release entitlements file is missing: $ENTITLEMENTS"
    [ -d "$APP_DIR" ] || fail "app bundle not found: $APP_DIR"
    [ -f "$APP_DIR/Contents/Info.plist" ] || fail "Info.plist missing from $APP_DIR"

    /usr/libexec/PlistBuddy -c "Print :NSMicrophoneUsageDescription" "$APP_DIR/Contents/Info.plist" >/dev/null ||
        fail "Info.plist is missing NSMicrophoneUsageDescription"

    sign_nested_code "$identity" "$APP_DIR"
    codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$identity" "$APP_DIR"
    codesign --verify --strict --verbose=2 "$APP_DIR"
}

sign_dmg() {
    local identity="$1"

    [ -f "$DMG_PATH" ] || fail "DMG not found: $DMG_PATH"
    codesign --force --timestamp --sign "$identity" "$DMG_PATH"
    codesign --verify --verbose=2 "$DMG_PATH"
}

IDENTITY="$(resolve_signing_identity)"
require_notary_profile

echo "Building Scribe.app $APP_VERSION ($CONFIGURATION)..."
"$SCRIPT_DIR/build-app.sh" "$CONFIGURATION"

echo "Signing app with Developer ID identity: $IDENTITY"
sign_app "$IDENTITY"

echo "Notarizing app with notary profile: $NOTARY_PROFILE"
"$SCRIPT_DIR/notarize.sh" app "$APP_DIR"

echo "Creating DMG from notarized app..."
"$SCRIPT_DIR/make-dmg.sh" --no-build "$CONFIGURATION"

echo "Signing DMG with Developer ID identity: $IDENTITY"
sign_dmg "$IDENTITY"

echo "Notarizing DMG with notary profile: $NOTARY_PROFILE"
"$SCRIPT_DIR/notarize.sh" dmg "$DMG_PATH"

echo "Release artifact ready: $DMG_PATH"
