"""
Does Binance USDS-M Futures REST accept signed requests from an Ed25519 key? And from an RSA key?

Read only: one signed GET of the account balances. It never places an order, and it prints no balance, only the status and
Binance's own error code.

  1. python binance_key_check.py generate ed25519        makes ed25519_private.pem here and prints the PUBLIC key
  2. In Binance (Demo Trading first): API Management -> create a SELF-GENERATED key, paste the public key, allow reading
     (and the IP of this computer if Binance asks for it). Binance answers with an API key.
  3. python binance_key_check.py check ed25519           asks for the API key (not echoed) and sends the request
  4. If it says the signature is not accepted: repeat with `rsa` instead of `ed25519`.

The private key stays in the .pem file on this computer. It is a test key: delete the file and the Binance API key afterwards.
Needs:  pip install cryptography
"""
import argparse
import base64
import getpass
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ed25519, padding, rsa

DEMO = "https://demo-fapi.binance.com"
LIVE = "https://fapi.binance.com"
PATH = "/fapi/v2/balance"  # SIGNED, read only


def private_path(kind):
    return f"{kind}_private.pem"


def generate(kind):
    path = private_path(kind)
    if os.path.exists(path):
        sys.exit(f"{path} already exists; delete it first (it holds a private key, so this tool never overwrites one).")

    key = ed25519.Ed25519PrivateKey.generate() if kind == "ed25519" else rsa.generate_private_key(public_exponent=65537, key_size=4096)
    private_pem = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "wb") as f:
        f.write(private_pem)

    public_pem = key.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()
    print(f"Private key saved to {os.path.abspath(path)} (keep it on this computer, never send it anywhere).")
    print("Paste this PUBLIC key into Binance (self-generated API key):\n")
    print(public_pem)


def sign(kind, key, payload):
    data = payload.encode("ascii")
    raw = key.sign(data) if kind == "ed25519" else key.sign(data, padding.PKCS1v15(), hashes.SHA256())
    return base64.b64encode(raw).decode("ascii")


def check(kind, base):
    with open(private_path(kind), "rb") as f:
        key = serialization.load_pem_private_key(f.read(), password=None)

    api_key = os.environ.get("BINANCE_API_KEY") or getpass.getpass("API key from Binance (not shown): ").strip()
    payload = urllib.parse.urlencode({"timestamp": int(time.time() * 1000), "recvWindow": 5000})
    signature = urllib.parse.quote(sign(kind, key, payload), safe="")  # base64 has + / = which must be URL-encoded
    request = urllib.request.Request(f"{base}{PATH}?{payload}&signature={signature}", headers={"X-MBX-APIKEY": api_key})

    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            status, body = response.status, response.read().decode()
    except urllib.error.HTTPError as e:
        status, body = e.code, e.read().decode()
    except urllib.error.URLError as e:
        sys.exit(f"Could not reach {base}: {e.reason}")

    print(f"{kind} against {base}: HTTP {status}")
    try:
        parsed = json.loads(body)
    except json.JSONDecodeError:
        print("Not JSON:", body[:200])
        return 2

    if status == 200:
        print(f"ACCEPTED. {kind} signatures work for Futures REST ({len(parsed)} asset rows returned, not shown).")
        return 0

    code, msg = parsed.get("code"), parsed.get("msg")
    print(f"Binance says: code {code}: {msg}")
    meaning = {
        -1022: f"The SIGNATURE was not accepted. If the key and the method are right, {kind} is not supported here.",
        -2014: "The API key is not in the right format (check what you pasted).",
        -2015: "Key, IP or permission problem: Binance did not get as far as the signature (check IP restriction and 'Enable Reading').",
        -1021: "The clock of this computer is off (timestamp outside recvWindow).",
        -2008: "Binance does not know this API key (wrong environment: demo key on live, or the other way round?).",
    }.get(code, "Not a known verdict: do not conclude anything from this one.")
    print(meaning)
    return 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("generate").add_argument("kind", choices=["ed25519", "rsa"])
    c = sub.add_parser("check")
    c.add_argument("kind", choices=["ed25519", "rsa"])
    c.add_argument("--live", action="store_true", help="use fapi.binance.com instead of the Demo Trading environment")
    c.add_argument("--base", help="another address (for testing this tool against a stand-in)")
    args = parser.parse_args()

    if args.command == "generate":
        generate(args.kind)
    else:
        sys.exit(check(args.kind, args.base or (LIVE if args.live else DEMO)))
