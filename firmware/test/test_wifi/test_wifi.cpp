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

static void test_without_a_spare_the_main_network_is_always_used() {
    for (uint32_t fails = 0; fails < 6; ++fails) TEST_ASSERT_FALSE(useSpareAtBoot(false, fails));
    TEST_ASSERT_FALSE(swapNetworks(false, false, false, kSwapAfterMs * 10, kNoBrokerOnMainMs * 10, false));
    TEST_ASSERT_FALSE(swapNetworks(false, false, true, 0, kNoBrokerOnMainMs * 10, false));
}

static void test_failing_boots_alternate_the_main_and_the_spare_network() {
    TEST_ASSERT_FALSE(useSpareAtBoot(true, 0));  // a normal boot
    TEST_ASSERT_TRUE(useSpareAtBoot(true, 1));   // the last boot never reached the broker
    TEST_ASSERT_FALSE(useSpareAtBoot(true, 2));  // and the one before failed too: back to the main network
    TEST_ASSERT_TRUE(useSpareAtBoot(true, 3));
}

static void test_an_awake_hornet_swaps_networks_only_after_a_long_silence_and_not_during_a_trial() {
    TEST_ASSERT_FALSE(swapNetworks(true, false, false, kSwapAfterMs - 1, kSwapAfterMs - 1, false));
    TEST_ASSERT_TRUE(swapNetworks(true, false, false, kSwapAfterMs, kSwapAfterMs, false));
    TEST_ASSERT_FALSE(swapNetworks(true, false, true, 0, kSwapAfterMs * 2, false));   // Wi-Fi and a short broker gap: wait
    TEST_ASSERT_FALSE(swapNetworks(true, true, false, kSwapAfterMs * 2, kNoBrokerOnMainMs * 2, false));  // a trial runs its own clock
}

static void test_wifi_without_the_broker_is_left_sooner_on_the_spare_than_on_the_main_network() {
    // The spare joins Wi-Fi but does not lead to the hive (an old guest network): back to the main one.
    TEST_ASSERT_FALSE(swapNetworks(true, false, true, 0, kNoBrokerOnSpareMs - 1, true));
    TEST_ASSERT_TRUE(swapNetworks(true, false, true, 0, kNoBrokerOnSpareMs, true));
    // On the main network the hive itself may be restarting: the spare is tried only after a longer wait.
    TEST_ASSERT_FALSE(swapNetworks(true, false, true, 0, kNoBrokerOnSpareMs, false));
    TEST_ASSERT_TRUE(swapNetworks(true, false, true, 0, kNoBrokerOnMainMs, false));
}

static void test_only_a_spare_that_reached_the_broker_becomes_the_main_network() {
    TEST_ASSERT_TRUE(promoteSpare(true, true));
    TEST_ASSERT_FALSE(promoteSpare(true, false));   // Wi-Fi up but no broker: not proven
    TEST_ASSERT_FALSE(promoteSpare(false, true));   // the main network works: leave it
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_credentials_follow_the_contract);
    RUN_TEST(test_no_change_in_progress_means_nothing_to_decide);
    RUN_TEST(test_reaching_the_broker_commits_at_once);
    RUN_TEST(test_waiting_until_the_time_is_up_then_rolling_back);
    RUN_TEST(test_a_deep_sleeper_gives_up_before_its_awake_limit);
    RUN_TEST(test_the_same_network_is_not_a_change);
    RUN_TEST(test_without_a_spare_the_main_network_is_always_used);
    RUN_TEST(test_failing_boots_alternate_the_main_and_the_spare_network);
    RUN_TEST(test_an_awake_hornet_swaps_networks_only_after_a_long_silence_and_not_during_a_trial);
    RUN_TEST(test_wifi_without_the_broker_is_left_sooner_on_the_spare_than_on_the_main_network);
    RUN_TEST(test_only_a_spare_that_reached_the_broker_becomes_the_main_network);
    return UNITY_END();
}
