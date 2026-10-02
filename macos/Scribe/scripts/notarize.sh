#!/usr/bin/env bash
set -euo pipefail

# Notarizes and staples an already signed Scribe release artifact.
#
# Usage:
#   scripts/notarize.sh app macos/Scribe/dist/Scribe.app
#   scripts/notarize.sh dmg macos/Scribe/dist/Scribe-macOS-<version>.dmg
#
# The notarytool profile defaults to "scribe-notary" and can be overridden with
# SCRIBE_NOTARY_PROFILE. Create it once with:
#   xcrun notarytool store-credentials scribe-notary --apple-id ... --team-id ...

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ARTIFACT_TYPE="${1:-}"
ARTIFACT_PATH="${2:-}"
NOTARY_PROFILE="${SCRIBE_NOTARY_PROFILE:-scribe-notary}"
NOTARY_WORK_DIR="$PACKAGE_DIR/.build/notary"

usage() {
    echo "usage: $0 app <Scribe.app> | dmg <Scribe.dmg>" >&2
}

extract_request_id() {
    sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1
}

print_notary_log() {
    local request_id="$1"
    if [ -z "$request_id" ]; then
        echo "notarytool did not return a request id; no log can be fetched." >&2
        return
    fi

    echo "Fetching notarization log for $request_id..." >&2
    xcrun notarytool log "$request_id" --keychain-profile "$NOTARY_PROFILE" || true
}

submit_for_notarization() {
    local submit_path="$1"
    local submit_output
    local request_id

    set +e
    submit_output="$(xcrun notarytool submit "$submit_path" \
        --keychain-profile "$NOTARY_PROFILE" \
        --wait \
        --output-format json 2>&1)"
    local submit_status=$?
    set -e

    printf '%s\n' "$submit_output"
    request_id="$(printf '%s\n' "$submit_output" | extract_request_id)"

    if [ "$submit_status" -ne 0 ]; then
        print_notary_log "$request_id"
        exit "$submit_status"
    fi

    if ! printf '%s\n' "$submit_output" | grep -q '"status"[[:space:]]*:[[:space:]]*"Accepted"'; then
        print_notary_log "$request_id"
        echo "error: notarization did not return Accepted status" >&2
        exit 1
    fi
}

if [ -z "$ARTIFACT_TYPE" ] || [ -z "$ARTIFACT_PATH" ]; then
    usage
    exit 1
fi

case "$ARTIFACT_TYPE" in
    app)
        if [ ! -d "$ARTIFACT_PATH" ]; then
            echo "error: app bundle not found: $ARTIFACT_PATH" >&2
            exit 1
        fi

        mkdir -p "$NOTARY_WORK_DIR"
        ZIP_PATH="$NOTARY_WORK_DIR/Scribe-for-notarization.zip"
        rm -f "$ZIP_PATH"
        ditto -c -k --keepParent "$ARTIFACT_PATH" "$ZIP_PATH"
        submit_for_notarization "$ZIP_PATH"
        xcrun stapler staple "$ARTIFACT_PATH"
        xcrun stapler validate "$ARTIFACT_PATH"
        spctl -a -vvv -t exec "$ARTIFACT_PATH"
        ;;
    dmg)
        if [ ! -f "$ARTIFACT_PATH" ]; then
            echo "error: DMG not found: $ARTIFACT_PATH" >&2
            exit 1
        fi

        submit_for_notarization "$ARTIFACT_PATH"
        xcrun stapler staple "$ARTIFACT_PATH"
        xcrun stapler validate "$ARTIFACT_PATH"
        spctl -a -t open --context context:primary-signature -v "$ARTIFACT_PATH"
        ;;
    *)
        usage
        exit 1
        ;;
esac

echo "Notarized, stapled, and verified: $ARTIFACT_PATH"
