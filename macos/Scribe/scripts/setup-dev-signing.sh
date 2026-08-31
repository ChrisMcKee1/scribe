#!/usr/bin/env bash
# One-time setup for local macOS development: creates a stable, self-signed code-signing identity
# so rebuilt Scribe.app bundles keep a consistent signature across builds.
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

KEYCHAIN_NAME="scribe-dev.keychain-db"
KEYCHAIN_PATH="$HOME/Library/Keychains/$KEYCHAIN_NAME"
IDENTITY_NAME="Scribe Local Dev"
KEYCHAIN_PASSWORD="$(openssl rand -base64 24)"

if security find-identity 2>/dev/null | grep -q "$IDENTITY_NAME"; then
    echo "A '$IDENTITY_NAME' signing identity already exists; nothing to do."
    echo "If Accessibility keeps re-prompting anyway, remove the old grant in System Settings >"
    echo "Privacy & Security > Accessibility, rebuild, and re-grant it once."
    exit 0
fi

WORKDIR="$(mktemp -d)"
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
# non-interactive session; creating and unlocking it here with a throwaway password sidesteps that
# entirely, and macOS keeps it unlocked for the rest of the login session.
security delete-keychain "$KEYCHAIN_PATH" 2>/dev/null || true
security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
security set-keychain-settings "$KEYCHAIN_PATH"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
security import "$WORKDIR/scribe-dev.p12" -k "$KEYCHAIN_PATH" -P "$P12_PASSWORD" -T /usr/bin/codesign -A
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH" >/dev/null

# Add the new keychain to the user's search list (rather than replacing it) so codesign can find
# the identity by name without needing -keychain on every invocation.
EXISTING_KEYCHAINS="$(security list-keychains -d user | sed -e 's/^[[:space:]]*"//' -e 's/"$//')"
security list-keychains -d user -s "$KEYCHAIN_PATH" $EXISTING_KEYCHAINS

echo "Created '$IDENTITY_NAME' signing identity in $KEYCHAIN_PATH."
echo "Rebuild the app (scripts/build-app.sh) and re-grant Accessibility one more time in"
echo "System Settings > Privacy & Security > Accessibility. Every future rebuild will keep the"
echo "same signature, so that grant will stick without re-prompting."
