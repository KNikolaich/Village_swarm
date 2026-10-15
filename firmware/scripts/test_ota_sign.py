"""Checks the pure-Python Ed25519 against the RFC 8032 test vectors:  python scripts/test_ota_sign.py"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ed25519  # noqa: E402

# RFC 8032 section 7.1: (secret, public, message, signature)
VECTORS = [
    ("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
     "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
     "",
     "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"),
    ("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
     "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
     "72",
     "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"),
]


def check(secret, public, message, signature):
    secret, message = bytes.fromhex(secret), bytes.fromhex(message)
    assert ed25519.public_key(secret).hex() == public, "public key"
    assert ed25519.sign(secret, message).hex() == signature, "signature"
    assert ed25519.verify(bytes.fromhex(public), message, bytes.fromhex(signature)), "verify"


def main():
    for vector in VECTORS:
        check(*vector)

    secret = bytes(range(32))
    public = ed25519.public_key(secret)
    message = os.urandom(64)
    signature = ed25519.sign(secret, message)
    assert ed25519.verify(public, message, signature)
    assert not ed25519.verify(public, message + b"x", signature), "another message"
    assert not ed25519.verify(public, message, bytes([signature[0] ^ 1]) + signature[1:]), "damaged signature"
    assert not ed25519.verify(ed25519.public_key(bytes(32)), message, signature), "another key"
    print("ed25519: all checks passed")


if __name__ == "__main__":
    main()
