#include <unity.h>

#include "../../lib/hornet-core/src/InkbirdIht2pb.h"

using namespace hornet::inkbird;

void setUp() {}
void tearDown() {}

static Reading parsed(const uint8_t* frame, size_t n, bool expected = true) {
    Reading r = {-1, 0};
    TEST_ASSERT_EQUAL(expected, parse(frame, n, r));
    return r;
}

static void test_a_positive_temperature_of_probe_one() {
    const uint8_t f[] = {0x55, 0xAA, 0x02, 0x03, 0x00, 0xFA};  // 250 tenths = 25.0 C
    const Reading r = parsed(f, sizeof f);
    TEST_ASSERT_EQUAL(0, r.probe);
    TEST_ASSERT_EQUAL_FLOAT(25.0F, r.celsius);
}

static void test_the_high_byte_counts_256_not_255() {
    const uint8_t f[] = {0x55, 0xAA, 0x04, 0x03, 0x03, 0x84};  // 0x0384 = 900 = 90.0 C
    const Reading r = parsed(f, sizeof f);
    TEST_ASSERT_EQUAL(1, r.probe);
    TEST_ASSERT_EQUAL_FLOAT(90.0F, r.celsius);
}

static void test_a_negative_temperature_is_twos_complement() {
    const uint8_t f[] = {0x55, 0xAA, 0x06, 0x03, 0xFF, 0xF6};  // -10 tenths = -1.0 C
    const Reading r = parsed(f, sizeof f);
    TEST_ASSERT_EQUAL(2, r.probe);
    TEST_ASSERT_EQUAL_FLOAT(-1.0F, r.celsius);
}

static void test_other_selectors_and_short_frames_are_ignored() {
    const uint8_t other[] = {0x55, 0xAA, 0x03, 0x03, 0x00, 0xFA};
    parsed(other, sizeof other, false);
    const uint8_t zero[] = {0x55, 0xAA, 0x00, 0x03, 0x00, 0xFA};
    parsed(zero, sizeof zero, false);
    const uint8_t shortFrame[] = {0x55, 0xAA, 0x02, 0x03, 0x00};
    parsed(shortFrame, sizeof shortFrame, false);
    Reading r;
    TEST_ASSERT_FALSE(parse(nullptr, 6, r));
}

static void test_values_that_are_no_real_temperature_are_ignored() {
    const uint8_t noValue[] = {0x55, 0xAA, 0x02, 0x03, 0xFF, 0xFF};  // 0xFFFF
    parsed(noValue, sizeof noValue, false);
    const uint8_t tooHot[] = {0x55, 0xAA, 0x02, 0x03, 0x0F, 0xA1};  // 4001 = 400.1 C
    parsed(tooHot, sizeof tooHot, false);
    const uint8_t tooCold[] = {0x55, 0xAA, 0x02, 0x03, 0xFE, 0x0B};  // -501 = -50.1 C
    parsed(tooCold, sizeof tooCold, false);
}

static void test_probes_are_named_like_the_other_roles() {
    TEST_ASSERT_EQUAL_STRING("temperature", metricName(0));
    TEST_ASSERT_EQUAL_STRING("temperature_2", metricName(1));
    TEST_ASSERT_EQUAL_STRING("temperature_3", metricName(2));
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_a_positive_temperature_of_probe_one);
    RUN_TEST(test_the_high_byte_counts_256_not_255);
    RUN_TEST(test_a_negative_temperature_is_twos_complement);
    RUN_TEST(test_other_selectors_and_short_frames_are_ignored);
    RUN_TEST(test_values_that_are_no_real_temperature_are_ignored);
    RUN_TEST(test_probes_are_named_like_the_other_roles);
    return UNITY_END();
}
