#!/usr/bin/env bash
# Makes the key pair that signs the tokens (ECDSA P-256, "ES256") and prints the two lines for deploy/.env. Nothing is written to a file.
#
#   ./deploy/make-jwt-keys.sh
#
# JWT_PRIVATE_KEY signs the tokens: only the API gets it. JWT_PUBLIC_KEY checks them: the key vault (trader) gets this one, and it
# cannot make a token with it. Every run makes a NEW pair. Changing the pair on a running stack signs everybody out of the panel once
# (the refresh tokens are not affected; the 10-minute confirmation of a second-factor code has to be given again).
# See docs/database-security.md. Needs openssl, which every server has.
set -u -o pipefail

command -v openssl >/dev/null 2>&1 || { echo "openssl is needed (sudo apt install openssl)." >&2; exit 1; }

pem="$(openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt)" || { echo "could not make a key." >&2; exit 1; }
# PKCS#8 (what .NET reads) for the private key: `openssl pkey -outform DER` would write the older SEC1 form instead.
private_key="$(printf '%s\n' "$pem" | openssl pkcs8 -topk8 -nocrypt -outform DER | base64 | tr -d '\n\r')"
public_key="$(printf '%s\n' "$pem" | openssl pkey -pubout -outform DER | base64 | tr -d '\n\r')"
[ -n "$private_key" ] && [ -n "$public_key" ] || { echo "could not make a key." >&2; exit 1; }

echo "# Put these two lines in deploy/.env (and remove JWT_SECRET, it is not used any more)."
echo "# The private key is a secret: only the API's environment, never git, never a chat."
echo "JWT_PRIVATE_KEY=$private_key"
echo "JWT_PUBLIC_KEY=$public_key"
