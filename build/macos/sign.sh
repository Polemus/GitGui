#!/usr/bin/env bash
#
# Code signing and notarisation for the macOS build. Sourced by
# build/macos/package.sh, not run on its own:
#
#     . "$ROOT/build/macos/sign.sh"
#     if signing_available; then signing_prepare; sign_bundle "$APP"; fi
#
# Everything is driven by environment variables, which in CI come from the
# repository secrets of the same name:
#
#   APPLE_CERTIFICATE_P12       Developer ID Application certificate and its
#                               private key, as base64 of the .p12
#   APPLE_CERTIFICATE_PASSWORD  the password that .p12 was exported with
#   APPLE_KEYCHAIN_PASSWORD     any string - it locks the throwaway keychain this
#                               makes, and nothing outside this build ever sees it
#   APPLE_API_KEY_ID            App Store Connect API key, for notarytool
#   APPLE_API_ISSUER_ID
#   APPLE_API_PRIVATE_KEY       the .p8, either as its PEM text or base64 of it
#
# There is deliberately no secret for the signing identity or the team id: both
# are printed on the certificate, so they are read back out of the keychain after
# the import. One less thing to keep in step.
#
# With none of that set - a fork, a pull request, anyone's laptop - every
# function below is a no-op and the build produces the same unsigned artifacts it
# always did. An unsigned build that says so is far better than a release job
# that only works for one person.

# ---------------------------------------------------------------- discovery

# True when there is enough to sign with. Notarisation needs three more variables
# and is checked separately, because signing without notarising is still a
# meaningful improvement - Gatekeeper names the developer instead of refusing to
# say where the app came from.
signing_available() {
    [ -n "${APPLE_CERTIFICATE_P12:-}" ] \
        && [ -n "${APPLE_CERTIFICATE_PASSWORD:-}" ] \
        && [ -n "${APPLE_KEYCHAIN_PASSWORD:-}" ]
}

notarisation_available() {
    [ -n "${APPLE_API_KEY_ID:-}" ] \
        && [ -n "${APPLE_API_ISSUER_ID:-}" ] \
        && [ -n "${APPLE_API_PRIVATE_KEY:-}" ]
}

# ------------------------------------------------------------------ keychain

SIGNING_KEYCHAIN=""
SIGNING_IDENTITY=""
NOTARY_KEY_FILE=""

# Turns APPLE_CERTIFICATE_P12 back into a file.
#
# openssl rather than base64(1): the one on macOS is the BSD build, whose long
# options differ from GNU's, and -A here means "the input is one long line" which
# is exactly what a secret pasted from `base64 -w0` is. The tr strips whatever
# wrapping the secret picked up on its way through a clipboard, which openssl
# would otherwise read as data.
_decode_p12() {
    local out="${1:?usage: _decode_p12 <path>}"

    printf '%s' "$APPLE_CERTIFICATE_P12" \
        | tr -d '\n\r \t' \
        | openssl base64 -d -A > "$out" 2>/dev/null || true

    if [ ! -s "$out" ]; then
        echo "!! APPLE_CERTIFICATE_P12 did not decode to anything" >&2
        echo "   it should be the base64 of the .p12 file:" >&2
        echo "       base64 -i Certificates.p12 | pbcopy" >&2
        return 1
    fi
}

# True when the password opens the file, whatever macOS makes of the container.
# This is the question _explain_p12_failure used to answer by inference, and the
# inference was wrong: not opening with an empty password does not make a
# password correct.
_p12_password_ok() {
    local p12="${1:?}"

    openssl pkcs12 -in "$p12" -passin pass:"$APPLE_CERTIFICATE_PASSWORD" -noout 2>/dev/null \
        || openssl pkcs12 -legacy -in "$p12" -passin pass:"$APPLE_CERTIFICATE_PASSWORD" -noout 2>/dev/null
}

# Rewrites the file with the algorithms macOS will actually read.
#
# OpenSSL 3 defaults a .p12 to AES-256-CBC with a PBKDF2/SHA-256 MAC. security(1)
# does not accept that container and reports "MAC verification failed during
# PKCS12 import (wrong password?)" - the same message it gives for a genuinely
# wrong password, which is the trap. Anyone without a Mac to export from
# Keychain Access has one of these, because openssl is how they had to make it.
#
# The three -pbe/-macalg options ask for the older algorithms explicitly, and are
# spelled the same way by OpenSSL and by the LibreSSL that macOS ships as
# /usr/bin/openssl, so this works whichever is on the runner.
_convert_p12_for_macos() {
    local p12="${1:?}"
    local pem="${p12%.p12}.pem"

    echo "==> Rewriting the certificate in the format security(1) reads"

    # An unencrypted private key on disk, briefly. The runner is thrown away
    # after the job, and it is removed the moment the re-export is done.
    ( umask 077
      openssl pkcs12 -in "$p12" -passin pass:"$APPLE_CERTIFICATE_PASSWORD" -nodes -out "$pem" 2>/dev/null \
        || openssl pkcs12 -legacy -in "$p12" -passin pass:"$APPLE_CERTIFICATE_PASSWORD" -nodes -out "$pem" 2>/dev/null )

    if [ ! -s "$pem" ]; then
        rm -f "$pem"
        return 1
    fi

    openssl pkcs12 -export \
        -in "$pem" \
        -out "$p12.macos" \
        -passout pass:"$APPLE_CERTIFICATE_PASSWORD" \
        -keypbe PBE-SHA1-3DES \
        -certpbe PBE-SHA1-3DES \
        -macalg sha1 2>/dev/null

    rm -f "$pem"

    [ -s "$p12.macos" ] || { rm -f "$p12.macos"; return 1; }
    mv "$p12.macos" "$p12"
}

