#include <unity.h>

#include <cmath>

#include "../../lib/hornet-core/src/LowVoltageCutoff.h"

using hornet::LowVoltageCutoff;
using Event = LowVoltageCutoff::Event;

void setUp() {}
void tearDown() {}

// Feeds `volts` once a second for `seconds`; returns the last event that was not None.
static Event feed(LowVoltageCutoff& l, uint32_t& nowMs, float volts, int seconds) {
    Event last = Event::None;
    for (int i = 0; i < seconds; ++i) {
        nowMs += 1000;
        const Event e = l.update(nowMs, volts);
        if (e != Event::None) last = e;
    }
    return last;
}

static void test_the_load_is_off_at_boot_and_comes_on_when_the_battery_is_good() {
    LowVoltageCutoff l;
    uint32_t now = 0;
    TEST_ASSERT_FALSE(l.connected());
    TEST_ASSERT_EQUAL(Event::None, feed(l, now, 12.8F, 4));
    TEST_ASSERT_FALSE(l.connected());
    TEST_ASSERT_EQUAL(Event::Restored, feed(l, now, 12.8F, 2));  // 5 s in a row
    TEST_ASSERT_TRUE(l.connected());
}

static void test_a_weak_battery_at_boot_keeps_the_load_off() {
    LowVoltageCutoff l;
    uint32_t now = 0;
    feed(l, now, 11.9F, 3600);
    TEST_ASSERT_FALSE(l.connected());
}

static void test_a_short_dip_does_not_cut_a_long_one_does() {
    LowVoltageCutoff l;
    uint32_t now = 0;
    feed(l, now, 12.8F, 10);
    TEST_ASSERT_TRUE(l.connected());
    feed(l, now, 10.0F, 20);  // an inverter starting
    TEST_ASSERT_TRUE(l.connected());
    feed(l, now, 12.0F, 1);   // recovered: the timer starts over
    feed(l, now, 10.0F, 20);
    TEST_ASSERT_TRUE(l.connected());
    TEST_ASSERT_EQUAL(Event::Cut, feed(l, now, 10.0F, 12));  // 30 s in a row
    TEST_ASSERT_FALSE(l.connected());
}

static void test_it_holds_between_the_two_limits() {
    LowVoltageCutoff l;
    uint32_t now = 0;
    feed(l, now, 12.8F, 10);
    feed(l, now, 10.6F, 3600);  // low, but not below the cutoff
    TEST_ASSERT_TRUE(l.connected());
}

static void test_after_a_cut_the_rebound_alone_does_not_bring_the_load_back() {
    LowVoltageCutoff l;
    uint32_t now = 0;
    feed(l, now, 12.8F, 10);
    feed(l, now, 10.0F, 40);
    TEST_ASSERT_FALSE(l.connected());
    feed(l, now, 12.0F, 3600);  // springs back to 12 V, still below 12.4
    TEST_ASSERT_FALSE(l.connected());
    TEST_ASSERT_EQUAL(Event::None, feed(l, now, 13.2F, 100));  // the charger is back, 120 s are needed
    TEST_ASSERT_FALSE(l.connected());
    TEST_ASSERT_EQUAL(Event::Restored, feed(l, now, 13.2F, 21));
    TEST_ASSERT_TRUE(l.connected());
}

static void test_a_blip_above_the_restore_limit_does_not_restore() {
    LowVoltageCutoff l;
    uint32_t now = 0;
    feed(l, now, 12.8F, 10);
    feed(l, now, 10.0F, 40);
    feed(l, now, 13.0F, 100);
    feed(l, now, 11.0F, 1);  // the timer starts over
    feed(l, now, 13.0F, 100);
    TEST_ASSERT_FALSE(l.connected());
}

static void test_no_reading_for_long_cuts_the_load() {
    LowVoltageCutoff l;
    uint32_t now = 0;
    feed(l, now, 12.8F, 10);
    TEST_ASSERT_EQUAL(Event::None, feed(l, now, NAN, 100));
    TEST_ASSERT_TRUE(l.connected());
    TEST_ASSERT_EQUAL(Event::Lost, feed(l, now, NAN, 30));  // 120 s without a reading
    TEST_ASSERT_FALSE(l.connected());
}

static void test_a_missing_reading_while_off_does_nothing() {
    LowVoltageCutoff l;
    uint32_t now = 0;
    TEST_ASSERT_EQUAL(Event::None, feed(l, now, NAN, 600));
    TEST_ASSERT_FALSE(l.connected());
}

static void test_configured_limits_apply_and_restore_stays_above_cutoff() {
    LowVoltageCutoff l;
    LowVoltageCutoff::Config c;
    c.cutoffV = 11.0F;
    c.restoreV = 10.0F;  // nonsense: below the cutoff
    l.configure(c);
    TEST_ASSERT_FLOAT_WITHIN(0.001F, 11.3F, l.config().restoreV);
    uint32_t now = 0;
    feed(l, now, 12.8F, 10);
    TEST_ASSERT_EQUAL(Event::Cut, feed(l, now, 10.9F, 31));
}

static void test_restart_puts_the_load_off() {
    LowVoltageCutoff l;
    uint32_t now = 0;
    feed(l, now, 12.8F, 10);
    TEST_ASSERT_TRUE(l.connected());
    l.restart();
    TEST_ASSERT_FALSE(l.connected());
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_the_load_is_off_at_boot_and_comes_on_when_the_battery_is_good);
    RUN_TEST(test_a_weak_battery_at_boot_keeps_the_load_off);
    RUN_TEST(test_a_short_dip_does_not_cut_a_long_one_does);
    RUN_TEST(test_it_holds_between_the_two_limits);
    RUN_TEST(test_after_a_cut_the_rebound_alone_does_not_bring_the_load_back);
    RUN_TEST(test_a_blip_above_the_restore_limit_does_not_restore);
    RUN_TEST(test_no_reading_for_long_cuts_the_load);
    RUN_TEST(test_a_missing_reading_while_off_does_nothing);
    RUN_TEST(test_configured_limits_apply_and_restore_stays_above_cutoff);
    RUN_TEST(test_restart_puts_the_load_off);
    return UNITY_END();
}
