#!/usr/bin/env python3
"""Signing keys and signatures for over-the-air updates (docs/ota.md).

  python scripts/ota_sign.py gen            create the key pair (once) and write ota_public_key.hex
  python scripts/ota_sign.py pub            print the public key of the private key
  python scripts/ota_sign.py sign FILE      print the signature of FILE (hex)

The private key (a 32-byte seed, hex) lives OUTSIDE the repository: %USERPROFILE%\\.village-swarm\\ota-signing.key, or
the file named by the HORNET_OTA_KEY environment variable. Keep a copy somewhere safe (a password manager): a hornet
that carries the matching public key accepts only images signed with it, and without the key every such hornet has
to be reflashed over USB. firmware/ota_public_key.hex is the public half and is committed.

What is signed: the SHA-512 digest of the image (64 bytes) with Ed25519. The hornet hashes the image while it
downloads it, so it needs no more memory for the check than for the download.
"""
import hashlib
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ed25519  # noqa: E402

FIRMWARE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PUBLIC_KEY_FILE = os.path.join(FIRMWARE_DIR, "ota_public_key.hex")


def key_path():
    return os.environ.get("HORNET_OTA_KEY") or os.path.join(os.path.expanduser("~"), ".village-swarm", "ota-signing.key")


def load_secret(required=True):
    path = key_path()
    if not os.path.exists(path):
        if required:
            raise SystemExit(f"no signing key at {path}: run `python scripts/ota_sign.py gen` (or set HORNET_OTA_KEY)")
        return None
    secret = bytes.fromhex(open(path, encoding="ascii").read().strip())
    if len(secret) != 32:
        raise SystemExit(f"{path} does not hold a 32-byte key")
    return secret


def image_digest(path):
    h = hashlib.sha512()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 16), b""):
            h.update(chunk)
    return h.digest()


def sign_image(path, secret):
    """(sha512 hex, signature hex) of the image file."""
    digest = image_digest(path)
    return digest.hex(), ed25519.sign(secret, digest).hex()


def main(argv):
    command = argv[1] if len(argv) > 1 else ""
    if command == "gen":
        path = key_path()
        if os.path.exists(path):
            raise SystemExit(f"{path} already exists: not overwriting a signing key")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        secret = os.urandom(32)
        with open(path, "w", encoding="ascii") as f:
            f.write(secret.hex() + "\n")
        try:
            os.chmod(path, 0o600)
        except OSError:
            pass
        public = ed25519.public_key(secret).hex()
        with open(PUBLIC_KEY_FILE, "w", encoding="ascii", newline="\n") as f:
            f.write(public + "\n")
        print(f"private key written to {path}  <- COPY IT TO A SAFE PLACE NOW, it is not in the repository")
        print(f"public key {public} written to {PUBLIC_KEY_FILE} (commit it)")
        return 0
    if command == "pub":
        print(ed25519.public_key(load_secret()).hex())
        return 0
    if command == "sign" and len(argv) > 2:
        print(sign_image(argv[2], load_secret())[1])
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
