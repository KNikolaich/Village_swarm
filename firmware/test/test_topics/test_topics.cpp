#include <unity.h>

#include "../../lib/hornet-core/src/Topics.h"

using namespace hornet::topics;

void setUp() {}
void tearDown() {}

static void test_valid_device_ids() {
    TEST_ASSERT_TRUE(isValidDeviceId("guard-gate1"));
    TEST_ASSERT_TRUE(isValidDeviceId("meteo-out1"));
    TEST_ASSERT_TRUE(isValidDeviceId("a"));
}

static void test_invalid_device_ids() {
    TEST_ASSERT_FALSE(isValidDeviceId(""));
    TEST_ASSERT_FALSE(isValidDeviceId("Guard-gate1"));
    TEST_ASSERT_FALSE(isValidDeviceId("1guard"));
    TEST_ASSERT_FALSE(isValidDeviceId("guard--gate"));
    TEST_ASSERT_FALSE(isValidDeviceId("guard-"));
    TEST_ASSERT_FALSE(isValidDeviceId("guard_gate1"));
    TEST_ASSERT_FALSE(isValidDeviceId("abcdefghijklmnopqrstuvwxyz1234567"));
}

static void test_device_topics() {
    TEST_ASSERT_EQUAL_STRING("vs/v1/dev/guard-gate1/status", status("guard-gate1").c_str());
    TEST_ASSERT_EQUAL_STRING("vs/v1/dev/guard-gate1/event/motion", event("guard-gate1", "motion").c_str());
    TEST_ASSERT_EQUAL_STRING("vs/v1/dev/meteo-out1/tele/temperature", tele("meteo-out1", "temperature").c_str());
    TEST_ASSERT_EQUAL_STRING("vs/v1/dev/guard-gate1/cmd/snapshot/ack", cmdAck("guard-gate1", "snapshot").c_str());
    TEST_ASSERT_EQUAL_STRING("vs/v1/dev/guard-gate1/cmd/#", cmdWildcard("guard-gate1").c_str());
    TEST_ASSERT_EQUAL_STRING("vs/v1/hive/#", hiveWildcard().c_str());
}

static void test_segments() {
    TEST_ASSERT_TRUE(isValidSegment("time_sync"));
    TEST_ASSERT_FALSE(isValidSegment("temp/x"));
    TEST_ASSERT_FALSE(isValidSegment("#"));
    TEST_ASSERT_FALSE(isValidSegment(""));
}

int main(int, char**) {
    UNITY_BEGIN();
    RUN_TEST(test_valid_device_ids);
    RUN_TEST(test_invalid_device_ids);
    RUN_TEST(test_device_topics);
    RUN_TEST(test_segments);
    return UNITY_END();
}
