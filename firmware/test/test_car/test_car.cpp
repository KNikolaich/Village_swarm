#include <unity.h>

#include "../../lib/hornet-core/src/CarDrive.h"

using namespace hornet;

void setUp() {}
void tearDown() {}

static void test_pins_are_the_free_ones_and_all_different() {
    const int defaults[4] = {14, 15, 13, 12};
    TEST_ASSERT_TRUE(carPinsValid(defaults, 4));
    const int camera[4] = {14, 15, 13, 0};   // XCLK of the camera
    TEST_ASSERT_FALSE(carPinsValid(camera, 4));
    const int psram[4] = {14, 15, 13, 16};
    TEST_ASSERT_FALSE(carPinsValid(psram, 4));
    const int twice[4] = {14, 15, 13, 13};
    TEST_ASSERT_FALSE(carPinsValid(twice, 4));
    TEST_ASSERT_FALSE(carPinsValid(defaults, 12));  // the light on a motor pin
    TEST_ASSERT_FALSE(carPinAllowed(1));            // the serial console
}

static void test_forward_drives_the_first_input_back_the_second() {
    CarMount m;
    MotorDuty d = carDuty(100, -100, m);
    TEST_ASSERT_EQUAL_UINT8(255, d.in[0]);
    TEST_ASSERT_EQUAL_UINT8(0, d.in[1]);
    TEST_ASSERT_EQUAL_UINT8(0, d.in[2]);
    TEST_ASSERT_EQUAL_UINT8(255, d.in[3]);
    d = carDuty(0, 0, m);
    for (int i = 0; i < 4; ++i) TEST_ASSERT_EQUAL_UINT8(0, d.in[i]);
}

static void test_speed_scales_by_the_limit_and_pwm_off_is_full() {
    CarMount m;
    m.maxSpeed = 50;
    TEST_ASSERT_EQUAL_UINT8(128, carDuty(100, 0, m).in[0]);
    TEST_ASSERT_EQUAL_UINT8(64, carDuty(50, 0, m).in[0]);
    TEST_ASSERT_EQUAL_UINT8(1, carDuty(1, 0, m).in[0] > 0 ? 1 : 0);
    m.pwm = false;
    TEST_ASSERT_EQUAL_UINT8(255, carDuty(10, 0, m).in[0]);
    TEST_ASSERT_EQUAL_UINT8(255, carDuty(500, 0, m).in[0]);  // clamped, never more than full
}

static void test_mounting_swaps_and_inverts() {
    CarMount m;
    m.invertLeft = true;
    MotorDuty d = carDuty(100, 100, m);
    TEST_ASSERT_EQUAL_UINT8(0, d.in[0]);
    TEST_ASSERT_EQUAL_UINT8(255, d.in[1]);
    TEST_ASSERT_EQUAL_UINT8(255, d.in[2]);
    m = CarMount();
    m.swapSides = true;
    d = carDuty(100, -50, m);  // the right wheels on IN1/IN2 now
    TEST_ASSERT_EQUAL_UINT8(0, d.in[0]);
    TEST_ASSERT_TRUE(d.in[1] > 0);
    TEST_ASSERT_EQUAL_UINT8(255, d.in[2]);
}

static void test_the_car_stops_500_ms_after_the_last_drive() {
    CarGuard g;
    TEST_ASSERT_EQUAL(CarStop::Boot, g.lastStop());
    TEST_ASSERT_FALSE(g.moving());
    TEST_ASSERT_TRUE(g.drive(60, 60, 1000));
    TEST_ASSERT_EQUAL(CarStop::None, g.tick(1499, true));
    TEST_ASSERT_TRUE(g.drive(60, 60, 1400));  // the repeat of a held control
    TEST_ASSERT_EQUAL(CarStop::None, g.tick(1899, true));
    TEST_ASSERT_EQUAL(CarStop::Deadman, g.tick(1900, true));
    TEST_ASSERT_FALSE(g.moving());
    TEST_ASSERT_EQUAL(0, g.left());
}

