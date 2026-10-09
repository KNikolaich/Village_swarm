#include <unity.h>

#include "../../lib/hornet-core/src/RelayTimer.h"

using hornet::RelayTimer;

void setUp() {}
void tearDown() {}

static void test_no_limit_stays_on() {
    RelayTimer t;
    t.start(1000, 0, 0);
    TEST_ASSERT_FALSE(t.active());
    TEST_ASSERT_FALSE(t.expired(1000 + 86400000UL));
}

static void test_for_s_switches_off() {
    RelayTimer t;
    t.start(1000, 60, 0);
    TEST_ASSERT_FALSE(t.expired(1000 + 59999));
    TEST_ASSERT_TRUE(t.expired(1000 + 60000));
    TEST_ASSERT_EQUAL_UINT32(30, t.remainingS(1000 + 30000));
}

static void test_max_on_caps_for_s_and_applies_alone() {
    RelayTimer t;
    t.start(0, 3600, 600);  // the shorter one wins
    TEST_ASSERT_TRUE(t.expired(600000));
    t.start(0, 0, 600);     // no for_s: max_on_s still applies
    TEST_ASSERT_TRUE(t.active());
    TEST_ASSERT_TRUE(t.expired(600000));
    t.start(0, 60, 600);
    TEST_ASSERT_TRUE(t.expired(60000));
}

static void test_survives_millis_wrap_and_stop() {
    RelayTimer t;
    t.start(0xFFFFFF00UL, 1, 0);
    TEST_ASSERT_FALSE(t.expired(0xFFFFFFFFUL));
    TEST_ASSERT_TRUE(t.expired(0x00000400UL));  // wrapped, 1.28 s later
    t.stop();
    TEST_ASSERT_FALSE(t.expired(0x00010000UL));
}

int main(int, char**) {
    UNITY_BEGIN();
    RUN_TEST(test_no_limit_stays_on);
    RUN_TEST(test_for_s_switches_off);
    RUN_TEST(test_max_on_caps_for_s_and_applies_alone);
    RUN_TEST(test_survives_millis_wrap_and_stop);
    return UNITY_END();
}
