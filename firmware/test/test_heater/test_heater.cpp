#include <unity.h>

#include <cmath>

#include "../../lib/hornet-core/src/Features.h"
#include "../../lib/hornet-core/src/HeaterFailsafe.h"

// The same rules as the simulator's reference model (backend/tools/Hive.Simulator/Roles/HeaterFailsafe.cs).

using hornet::Features;
using hornet::HeaterFailsafe;

void setUp() {}
void tearDown() {}

static constexpr uint32_t kMin = 60000UL;

static HeaterFailsafe make(float t, uint32_t maxOnS = 21600) {
    HeaterFailsafe f;
    f.configure(5, 25, maxOnS, 3 * kMin);
    f.reading(0, t);
    f.evaluate(0);
    return f;
}

static void test_follows_commands_between_limits() {
    HeaterFailsafe f = make(18);
    TEST_ASSERT_FALSE(f.isOn());
    TEST_ASSERT_NULL(f.command(true, 0, 1000));
    TEST_ASSERT_TRUE(f.isOn());
    f.reading(2000, 18);
    TEST_ASSERT_FALSE(f.evaluate(2000));
    TEST_ASSERT_NULL(f.command(false, 0, 3000));
    TEST_ASSERT_FALSE(f.isOn());
}

static void test_for_s_switches_off() {
    HeaterFailsafe f = make(18);
    TEST_ASSERT_NULL(f.command(true, 60, 0));
    f.reading(59000, 18);
    TEST_ASSERT_FALSE(f.evaluate(59999));
    f.reading(60000, 18);
    TEST_ASSERT_TRUE(f.evaluate(60000));
    TEST_ASSERT_FALSE(f.isOn());
    TEST_ASSERT_EQUAL_STRING("idle", f.lastReason());
}

static void test_below_min_heats_whatever_the_commands_with_hysteresis() {
    HeaterFailsafe f = make(4.9F);
    TEST_ASSERT_TRUE(f.isOn());
    TEST_ASSERT_EQUAL_STRING("min_c", f.lastReason());
    TEST_ASSERT_EQUAL_STRING("failsafe: below min_c", f.command(false, 0, 1000));
    TEST_ASSERT_TRUE(f.isOn());
    f.reading(2000, 5.9F);  // above min_c, inside the hysteresis: keeps heating
    TEST_ASSERT_FALSE(f.evaluate(2000));
    TEST_ASSERT_TRUE(f.isOn());
    f.reading(3000, 6.0F);
    TEST_ASSERT_TRUE(f.evaluate(3000));
    TEST_ASSERT_FALSE(f.isOn());
}

static void test_above_max_switches_off_and_rejects_on() {
    HeaterFailsafe f = make(20);
    TEST_ASSERT_NULL(f.command(true, 0, 0));
    f.reading(1000, 25.1F);
    TEST_ASSERT_TRUE(f.evaluate(1000));
    TEST_ASSERT_FALSE(f.isOn());
    TEST_ASSERT_EQUAL_STRING("max_c", f.lastReason());
    f.reading(2000, 25.0F);
    TEST_ASSERT_EQUAL_STRING("failsafe: max_c exceeded", f.command(true, 0, 2000));
    f.reading(3000, 24.0F);  // cooled down: the dropped command does not come back by itself
    f.evaluate(3000);
    TEST_ASSERT_FALSE(f.isOn());
    TEST_ASSERT_NULL(f.command(true, 0, 3000));
}

static void test_max_on_cuts_off_and_rests() {
    HeaterFailsafe f = make(18, 600);
    TEST_ASSERT_NULL(f.command(true, 0, 0));  // no for_s: max_on_s still applies
    for (uint32_t t = 60000; t < 600000; t += 60000) {
        f.reading(t, 18);
        f.evaluate(t);
    }
    TEST_ASSERT_TRUE(f.isOn());
    f.reading(600000, 18);
    TEST_ASSERT_TRUE(f.evaluate(600000));
    TEST_ASSERT_FALSE(f.isOn());
    TEST_ASSERT_EQUAL_STRING("max_on_s", f.lastReason());
    TEST_ASSERT_EQUAL_STRING("failsafe: resting after max_on_s", f.command(true, 0, 630000));
    TEST_ASSERT_NULL(f.command(true, 0, 660000));
}

