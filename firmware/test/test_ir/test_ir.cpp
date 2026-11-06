#include <unity.h>

#include "../../lib/hornet-core/src/IrCode.h"

using namespace hornet::ir;

void setUp() {}
void tearDown() {}

static void test_hex_is_read_with_either_prefix_and_case() {
    uint64_t v = 0;
    TEST_ASSERT_TRUE(parseHex("0x02FD807F", v));
    TEST_ASSERT_EQUAL_UINT64(0x02FD807FULL, v);
    TEST_ASSERT_TRUE(parseHex("0Xa1", v));
    TEST_ASSERT_EQUAL_UINT64(0xA1ULL, v);
    TEST_ASSERT_TRUE(parseHex("0xFFFFFFFFFFFFFFFF", v));
    TEST_ASSERT_EQUAL_UINT64(0xFFFFFFFFFFFFFFFFULL, v);
}

static void test_anything_that_is_not_a_hex_code_is_refused() {
    uint64_t v = 7;
    TEST_ASSERT_FALSE(parseHex(nullptr, v));
    TEST_ASSERT_FALSE(parseHex("", v));
    TEST_ASSERT_FALSE(parseHex("02FD807F", v));     // no prefix
    TEST_ASSERT_FALSE(parseHex("0x", v));           // no digits
    TEST_ASSERT_FALSE(parseHex("0x1234567890ABCDEF1", v));  // 17 digits
    TEST_ASSERT_FALSE(parseHex("0x12G4", v));
    TEST_ASSERT_FALSE(parseHex("0x12 4", v));
    TEST_ASSERT_EQUAL_UINT64(7ULL, v);              // untouched on failure
}

static void test_a_code_is_written_with_the_digits_its_bits_need() {
    TEST_ASSERT_EQUAL_STRING("0x02FD807F", formatHex(0x02FD807FULL, 32).c_str());
    TEST_ASSERT_EQUAL_STRING("0x0A1", formatHex(0xA1ULL, 12).c_str());
    TEST_ASSERT_EQUAL_STRING("0x1", formatHex(1ULL, 1).c_str());
    TEST_ASSERT_EQUAL_STRING("0x0000000000000001", formatHex(1ULL, 64).c_str());
}

static void test_the_state_text_matches_what_tasmota_reports() {
    TEST_ASSERT_EQUAL_STRING("NEC 32 0x02FD807F", describe("NEC", 32, 0x02FD807FULL).c_str());
    TEST_ASSERT_EQUAL_STRING("SONY 12 0xA90", describe("SONY", 12, 0xA90ULL).c_str());
}

static void test_a_text_written_by_formatHex_reads_back() {
    uint64_t v = 0;
    TEST_ASSERT_TRUE(parseHex(formatHex(0x1234ABCDULL, 32).c_str(), v));
    TEST_ASSERT_EQUAL_UINT64(0x1234ABCDULL, v);
}

static void test_protocol_names_and_bit_counts_are_checked() {
    TEST_ASSERT_TRUE(validProtocolName("NEC"));
    TEST_ASSERT_TRUE(validProtocolName("RC5"));
    TEST_ASSERT_TRUE(validProtocolName("LG2"));
    TEST_ASSERT_FALSE(validProtocolName("N"));
    TEST_ASSERT_FALSE(validProtocolName("NE C"));
    TEST_ASSERT_FALSE(validProtocolName("N;EC"));
    TEST_ASSERT_FALSE(validProtocolName(nullptr));
    TEST_ASSERT_TRUE(validBits(1));
    TEST_ASSERT_TRUE(validBits(64));
    TEST_ASSERT_FALSE(validBits(0));
    TEST_ASSERT_FALSE(validBits(65));
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_hex_is_read_with_either_prefix_and_case);
    RUN_TEST(test_anything_that_is_not_a_hex_code_is_refused);
    RUN_TEST(test_a_code_is_written_with_the_digits_its_bits_need);
    RUN_TEST(test_the_state_text_matches_what_tasmota_reports);
    RUN_TEST(test_a_text_written_by_formatHex_reads_back);
    RUN_TEST(test_protocol_names_and_bit_counts_are_checked);
    return UNITY_END();
}
