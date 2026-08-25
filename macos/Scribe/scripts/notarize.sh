#!/usr/bin/env bash
set -euo pipefail

# Scaffolding for Apple notarization, documented but NOT verified end to end: this environment has
# no Apple Developer Program membership or Developer ID certificate, so this script cannot be run
# to completion here. It exists so that whoever has real credentials can ship a distributable
# build without first reverse-engineering the notarytool flow. Do not run this expecting it to
# work without every one of the prerequisites below.
#
# Prerequisites this script does NOT set up for you:
#   1. An active Apple Developer Program membership ($99/year).
#   2. A "Developer ID Application" certificate in your login keychain (Xcode > Settings >
#      Accounts > Manage Certificates, or Certificates, Identifiers and Profiles on the Apple
#      Developer site). Ad-hoc signing (what build-app.sh falls back to) and the local
#      self-signed "Scribe Local Dev" identity from setup-dev-signing.sh are NOT accepted for
#      notarization; Apple only notarizes binaries signed with a real Developer ID certificate.
#   3. An app-specific password or API key for notarytool, stored once via:
#        xcrun notarytool store-credentials "scribe-notarize" \
#          --apple-id "you@example.com" --team-id "TEAMID1234" --password "app-specific-password"
#      (or an App Store Connect API key with --key/--key-id/--issuer instead).
#
# Usage once the above exists:
#   DEVELOPER_ID_APPLICATION="Developer ID Application: Your Name (TEAMID1234)" \
#   NOTARY_PROFILE="scribe-notarize" \
#     scripts/notarize.sh [release]

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
CONFIGURATION="${1:-release}"
APP_DIR="$PACKAGE_DIR/dist/Scribe.app"

: "${DEVELOPER_ID_APPLICATION:?Set DEVELOPER_ID_APPLICATION to your 'Developer ID Application: ...' signing identity}"
: "${NOTARY_PROFILE:?Set NOTARY_PROFILE to the notarytool credential profile name from 'store-credentials'}"

"$SCRIPT_DIR/build-app.sh" "$CONFIGURATION"

if [ ! -d "$APP_DIR" ]; then
    echo "error: $APP_DIR was not produced by build-app.sh" >&2
    exit 1
fi

# Real distribution signing: unlike build-app.sh's ad-hoc/local-dev fallback, this must be a
# Developer ID Application certificate, with the hardened runtime enabled (--options runtime),
# which notarization requires.
codesign --force --deep --options runtime --sign "$DEVELOPER_ID_APPLICATION" "$APP_DIR"

ZIP_PATH="$PACKAGE_DIR/dist/Scribe-for-notarization.zip"
rm -f "$ZIP_PATH"
ditto -c -k --keepParent "$APP_DIR" "$ZIP_PATH"

xcrun notarytool submit "$ZIP_PATH" --keychain-profile "$NOTARY_PROFILE" --wait

# Stapling embeds the notarization ticket in the app itself, so Gatekeeper can verify it offline
# (a user without internet access, or Apple's notary service being briefly unreachable, would
# otherwise still see a warning even though the app was in fact notarized).
xcrun stapler staple "$APP_DIR"

echo "Notarized and stapled: $APP_DIR"
echo "Re-run scripts/make-dmg.sh afterward to package this exact, now-stapled build into a DMG."