static void test_forced_heating_is_cut_by_max_on_too() {
    HeaterFailsafe f = make(3, 600);
    TEST_ASSERT_TRUE(f.isOn());
    f.reading(600000, 3);
    TEST_ASSERT_TRUE(f.evaluate(600000));
    TEST_ASSERT_FALSE(f.isOn());  // resting
    f.reading(630000, 3);
    TEST_ASSERT_FALSE(f.evaluate(630000));
    f.reading(660000, 3);
    TEST_ASSERT_TRUE(f.evaluate(660000));
    TEST_ASSERT_TRUE(f.isOn());
}

static void test_lost_sensor_runs_antifreeze_cycle() {
    HeaterFailsafe f = make(18);
    TEST_ASSERT_FALSE(f.evaluate(3 * kMin - 1));
    TEST_ASSERT_TRUE(f.evaluate(3 * kMin));  // nothing read for 3 minutes
    TEST_ASSERT_TRUE(f.isOn());
    TEST_ASSERT_EQUAL_STRING("sensor_lost", f.lastReason());
    TEST_ASSERT_EQUAL_STRING("failsafe: temperature sensor lost", f.command(true, 0, 4 * kMin));
    TEST_ASSERT_FALSE(f.evaluate(13 * kMin - 1));
    TEST_ASSERT_TRUE(f.evaluate(13 * kMin));  // 10 min on, then 20 off
    TEST_ASSERT_FALSE(f.isOn());
    TEST_ASSERT_TRUE(f.evaluate(33 * kMin));
    TEST_ASSERT_TRUE(f.isOn());
    f.reading(34 * kMin, 18);  // the sensor is back: commands rule again (none: off)
    TEST_ASSERT_TRUE(f.evaluate(34 * kMin));
    TEST_ASSERT_FALSE(f.isOn());
}

static void test_failed_reads_do_not_count() {
    HeaterFailsafe f;
    f.configure(5, 25, 21600, 3 * kMin);
    f.evaluate(1000);
    f.reading(2000, NAN);
    TEST_ASSERT_FALSE(f.evaluate(3 * kMin + 999));
    TEST_ASSERT_TRUE(f.evaluate(3 * kMin + 1000));
    TEST_ASSERT_EQUAL_STRING("sensor_lost", f.lastReason());
}

static void test_features_heater_implies_relay() {
    Features f{hornet::kHeater | hornet::kThermometer};
    Features n = f.normalized();
    TEST_ASSERT_TRUE(n.has(hornet::kRelay));
    TEST_ASSERT_TRUE(n.has(hornet::kThermometer));
    TEST_ASSERT_FALSE(n.has(hornet::kBme280));
    TEST_ASSERT_EQUAL_UINT8(hornet::kBme280, Features::parseOne("bme280"));
    TEST_ASSERT_EQUAL_UINT8(0, Features::parseOne("camera"));
    TEST_ASSERT_EQUAL_UINT8(0, Features::parseOne(nullptr));
}

int main(int, char**) {
    UNITY_BEGIN();
    RUN_TEST(test_follows_commands_between_limits);
    RUN_TEST(test_for_s_switches_off);
    RUN_TEST(test_below_min_heats_whatever_the_commands_with_hysteresis);
    RUN_TEST(test_above_max_switches_off_and_rejects_on);
    RUN_TEST(test_max_on_cuts_off_and_rests);
    RUN_TEST(test_forced_heating_is_cut_by_max_on_too);
    RUN_TEST(test_lost_sensor_runs_antifreeze_cycle);
    RUN_TEST(test_failed_reads_do_not_count);
    RUN_TEST(test_features_heater_implies_relay);
    return UNITY_END();
}
