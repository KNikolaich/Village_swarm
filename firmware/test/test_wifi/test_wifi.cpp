#include <unity.h>

#include <string>

#include "../../lib/hornet-core/src/WifiChange.h"

using namespace hornet::wifichange;

void setUp() {}
void tearDown() {}

static void test_credentials_follow_the_contract() {
    TEST_ASSERT_TRUE(validCredentials("village-iot", "correct horse"));
    TEST_ASSERT_TRUE(validCredentials("open-network", ""));            // open network
    TEST_ASSERT_TRUE(validCredentials(std::string(32, 'a'), std::string(63, 'p')));
    TEST_ASSERT_FALSE(validCredentials("", "password123"));            // no name
    TEST_ASSERT_FALSE(validCredentials(std::string(33, 'a'), "password123"));
    TEST_ASSERT_FALSE(validCredentials("village-iot", "short"));       // WPA needs 8
    TEST_ASSERT_FALSE(validCredentials("village-iot", std::string(64, 'p')));
}

static void test_no_change_in_progress_means_nothing_to_decide() {
    TEST_ASSERT_EQUAL(Trial::None, decide(false, true, 1000, false));
    TEST_ASSERT_EQUAL(Trial::None, decide(false, false, 999999, false));
}

static void test_reaching_the_broker_commits_at_once() {
    TEST_ASSERT_EQUAL(Trial::Commit, decide(true, true, 4000, false));
    TEST_ASSERT_EQUAL(Trial::Commit, decide(true, true, kTrialMs + 5000, false));  // late but there: keep it
}

static void test_waiting_until_the_time_is_up_then_rolling_back() {
    TEST_ASSERT_EQUAL(Trial::Waiting, decide(true, false, kTrialMs - 1, false));
    TEST_ASSERT_EQUAL(Trial::RollBack, decide(true, false, kTrialMs, false));
}

static void test_a_deep_sleeper_gives_up_before_its_awake_limit() {
    TEST_ASSERT_TRUE(kSleepyTrialMs < 30000);  // kAwakeLimitMs in HornetCore.cpp
    TEST_ASSERT_EQUAL(Trial::Waiting, decide(true, false, kSleepyTrialMs - 1, true));
    TEST_ASSERT_EQUAL(Trial::RollBack, decide(true, false, kSleepyTrialMs, true));
}

static void test_the_same_network_is_not_a_change() {
    TEST_ASSERT_TRUE(sameAsCurrent("a", "pass1234", "a", "pass1234"));
    TEST_ASSERT_FALSE(sameAsCurrent("a", "pass1234", "a", "other-pass"));
    TEST_ASSERT_FALSE(sameAsCurrent("b", "pass1234", "a", "pass1234"));
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_credentials_follow_the_contract);
    RUN_TEST(test_no_change_in_progress_means_nothing_to_decide);
    RUN_TEST(test_reaching_the_broker_commits_at_once);
    RUN_TEST(test_waiting_until_the_time_is_up_then_rolling_back);
    RUN_TEST(test_a_deep_sleeper_gives_up_before_its_awake_limit);
    RUN_TEST(test_the_same_network_is_not_a_change);
    return UNITY_END();
}
