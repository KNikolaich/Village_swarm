#include <unity.h>

#include <cstring>
#include <string>

#include "../../lib/hornet-core/src/OtaSignature.h"

using namespace hornet::otasig;

void setUp() {}
void tearDown() {}

// Produced by scripts/ed25519.py (the signer used at build time) for the image below, with the seed 00 01 02 ... 1f:
//   python: digest = sha512(image); signature = ed25519.sign(seed, digest)
static const char* kPublicKey = "03a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1ddc8664125531b8";
static const char* kSignature =
    "c7e81422d27639e07b3a2ffa66fd645298ab13124e58b3ff4cab17744bae49247119b7a6e6b911de344b56dfce903f98f6d63277355ac30c5281e4ea0cc87106";
static const char* kDigest =
    "a7875d841eb26ba90ecce80cc9b5add3a7c3ea2574fb6a948eec3983862c0b653682fa1cd70220a4cee488f40e8d9ee124715075eae3426c7638efe40bed45a4";

static std::string toHex(const uint8_t* data, size_t n) {
    static const char* digits = "0123456789abcdef";
    std::string out;
    for (size_t i = 0; i < n; ++i) {
        out += digits[data[i] >> 4];
        out += digits[data[i] & 15];
    }
    return out;
}

// The image the Python side signed: 5000 bytes, byte i = (i * 7 + 3) mod 256.
static void hashImage(uint8_t digest[kDigestBytes], size_t chunk) {
    ImageHasher hasher;
    uint8_t image[5000];
    for (size_t i = 0; i < sizeof image; ++i) image[i] = static_cast<uint8_t>((i * 7 + 3) % 256);
    for (size_t at = 0; at < sizeof image; at += chunk) hasher.update(image + at, std::min(chunk, sizeof image - at));
    hasher.finish(digest);
}

static void test_the_digest_is_the_same_whatever_the_chunking() {
    uint8_t a[kDigestBytes], b[kDigestBytes];
    hashImage(a, 512);  // how the download feeds it
    hashImage(b, 1);
    TEST_ASSERT_EQUAL_STRING(kDigest, toHex(a, kDigestBytes).c_str());
    TEST_ASSERT_EQUAL_MEMORY(a, b, kDigestBytes);
}

static void test_a_signature_made_by_the_python_signer_verifies() {
    uint8_t digest[kDigestBytes];
    hashImage(digest, 512);
    TEST_ASSERT_TRUE(verify(digest, kSignature, kPublicKey));
}

static void test_a_changed_image_does_not_verify() {
    uint8_t digest[kDigestBytes];
    hashImage(digest, 512);
    digest[10] ^= 1;
    TEST_ASSERT_FALSE(verify(digest, kSignature, kPublicKey));
}

static void test_a_damaged_signature_does_not_verify() {
    uint8_t digest[kDigestBytes];
    hashImage(digest, 512);
    std::string bad = kSignature;
    bad[0] = bad[0] == '0' ? '1' : '0';
    TEST_ASSERT_FALSE(verify(digest, bad, kPublicKey));
}

static void test_another_key_does_not_verify() {
    uint8_t digest[kDigestBytes];
    hashImage(digest, 512);
    std::string other = kPublicKey;
    other[0] = other[0] == '0' ? '1' : '0';
    TEST_ASSERT_FALSE(verify(digest, kSignature, other));
}

static void test_malformed_hex_is_refused_not_crashed_on() {
    uint8_t digest[kDigestBytes];
    hashImage(digest, 512);
    TEST_ASSERT_FALSE(verify(digest, "", kPublicKey));
    TEST_ASSERT_FALSE(verify(digest, std::string(128, 'z'), kPublicKey));
    TEST_ASSERT_FALSE(verify(digest, kSignature, "abcd"));
    TEST_ASSERT_FALSE(verify(digest, std::string(kSignature) + "00", kPublicKey));
}

static void test_a_hornet_with_a_key_refuses_an_unsigned_command() {
    TEST_ASSERT_TRUE(checkCommand(true, false, "") == Verdict::RefuseUnsigned);
    TEST_ASSERT_TRUE(checkCommand(true, false, "short") == Verdict::RefuseUnsigned);
    TEST_ASSERT_TRUE(checkCommand(true, false, kSignature) == Verdict::Proceed);
}

static void test_development_builds_may_accept_unsigned_images() {
    TEST_ASSERT_TRUE(checkCommand(true, true, "") == Verdict::Proceed);    // built with the unsigned flag
    TEST_ASSERT_TRUE(checkCommand(false, true, "") == Verdict::Proceed);   // no key at all: nothing to check against
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_the_digest_is_the_same_whatever_the_chunking);
    RUN_TEST(test_a_signature_made_by_the_python_signer_verifies);
    RUN_TEST(test_a_changed_image_does_not_verify);
    RUN_TEST(test_a_damaged_signature_does_not_verify);
    RUN_TEST(test_another_key_does_not_verify);
    RUN_TEST(test_malformed_hex_is_refused_not_crashed_on);
    RUN_TEST(test_a_hornet_with_a_key_refuses_an_unsigned_command);
    RUN_TEST(test_development_builds_may_accept_unsigned_images);
    return UNITY_END();
}