static void test_a_lost_link_stops_at_once() {
    CarGuard g;
    g.drive(50, 50, 1000);
    TEST_ASSERT_EQUAL(CarStop::Link, g.tick(1010, false));
    TEST_ASSERT_FALSE(g.moving());
}

static void test_the_max_run_holds_the_car_until_a_stop_comes() {
    CarGuard g;
    g.setMaxRunMs(2000);
    uint32_t now = 1000;
    g.drive(50, 50, now);
    for (; now < 3000; now += 200) {
        g.drive(50, 50, now);
        TEST_ASSERT_EQUAL(CarStop::None, g.tick(now, true));
    }
    g.drive(50, 50, now);
    TEST_ASSERT_EQUAL(CarStop::MaxRun, g.tick(now, true));
    TEST_ASSERT_FALSE(g.drive(50, 50, now + 200));  // still held: refused
    TEST_ASSERT_FALSE(g.moving());
    TEST_ASSERT_TRUE(g.drive(0, 0, now + 400));     // released
    TEST_ASSERT_EQUAL(CarStop::Hive, g.lastStop());
    TEST_ASSERT_TRUE(g.drive(50, 50, now + 600));   // and it may go again
    TEST_ASSERT_TRUE(g.moving());
}

static void test_a_stale_drive_is_ignored_and_an_unsynced_clock_is_not_judged() {
    CarGuard g;
    TEST_ASSERT_FALSE(g.drive(50, 50, 1000, 1762077600000ULL, 1762077601001ULL));
    TEST_ASSERT_FALSE(g.moving());
    TEST_ASSERT_TRUE(g.drive(50, 50, 1000, 1762077600000ULL, 1762077600500ULL));
    TEST_ASSERT_TRUE(g.drive(50, 50, 1100, 1762077600000ULL, 0));
}

static void test_millis_wraparound_keeps_the_deadman() {
    CarGuard g;
    g.drive(50, 50, 0xFFFFFF00u);
    TEST_ASSERT_EQUAL(CarStop::None, g.tick(0x00000010u, true));  // 272 ms later, across the wrap
    TEST_ASSERT_EQUAL(CarStop::Deadman, g.tick(0x00000100u, true));
}

static void test_the_stream_starts_only_once_wifi_is_up_and_retries_every_10_s() {
    // At boot (setup): no Wi-Fi yet, nothing may open a socket.
    TEST_ASSERT_FALSE(carStreamMayStart(true, false, false, false, 100, 0));
    TEST_ASSERT_TRUE(carStreamMayStart(true, false, true, false, 3000, 0));
    TEST_ASSERT_FALSE(carStreamMayStart(true, false, true, true, 5000, 3000));   // failed 2 s ago
    TEST_ASSERT_TRUE(carStreamMayStart(true, false, true, true, 13000, 3000));
    TEST_ASSERT_FALSE(carStreamMayStart(true, true, true, true, 60000, 3000));   // running
    TEST_ASSERT_FALSE(carStreamMayStart(false, false, true, false, 3000, 0));   // no camera, no stream
}

int main(int, char**) {
    UNITY_BEGIN();
    RUN_TEST(test_pins_are_the_free_ones_and_all_different);
    RUN_TEST(test_forward_drives_the_first_input_back_the_second);
    RUN_TEST(test_speed_scales_by_the_limit_and_pwm_off_is_full);
    RUN_TEST(test_mounting_swaps_and_inverts);
    RUN_TEST(test_the_car_stops_500_ms_after_the_last_drive);
    RUN_TEST(test_a_lost_link_stops_at_once);
    RUN_TEST(test_the_max_run_holds_the_car_until_a_stop_comes);
    RUN_TEST(test_a_stale_drive_is_ignored_and_an_unsynced_clock_is_not_judged);
    RUN_TEST(test_millis_wraparound_keeps_the_deadman);
    RUN_TEST(test_the_stream_starts_only_once_wifi_is_up_and_retries_every_10_s);
    return UNITY_END();
}
