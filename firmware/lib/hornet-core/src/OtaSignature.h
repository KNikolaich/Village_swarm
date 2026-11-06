#pragma once

// Signed firmware images (docs/ota.md). The hive sends the signature with cmd/ota; the hornet hashes the image while
// it downloads it (SHA-512) and checks the Ed25519 signature of that digest against the public key built into the
// firmware, BEFORE the image is marked for the bootloader. A hive (or broker) that is taken over cannot make a hornet
// run firmware that was not signed with the developer's private key.
//
// Arduino-free: native tests run it with the Python signer's output. Monocypher (BSD-2/CC0) does the maths; its
// Ed25519 + SHA-512 add-on is the standard RFC 8032 algorithm, the same as scripts/ed25519.py.

#include <cstddef>
#include <cstdint>
#include <string>

#include "third_party/monocypher-ed25519.h"

namespace hornet {
namespace otasig {

constexpr size_t kDigestBytes = 64;
constexpr size_t kSignatureBytes = 64;
constexpr size_t kPublicKeyBytes = 32;

inline int hexValue(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

// Exactly 2 * outLen hex characters into outLen bytes; false for anything else.
inline bool fromHex(const std::string& hex, uint8_t* out, size_t outLen) {
    if (hex.size() != outLen * 2) return false;
    for (size_t i = 0; i < outLen; ++i) {
        const int hi = hexValue(hex[2 * i]);
        const int lo = hexValue(hex[2 * i + 1]);
        if (hi < 0 || lo < 0) return false;
        out[i] = static_cast<uint8_t>(hi << 4 | lo);
    }
    return true;
}

// SHA-512 of the image, fed in the chunks it arrives in.
class ImageHasher {
public:
    ImageHasher() { crypto_sha512_init(&ctx_); }
    void update(const uint8_t* data, size_t length) { crypto_sha512_update(&ctx_, data, length); }
    void finish(uint8_t digest[kDigestBytes]) { crypto_sha512_final(&ctx_, digest); }

private:
    crypto_sha512_ctx ctx_;
};

// True when `signatureHex` is a valid Ed25519 signature, by the key `publicKeyHex`, of `digest`.
inline bool verify(const uint8_t digest[kDigestBytes], const std::string& signatureHex, const std::string& publicKeyHex) {
    uint8_t signature[kSignatureBytes];
    uint8_t key[kPublicKeyBytes];
    if (!fromHex(signatureHex, signature, kSignatureBytes) || !fromHex(publicKeyHex, key, kPublicKeyBytes)) return false;
    return crypto_ed25519_check(signature, key, digest, kDigestBytes) == 0;
}

// What to do with an update command, before any download: a hornet that carries a public key (and was not built to
// accept unsigned images) refuses a command without a well-formed signature at once.
enum class Verdict { Proceed, RefuseUnsigned };

inline Verdict checkCommand(bool keyBuiltIn, bool allowUnsigned, const std::string& signatureHex) {
    const bool wellFormed = signatureHex.size() == kSignatureBytes * 2;
    if (keyBuiltIn && !allowUnsigned && !wellFormed) return Verdict::RefuseUnsigned;
    return Verdict::Proceed;
}

}  // namespace otasig
}  // namespace hornet
