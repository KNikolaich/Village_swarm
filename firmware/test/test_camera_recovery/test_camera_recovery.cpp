#include <unity.h>

#include "../../lib/hornet-core/src/CameraRecovery.h"

using namespace hornet;

void setUp() {}
void tearDown() {}

static void test_first_failure_restarts_at_once() {
    CameraRecovery r(60000);
    TEST_ASSERT_TRUE(r.failed(1000));
    TEST_ASSERT_EQUAL_INT(1, r.failures());
}

static void test_failures_in_a_row_restart_at_most_once_a_minute() {
    CameraRecovery r(60000);
    TEST_ASSERT_TRUE(r.failed(1000));
    TEST_ASSERT_FALSE(r.failed(1700));
    TEST_ASSERT_FALSE(r.failed(60999));
    TEST_ASSERT_TRUE(r.failed(61000));
    TEST_ASSERT_EQUAL_INT(4, r.failures());
}

static void test_a_good_frame_rearms_the_immediate_restart() {
    CameraRecovery r(60000);
    TEST_ASSERT_TRUE(r.failed(1000));
    r.succeeded();
    TEST_ASSERT_EQUAL_INT(0, r.failures());
    TEST_ASSERT_TRUE(r.failed(2000));
}

static void test_millis_wraparound_does_not_block_restarts() {
    CameraRecovery r(60000);
    TEST_ASSERT_TRUE(r.failed(0xFFFFFF00u));
    TEST_ASSERT_FALSE(r.failed(0x00000100u));       // 512 ms later, across the wrap
    TEST_ASSERT_TRUE(r.failed(0xFFFFFF00u + 60000)); // a minute later
}

int main(int, char**) {
    UNITY_BEGIN();
    RUN_TEST(test_first_failure_restarts_at_once);
    RUN_TEST(test_failures_in_a_row_restart_at_most_once_a_minute);
    RUN_TEST(test_a_good_frame_rearms_the_immediate_restart);
    RUN_TEST(test_millis_wraparound_does_not_block_restarts);
    return UNITY_END();
}