# Imports the certificate, converting the container first if that is what is in
# the way, and saying which secret is at fault if it is not.
_import_p12() {
    local p12="${1:?}"
    local import=(security import "$p12"
        -k "$SIGNING_KEYCHAIN"
        -P "$APPLE_CERTIFICATE_PASSWORD"
        -T /usr/bin/codesign
        -T /usr/bin/security)

    "${import[@]}" 2>/dev/null && return 0

    local size
    size="$(wc -c < "$p12" | tr -d ' ')"

    # A PKCS#12 is DER, so it always begins with 0x30 (SEQUENCE). Anything else
    # means APPLE_CERTIFICATE_P12 is not the base64 of a .p12 - most often a .cer
    # export, which carries no private key and cannot sign anything.
    if [ "$(head -c 1 "$p12" | od -An -tx1 | tr -d ' ')" != "30" ]; then
        echo "!! APPLE_CERTIFICATE_P12 is not a PKCS#12 file ($size bytes decoded)" >&2
        echo "   a .cer export has no private key in it and cannot sign" >&2
        return 1
    fi

    if ! _p12_password_ok "$p12"; then
        echo "!! APPLE_CERTIFICATE_PASSWORD does not open the certificate" >&2
        echo "   openssl rejects it too, so this is the password and not the format." >&2
        echo "   Check it with:" >&2
        echo "       openssl pkcs12 -in Certificates.p12 -noout -passin pass:'...'" >&2
        return 1
    fi

    # The password is right and security still refused, so it is the container.
    if ! _convert_p12_for_macos "$p12"; then
        echo "!! the certificate opens but could not be rewritten for macOS" >&2
        return 1
    fi

    if ! "${import[@]}"; then
        echo "!! the certificate still will not import after being rewritten" >&2
        return 1
    fi
}

# Imports the certificate into a keychain of its own and works out which identity
# it holds. Safe to call more than once.
#
# The team id is never needed: notarytool takes an App Store Connect key, and the
# issuer already identifies the team. Nothing here has to be told it.
signing_prepare() {
    [ -n "$SIGNING_IDENTITY" ] && return 0

    local work="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
    SIGNING_KEYCHAIN="$work/gitgui-signing.keychain-db"

    # A keychain of our own rather than the login one: this runs on a shared
    # runner image, and the cleanup at the end has to be able to remove
    # everything it added without touching anything it did not.
    if [ ! -f "$SIGNING_KEYCHAIN" ]; then
        echo "==> Creating a keychain for the signing certificate"
        security create-keychain -p "$APPLE_KEYCHAIN_PASSWORD" "$SIGNING_KEYCHAIN"

        # Without this the keychain relocks on a timer part-way through a long
        # notarisation wait, and the *next* codesign fails for no visible reason.
        security set-keychain-settings -lut 21600 "$SIGNING_KEYCHAIN"
        security unlock-keychain -p "$APPLE_KEYCHAIN_PASSWORD" "$SIGNING_KEYCHAIN"

        local p12="$work/certificate.p12"
        _decode_p12 "$p12"

        if ! _import_p12 "$p12"; then
            rm -f "$p12"
            return 1
        fi

        rm -f "$p12"

        # The import alone leaves the key needing interactive approval the first
        # time codesign touches it, which on a runner means hanging until the job
        # times out rather than failing.
        security set-key-partition-list \
            -S apple-tool:,apple:,codesign: \
            -s -k "$APPLE_KEYCHAIN_PASSWORD" \
            "$SIGNING_KEYCHAIN" >/dev/null

        # Prepend rather than replace: dropping the login keychain out of the
        # search list breaks other tools on the runner in confusing ways. The sed
        # strips the indentation and quotes that list-keychains prints, and only
        # those - trimming spaces generally would corrupt any path containing one.
        local existing
        existing="$(security list-keychains -d user | sed -e 's/^[[:space:]]*"//' -e 's/"[[:space:]]*$//')"
        # Word splitting on the newlines is the point here.
        # shellcheck disable=SC2086
        security list-keychains -d user -s "$SIGNING_KEYCHAIN" $existing
    fi

    # find-identity prints e.g.
    #   1) ABC123... "Developer ID Application: Some Name (TEAMID123)"
    SIGNING_IDENTITY="$(security find-identity -v -p codesigning "$SIGNING_KEYCHAIN" \
        | awk -F'"' '/Developer ID Application/ { print $2; exit }')"

    if [ -z "$SIGNING_IDENTITY" ]; then
        echo "!! no Developer ID Application identity in the certificate" >&2
        echo "   a Mac App Store or Development certificate cannot sign a download" >&2
        return 1
    fi

    echo "==> Signing as $SIGNING_IDENTITY"
}

