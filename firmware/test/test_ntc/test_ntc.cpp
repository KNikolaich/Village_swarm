#include <unity.h>

#include <cmath>
#include <initializer_list>

#include "../../lib/hornet-core/src/Thermistor.h"

using namespace hornet::ntc;

void setUp() {}
void tearDown() {}

// The pin voltage a 10k/B3950 NTC gives at t (C): the formula of the docs, solved the other way round.
static float pinMv(const Params& p, float t) {
    const float r = p.rNominal * std::exp(p.beta * (1.0F / (t + kKelvin) - 1.0F / kNominalK));
    const float vcc = p.vcc * 1000.0F;
    return p.side == Side::High ? vcc * p.rSeries / (p.rSeries + r) : vcc * r / (p.rSeries + r);
}

static void test_half_supply_is_25_degrees_on_either_side() {
    Params p;
    TEST_ASSERT_FLOAT_WITHIN(0.01F, 25.0F, celsiusFromMv(p, 1650.0F));
    p.side = Side::Low;
    TEST_ASSERT_FLOAT_WITHIN(0.01F, 25.0F, celsiusFromMv(p, 1650.0F));
}

static void test_known_points_round_trip() {
    for (Side side : {Side::High, Side::Low}) {
        Params p;
        p.side = side;
        for (float t : {-30.0F, 0.0F, 21.5F, 60.0F, 100.0F})
            TEST_ASSERT_FLOAT_WITHIN(0.05F, t, celsiusFromMv(p, pinMv(p, t)));
    }
}

static void test_high_side_voltage_rises_with_temperature() {
    Params p;  // NTC to 3V3: cold is low on the pin, far from the C3 ADC's 2.5 V ceiling
    TEST_ASSERT_TRUE(pinMv(p, -30.0F) < pinMv(p, 0.0F));
    TEST_ASSERT_TRUE(pinMv(p, -30.0F) > 100.0F);
    TEST_ASSERT_TRUE(pinMv(p, 40.0F) < 2500.0F);
    // 0 C: the NTC is ~33.6k, 3300 * 10 / 43.6 = ~757 mV
    TEST_ASSERT_FLOAT_WITHIN(3.0F, 757.0F, pinMv(p, 0.0F));
}

static void test_rails_mean_a_broken_divider() {
    Params p;
    TEST_ASSERT_TRUE(std::isnan(celsiusFromMv(p, 0.0F)));
    TEST_ASSERT_TRUE(std::isnan(celsiusFromMv(p, 3300.0F)));
    TEST_ASSERT_TRUE(std::isnan(celsiusFromMv(p, NAN)));
}

static void test_calibration_moves_the_result() {
    Params p;
    p.offset = -0.7F;
    TEST_ASSERT_FLOAT_WITHIN(0.01F, 24.3F, celsiusFromMv(p, 1650.0F));
    // A probe that measures 10.5k at 25 C reads 25 C at the voltage it gives there.
    p.offset = 0;
    p.rNominal = 10500.0F;
    TEST_ASSERT_FLOAT_WITHIN(0.01F, 25.0F, celsius(p, 10500.0F));
    // And the 10k resistor that is really 9.9k: half supply means the NTC is 9.9k too.
    p.rNominal = 10000.0F;
    p.rSeries = 9900.0F;
    TEST_ASSERT_FLOAT_WITHIN(1.0F, 9900.0F, resistance(p, 1650.0F));
}

static void test_side_names() {
    TEST_ASSERT_TRUE(parseSide("low") == Side::Low);
    TEST_ASSERT_TRUE(parseSide("high") == Side::High);
    TEST_ASSERT_TRUE(parseSide(nullptr) == Side::High);
    TEST_ASSERT_TRUE(parseSide("sideways", Side::Low) == Side::Low);
}

static void test_median_ignores_a_spike() {
    float v[9] = {1650, 1652, 1649, 3100, 1651, 1650, 20, 1648, 1653};
    TEST_ASSERT_EQUAL_FLOAT(1650.0F, median(v, 9));
    float even[4] = {4, 1, 3, 2};
    TEST_ASSERT_EQUAL_FLOAT(2.5F, median(even, 4));
}

static void test_smoother_follows_slowly_and_jumps_on_a_real_change() {
    Smoother s(0.2F);
    TEST_ASSERT_FALSE(s.has());
    s.add(NAN);
    TEST_ASSERT_FALSE(s.has());
    s.add(20.0F);
    TEST_ASSERT_EQUAL_FLOAT(20.0F, s.value());
    s.add(21.0F);  // a degree of noise moves it by a fifth
    TEST_ASSERT_FLOAT_WITHIN(0.001F, 20.2F, s.value());
    s.add(60.0F);  // one wild sample is dropped
    TEST_ASSERT_FLOAT_WITHIN(0.001F, 20.2F, s.value());
    s.add(20.2F);
    s.add(60.0F);
    s.add(61.0F);  // twice in a row: the probe really is in hot water
    TEST_ASSERT_EQUAL_FLOAT(61.0F, s.value());
    s.reset();
    TEST_ASSERT_TRUE(std::isnan(s.value()));
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_half_supply_is_25_degrees_on_either_side);
    RUN_TEST(test_known_points_round_trip);
    RUN_TEST(test_high_side_voltage_rises_with_temperature);
    RUN_TEST(test_rails_mean_a_broken_divider);
    RUN_TEST(test_calibration_moves_the_result);
    RUN_TEST(test_side_names);
    RUN_TEST(test_median_ignores_a_spike);
    RUN_TEST(test_smoother_follows_slowly_and_jumps_on_a_real_change);
    return UNITY_END();
}
