#include <unity.h>

#include <cmath>

#include "../../lib/hornet-core/src/PowerMeter.h"

using namespace hornet::power;

void setUp() {}
void tearDown() {}

static void test_bus_voltage_is_1_25_mv_per_step() {
    TEST_ASSERT_FLOAT_WITHIN(0.0001F, 12.6F, busVolts(10080));  // 12.6 V
    TEST_ASSERT_FLOAT_WITHIN(0.0001F, 14.4F, busVolts(11520));
}

static void test_current_follows_the_shunt() {
    // 2 mOhm: 2 A drops 4 mV = 1600 steps of 2.5 uV
    TEST_ASSERT_FLOAT_WITHIN(0.001F, 2.0F, amps(1600, 2.0F));
    // 10 mOhm: 2 A drops 20 mV = 8000 steps
    TEST_ASSERT_FLOAT_WITHIN(0.001F, 2.0F, amps(8000, 10.0F));
    // charging reads negative, and the wires swapped (invert) turn it round
    TEST_ASSERT_FLOAT_WITHIN(0.001F, -4.0F, amps(-3200, 2.0F));
    TEST_ASSERT_FLOAT_WITHIN(0.001F, 4.0F, amps(-3200, 2.0F, true));
}

static void test_a_missing_shunt_is_not_a_number() {
    TEST_ASSERT_TRUE(std::isnan(amps(1000, 0.0F)));
    TEST_ASSERT_TRUE(std::isnan(amps(1000, -1.0F)));
}

static void test_window_is_the_mean_of_its_samples() {
    Window w;
    TEST_ASSERT_TRUE(w.empty());
    TEST_ASSERT_TRUE(std::isnan(w.mean()));
    w.add(1.0F);
    w.add(2.0F);
    w.add(6.0F);
    TEST_ASSERT_FLOAT_WITHIN(0.0001F, 3.0F, w.mean());
    w.take();
    TEST_ASSERT_TRUE(w.empty());
}

static void test_two_amps_for_an_hour_is_two_ah() {
    ChargeCounter c;
    for (int i = 0; i < 3600; ++i) c.update(12.2F, 2.0F, 1.0F);
    TEST_ASSERT_FLOAT_WITHIN(0.01F, 2.0F, c.ah());
}

static void test_charge_gives_back_less_and_stops_at_zero() {
    ChargeCounter c;
    c.set(1.0F);
    for (int i = 0; i < 3600; ++i) c.update(13.5F, -1.0F, 1.0F);  // 1 Ah in, 0.9 counted
    TEST_ASSERT_FLOAT_WITHIN(0.01F, 0.1F, c.ah());
    for (int i = 0; i < 3600; ++i) c.update(13.5F, -1.0F, 1.0F);
    TEST_ASSERT_EQUAL_FLOAT(0.0F, c.ah());
}

static void test_full_needs_voltage_and_a_trickle_for_a_while() {
    ChargeCounter c;
    c.configure(14.0F, 0.5F, 600.0F);
    c.set(3.0F);
    // 14.4 V but 3 A still going in: not full, however long it takes
    for (int i = 0; i < 1000; ++i) c.update(14.4F, -3.0F, 1.0F);
    TEST_ASSERT_TRUE(c.ah() < 3.0F);
    TEST_ASSERT_TRUE(c.ah() > 0.0F);
    // the current tapers to 0.2 A: after 600 s in a row the counter is reset
    bool reset = false;
    for (int i = 0; i < 599; ++i) reset |= c.update(14.4F, -0.2F, 1.0F);
    TEST_ASSERT_FALSE(reset);
    reset = c.update(14.4F, -0.2F, 1.0F);
    TEST_ASSERT_TRUE(reset);
    TEST_ASSERT_EQUAL_FLOAT(0.0F, c.ah());
}

static void test_a_discharge_blip_restarts_the_full_timer() {
    ChargeCounter c;
    c.configure(14.0F, 0.5F, 100.0F);
    c.set(2.0F);
    for (int i = 0; i < 90; ++i) c.update(14.2F, 0.1F, 1.0F);
    c.update(12.0F, 2.0F, 1.0F);  // the mains went off for a second
    bool reset = false;
    for (int i = 0; i < 90; ++i) reset |= c.update(14.2F, 0.1F, 1.0F);
    TEST_ASSERT_FALSE(reset);
}

static void test_bad_steps_are_ignored() {
    ChargeCounter c;
    c.update(12.0F, 2.0F, 600.0F);  // a long pause (the hornet was stuck): no guess
    c.update(NAN, 2.0F, 1.0F);
    c.update(12.0F, NAN, 1.0F);
    TEST_ASSERT_EQUAL_FLOAT(0.0F, c.ah());
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_bus_voltage_is_1_25_mv_per_step);
    RUN_TEST(test_current_follows_the_shunt);
    RUN_TEST(test_a_missing_shunt_is_not_a_number);
    RUN_TEST(test_window_is_the_mean_of_its_samples);
    RUN_TEST(test_two_amps_for_an_hour_is_two_ah);
    RUN_TEST(test_charge_gives_back_less_and_stops_at_zero);
    RUN_TEST(test_full_needs_voltage_and_a_trickle_for_a_while);
    RUN_TEST(test_a_discharge_blip_restarts_the_full_timer);
    RUN_TEST(test_bad_steps_are_ignored);
    return UNITY_END();
}
