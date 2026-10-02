#!/usr/bin/env bash
# One-time setup for local macOS development: creates a stable, self-signed code-signing identity
# so rebuilt Scribe.app bundles keep a consistent signature across builds, and stores it in a
# dedicated keychain with a user-chosen password that can be reused later to unlock, delete, or
# recreate that keychain without regenerating the signing identity.
#
# Why this matters: macOS's TCC (privacy) database keys Accessibility/Microphone grants off the
# app's code signature, not its bundle path. build-app.sh previously ad-hoc signed ("codesign
# --sign -") on every build, which mints a brand-new signature each time, so a rebuilt app looks
# like a new binary to TCC and re-prompts for Accessibility on every single run, even after the
# user already granted it once. Running this script once, then re-granting Accessibility one more
# time for the resulting build, makes every subsequent rebuild keep the same signature and the
# same grant.
#
# Usage: scripts/setup-dev-signing.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
KEYCHAIN_NAME="scribe-dev.keychain-db"
KEYCHAIN_PATH="$HOME/Library/Keychains/$KEYCHAIN_NAME"
IDENTITY_NAME="Scribe Local Dev"

ensure_keychain_in_search_list() {
    if security list-keychains -d user | grep -Fq "$KEYCHAIN_PATH"; then
        return
    fi

    EXISTING_KEYCHAINS="$(security list-keychains -d user | sed -e 's/^[[:space:]]*"//' -e 's/"$//')"
    # shellcheck disable=SC2086
    security list-keychains -d user -s "$KEYCHAIN_PATH" $EXISTING_KEYCHAINS
}

keychain_has_identity() {
    security find-identity "$KEYCHAIN_PATH" 2>/dev/null | grep -Fq "\"$IDENTITY_NAME\""
}

if [[ -n "${SCRIBE_KEYCHAIN_PASSWORD:-}" ]]; then
    KEYCHAIN_PASSWORD="$SCRIBE_KEYCHAIN_PASSWORD"
else
    read -r -s -p "Enter a password for $KEYCHAIN_NAME: " KEYCHAIN_PASSWORD
    echo
    read -r -s -p "Re-enter the password for $KEYCHAIN_NAME: " KEYCHAIN_PASSWORD_CONFIRM
    echo

    if [[ "$KEYCHAIN_PASSWORD" != "$KEYCHAIN_PASSWORD_CONFIRM" ]]; then
        echo "Keychain passwords did not match. Re-run the script and try again." >&2
        exit 1
    fi
fi

if [ -f "$KEYCHAIN_PATH" ]; then
    security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH" >/dev/null
fi

ensure_keychain_in_search_list

if keychain_has_identity; then
    echo "A '$IDENTITY_NAME' signing identity already exists in $KEYCHAIN_PATH; nothing to do."
    echo "If TCC keeps re-prompting anyway, remove the old grant in System Settings >"
    echo "Privacy & Security, rebuild, and re-grant it once."
    exit 0
fi

if [ -f "$KEYCHAIN_PATH" ]; then
    echo "Recreating $KEYCHAIN_PATH because '$IDENTITY_NAME' is missing or inaccessible there."
    security delete-keychain "$KEYCHAIN_PATH" 2>/dev/null || true
fi

WORKDIR="$PACKAGE_DIR/.build/setup-dev-signing-work"
rm -rf "$WORKDIR"
mkdir -p "$WORKDIR"
trap 'rm -rf "$WORKDIR"' EXIT

cat > "$WORKDIR/codesign.cnf" <<EOF
[req]
distinguished_name = dn
x509_extensions = v3_ca
prompt = no

[dn]
CN = $IDENTITY_NAME

[v3_ca]
basicConstraints = critical, CA:false
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
EOF

openssl req -x509 -newkey rsa:2048 \
    -keyout "$WORKDIR/key.pem" -out "$WORKDIR/cert.pem" \
    -days 3650 -nodes -config "$WORKDIR/codesign.cnf"

P12_PASSWORD="$(openssl rand -base64 24)"
openssl pkcs12 -export -out "$WORKDIR/scribe-dev.p12" \
    -inkey "$WORKDIR/key.pem" -in "$WORKDIR/cert.pem" -passout "pass:$P12_PASSWORD"

# A dedicated keychain (rather than the login keychain) avoids the interactive "codesign wants to
# use your confidential information" prompt that a login-keychain import can trigger in a
# non-interactive session. The user-chosen password lets later sessions unlock and reuse the same
# identity instead of minting a replacement that would make TCC ask again.
security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
security set-keychain-settings "$KEYCHAIN_PATH"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
security import "$WORKDIR/scribe-dev.p12" -k "$KEYCHAIN_PATH" -P "$P12_PASSWORD" -T /usr/bin/codesign -A
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH" >/dev/null

ensure_keychain_in_search_list

echo "Created '$IDENTITY_NAME' signing identity in $KEYCHAIN_PATH."
echo "Rebuild the app (scripts/build-app.sh) and re-grant Accessibility and Input Monitoring one"
echo "more time in System Settings > Privacy & Security. Every future rebuild will keep the same"
echo "signature, so those grants will stick without re-prompting."
echo "Keep the $KEYCHAIN_NAME password somewhere safe. You will need it if you later want to"
echo "unlock, delete, or recreate that dedicated keychain."
