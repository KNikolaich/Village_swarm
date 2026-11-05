#include <unity.h>

#include "../../lib/hornet-core/src/BrokerFailover.h"

using namespace hornet::failover;

void setUp() {}
void tearDown() {}

static const uint32_t kSec = 1000;
static const uint32_t kMin = 60 * kSec;

static void test_without_a_city_broker_it_stays_home_whatever_happens() {
    State s;
    TEST_ASSERT_EQUAL(Broker::Home, step(s, 0, true, false, false));
    TEST_ASSERT_EQUAL(Broker::Home, step(s, 10 * kMin, true, false, false));
    TEST_ASSERT_EQUAL(Broker::Home, step(s, 60 * kMin, true, false, false));
}

static void test_home_that_stays_silent_for_three_minutes_sends_it_to_the_city() {
    State s;
    s.sinceMs = 1000;
    TEST_ASSERT_EQUAL(Broker::Home, step(s, 1000 + 179 * kSec, true, false, true));
    TEST_ASSERT_EQUAL(Broker::City, step(s, 1000 + 180 * kSec, true, false, true));
}

static void test_a_connection_to_home_starts_the_count_again() {
    State s;
    s.sinceMs = 0;
    step(s, 100 * kSec, true, false, true);
    step(s, 120 * kSec, true, true, true);   // home answered: the silence is over
    TEST_ASSERT_EQUAL(Broker::Home, step(s, 290 * kSec, true, false, true));   // only 170 s since
    TEST_ASSERT_EQUAL(Broker::City, step(s, 300 * kSec, true, false, true));
}

static void test_a_lost_wifi_is_not_home_being_down() {
    State s;
    s.sinceMs = 0;
    for (uint32_t t = 0; t < 20 * kMin; t += 10 * kSec) TEST_ASSERT_EQUAL(Broker::Home, step(s, t, false, false, true));
    // the Wi-Fi is back: the three minutes start now, not twenty minutes ago
    TEST_ASSERT_EQUAL(Broker::Home, step(s, 20 * kMin, true, false, true));
    TEST_ASSERT_EQUAL(Broker::Home, step(s, 20 * kMin + 160 * kSec, true, false, true));
    TEST_ASSERT_EQUAL(Broker::City, step(s, 20 * kMin + 190 * kSec, true, false, true));
}

static void test_on_the_city_it_looks_at_home_every_five_minutes_and_comes_back_when_home_answers() {
    State s;
    s.broker = Broker::City;
    s.sinceMs = 0;
    TEST_ASSERT_EQUAL(Broker::City, step(s, 299 * kSec, true, true, true));
    TEST_ASSERT_EQUAL(Broker::Home, step(s, 300 * kSec, true, false, true));   // the probe begins on home
    TEST_ASSERT_TRUE(s.probing);
    TEST_ASSERT_EQUAL(Broker::Home, step(s, 310 * kSec, true, true, true));   // home answers
    TEST_ASSERT_FALSE(s.probing);
    TEST_ASSERT_EQUAL(Broker::Home, step(s, 400 * kSec, true, true, true));   // and it stays
}

static void test_a_probe_that_gets_no_answer_goes_back_to_the_city_and_waits_five_more_minutes() {
    State s;
    s.broker = Broker::City;
    s.sinceMs = 0;
    step(s, 300 * kSec, true, false, true);
    TEST_ASSERT_EQUAL(Broker::Home, step(s, 300 * kSec + 24 * kSec, true, false, true));
    TEST_ASSERT_EQUAL(Broker::City, step(s, 300 * kSec + 25 * kSec, true, false, true));
    TEST_ASSERT_FALSE(s.probing);
    TEST_ASSERT_EQUAL(Broker::City, step(s, 300 * kSec + 25 * kSec + 299 * kSec, true, true, true));
    TEST_ASSERT_EQUAL(Broker::Home, step(s, 300 * kSec + 25 * kSec + 300 * kSec, true, true, true));
}

static void test_the_clock_wrapping_around_does_not_confuse_it() {
    State s;
    s.sinceMs = 0xFFFFFFFFu - 60 * kSec;
    const uint32_t since = s.sinceMs;
    TEST_ASSERT_EQUAL(Broker::Home, step(s, since + 179 * kSec, true, false, true));   // 179 s later, already past the wrap
    TEST_ASSERT_EQUAL(Broker::City, step(s, since + 180 * kSec, true, false, true));
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_without_a_city_broker_it_stays_home_whatever_happens);
    RUN_TEST(test_home_that_stays_silent_for_three_minutes_sends_it_to_the_city);
    RUN_TEST(test_a_connection_to_home_starts_the_count_again);
    RUN_TEST(test_a_lost_wifi_is_not_home_being_down);
    RUN_TEST(test_on_the_city_it_looks_at_home_every_five_minutes_and_comes_back_when_home_answers);
    RUN_TEST(test_a_probe_that_gets_no_answer_goes_back_to_the_city_and_waits_five_more_minutes);
    RUN_TEST(test_the_clock_wrapping_around_does_not_confuse_it);
    return UNITY_END();
}