# Removes the keychain and the notarisation key. Called from a trap, so it must
# not fail when there is nothing to remove.
signing_cleanup() {
    [ -n "$NOTARY_KEY_FILE" ] && rm -f "$NOTARY_KEY_FILE"

    if [ -n "$SIGNING_KEYCHAIN" ] && [ -f "$SIGNING_KEYCHAIN" ]; then
        security delete-keychain "$SIGNING_KEYCHAIN" 2>/dev/null || true
    fi

    SIGNING_KEYCHAIN=""
    SIGNING_IDENTITY=""
    NOTARY_KEY_FILE=""
}

# ------------------------------------------------------------------- signing

_codesign() {
    codesign --force \
        --sign "$SIGNING_IDENTITY" \
        --keychain "$SIGNING_KEYCHAIN" \
        --options runtime \
        --timestamp \
        "$@"
}

# Signs a .app, inside out.
#
# The order is the whole trick. A bundle's signature seals what is inside it, so
# every nested binary has to be signed before the bundle is - sign the bundle
# first and the nested signatures invalidate it. --deep exists to do this and
# Apple has told people not to use it for years: it applies the same entitlements
# to every nested binary, which is not what any of them want.
sign_bundle() {
    local app="${1:?usage: sign_bundle <path to .app>}"
    local entitlements
    entitlements="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/entitlements.plist"

    echo "==> Signing the binaries inside $(basename "$app")"

    # Mach-O by content, not by extension: a self-contained publish drops
    # createdump and a handful of other helpers in with no suffix at all, and an
    # unsigned one of those fails notarisation after everything else succeeded.
    local count=0
    while IFS= read -r -d '' file; do
        case "$(file -b "$file")" in
            *Mach-O*)
                _codesign "$file"
                count=$((count + 1))
                ;;
        esac
    done < <(find "$app/Contents" -type f -print0)

    echo "==> Signed $count nested binar$([ "$count" = 1 ] && echo y || echo ies)"

    # The bundle last, and the only thing that carries the entitlements.
    _codesign --entitlements "$entitlements" "$app"

    codesign --verify --deep --strict --verbose=2 "$app"
}

sign_dmg() {
    local dmg="${1:?usage: sign_dmg <path to .dmg>}"

    echo "==> Signing $(basename "$dmg")"
    _codesign "$dmg"
}

# -------------------------------------------------------------- notarisation

_notary_key() {
    if [ -z "$NOTARY_KEY_FILE" ]; then
        NOTARY_KEY_FILE="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/notary-key.p8"

        # Accept the .p8 either as its own text or base64 of it. Both are things
        # people paste into a secret, and telling them apart is one grep.
        if printf '%s' "$APPLE_API_PRIVATE_KEY" | grep -q 'BEGIN PRIVATE KEY'; then
            printf '%s\n' "$APPLE_API_PRIVATE_KEY" > "$NOTARY_KEY_FILE"
        else
            printf '%s' "$APPLE_API_PRIVATE_KEY" | base64 --decode > "$NOTARY_KEY_FILE"
        fi

        chmod 600 "$NOTARY_KEY_FILE"
    fi

    printf '%s' "$NOTARY_KEY_FILE"
}

# Submits something to Apple and waits for the answer, then staples the ticket
# into it so Gatekeeper can see the result without asking Apple again - which is
# what makes a first launch work on a machine that is offline.
notarize() {
    local path="${1:?usage: notarize <path>}"
    local submission="$path"
    local zip=""

    # notarytool takes a .dmg, a .pkg or a .zip, and a .app is none of those.
    if [ -d "$path" ]; then
        zip="${path%.app}.zip"
        # ditto rather than zip: it is the only one that preserves the symlinks
        # and extended attributes a signed bundle depends on.
        ditto -c -k --keepParent "$path" "$zip"
        submission="$zip"
    fi

    echo "==> Notarising $(basename "$submission") - this waits on Apple"
    xcrun notarytool submit "$submission" \
        --key "$(_notary_key)" \
        --key-id "$APPLE_API_KEY_ID" \
        --issuer "$APPLE_API_ISSUER_ID" \
        --wait \
        --timeout 30m

    [ -n "$zip" ] && rm -f "$zip"

    # Staple the original, not the zip: the ticket is looked up by the code
    # signature inside, so it attaches to the .app or .dmg itself.
    echo "==> Stapling the ticket to $(basename "$path")"
    xcrun stapler staple "$path"
}

# What a user's machine will do, done here instead. Worth its own step: a build
# can sign, notarise and staple without complaint and still be refused, and the
# place to find that out is not a bug report.
verify_gatekeeper() {
    local path="${1:?usage: verify_gatekeeper <path>}"

    if [ -d "$path" ]; then
        spctl --assess --type exec --verbose=4 "$path"
    else
        spctl --assess --type open --context context:primary-signature --verbose=4 "$path"
    fi
}
