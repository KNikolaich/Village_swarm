#include <unity.h>

#include <vector>

#include "../../lib/hornet-core/src/Buttons.h"

using namespace hornet::buttons;

void setUp() {}
void tearDown() {}

struct Step {
    uint32_t at;  // ms
    bool down;
};

/// Plays levels at the given times (polling every 5 ms in between) and returns what the button said, with the time of each.
static std::vector<std::pair<uint32_t, Out>> play(Button& b, const std::vector<Step>& steps, uint32_t from, uint32_t until) {
    std::vector<std::pair<uint32_t, Out>> out;
    size_t next = 0;
    bool level = false;
    for (uint32_t t = 0; t <= until; t += 5) {
        while (next < steps.size() && steps[next].at <= t) level = steps[next++].down;
        if (t < from) continue;
        const Out o = b.poll(level, t);
        if (o != Out::None) out.push_back({t, o});
    }
    return out;
}

static std::vector<std::pair<uint32_t, Out>> play(Button& b, const std::vector<Step>& steps, uint32_t until) { return play(b, steps, 0, until); }

static void test_a_clean_short_press_says_short_once_when_let_go() {
    Button b;
    const auto out = play(b, {{100, true}, {300, false}}, 1000);
    TEST_ASSERT_EQUAL(1, out.size());
    TEST_ASSERT_TRUE(out[0].second == Out::Short);
    TEST_ASSERT_TRUE(out[0].first >= 350 && out[0].first <= 360);  // after the release has been steady for 50 ms
}

static void test_chatter_on_press_and_on_release_is_one_press() {
    Button b;
    // down, up, down, up, down for a few ms each, then held; the same on the way out
    const auto out = play(b, {{100, true}, {105, false}, {110, true}, {115, false}, {120, true},
                              {300, false}, {305, true}, {310, false}, {315, true}, {320, false}}, 1000);
    TEST_ASSERT_EQUAL(1, out.size());
    TEST_ASSERT_TRUE(out[0].second == Out::Short);
}

// Chatter in bursts of 5 to 20 ms, repeated for 100 ms, then the level settles: one press at the press, one release at the release.
static void test_bursts_of_chatter_for_100_ms_are_still_one_press() {
    Button b;
    std::vector<Step> steps;
    uint32_t t = 100;
    bool level = true;
    for (uint32_t i = 0, widths[] = {5, 12, 7, 20, 9, 15, 6, 11}; i < 8 && t < 200; i++) {  // the burst: 100 ms of toggling
        steps.push_back({t, level});
        t += widths[i];
        level = !level;
    }
    steps.push_back({t, true});  // settles pressed (t is about 185 ms)
    for (uint32_t i = 0, widths[] = {6, 14, 5, 18, 8, 12, 10, 9}; i < 8; i++) {  // the same on the way out, from 600 ms
        if (i == 0) t = 600;
        steps.push_back({t, i % 2 ? true : false});
        t += widths[i];
    }
    steps.push_back({t, false});  // settles released
    const auto out = play(b, steps, 1500);
    TEST_ASSERT_EQUAL(1, out.size());
    TEST_ASSERT_TRUE(out[0].second == Out::Short);
}

static void test_a_glitch_in_the_middle_of_a_hold_is_not_a_second_press() {
    Button b;
    // held from 100 to 500 with a 20 ms dropout at 250 (worn contact): one press, not two
    const auto out = play(b, {{100, true}, {250, false}, {270, true}, {500, false}}, 1500);
    TEST_ASSERT_EQUAL(1, out.size());
    TEST_ASSERT_TRUE(out[0].second == Out::Short);
}

static void test_a_touch_shorter_than_the_stable_time_is_nothing() {
    Button b;
    TEST_ASSERT_EQUAL(0, play(b, {{100, true}, {140, false}}, 1000).size());  // 40 ms is a touch, not a press
}

static void test_a_long_press_says_long_once_while_held_and_no_short_after() {
    Button b;
    const auto out = play(b, {{100, true}, {3500, false}}, 5000);
    TEST_ASSERT_EQUAL(1, out.size());
    TEST_ASSERT_TRUE(out[0].second == Out::Long);
    TEST_ASSERT_TRUE(out[0].first >= 2100 && out[0].first <= 2140);  // 2 s after the press began
}

static void test_a_press_right_after_a_view_change_is_ignored_whole() {
    Button b;
    b.lockFor(1000, kExitLockMs);  // the screen just changed
    // a second press 150 ms later (bounce, or an impatient finger): nothing
    const auto out = play(b, {{1150, true}, {1300, false}}, 1000, 2000);
    TEST_ASSERT_EQUAL(0, out.size());
}

static void test_a_press_after_the_lock_counts_again() {
    Button b;
    b.lockFor(0, kExitLockMs);
    const auto out = play(b, {{700, true}, {900, false}}, 1800);
    TEST_ASSERT_EQUAL(1, out.size());
    TEST_ASSERT_TRUE(out[0].second == Out::Short);
}

static void test_a_finger_still_down_when_the_screen_changes_is_not_a_new_press() {
    Button b;
    // pressed at 100, the polling stopped for the refresh and the lock comes at 600 while the finger is still down; let go at 900
    b.poll(true, 100);
    b.lockFor(600, kExitLockMs);
    const auto out = play(b, {{100, true}, {900, false}}, 600, 1500);
    TEST_ASSERT_EQUAL(0, out.size());
}

static void test_a_press_that_began_in_the_lock_stays_ignored_after_it_ends() {
    Button b;
    b.lockFor(0, kExitLockMs);
    // begins at 300 (locked), held past the end of the lock, let go at 800: ignored; the next clean press counts
    const auto out = play(b, {{300, true}, {800, false}, {1000, true}, {1200, false}}, 2000);
    TEST_ASSERT_EQUAL(1, out.size());
    TEST_ASSERT_TRUE(out[0].second == Out::Short);
    TEST_ASSERT_TRUE(out[0].first > 1200);
}

static void test_busy_while_the_button_is_down() {
    Button b;
    TEST_ASSERT_FALSE(b.busy());
    b.poll(true, 10);
    TEST_ASSERT_TRUE(b.busy());
    b.poll(false, 20);
    b.poll(false, 60);
    TEST_ASSERT_FALSE(b.busy());
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_a_clean_short_press_says_short_once_when_let_go);
    RUN_TEST(test_chatter_on_press_and_on_release_is_one_press);
    RUN_TEST(test_bursts_of_chatter_for_100_ms_are_still_one_press);
    RUN_TEST(test_a_glitch_in_the_middle_of_a_hold_is_not_a_second_press);
    RUN_TEST(test_a_touch_shorter_than_the_stable_time_is_nothing);
    RUN_TEST(test_a_long_press_says_long_once_while_held_and_no_short_after);
    RUN_TEST(test_a_press_right_after_a_view_change_is_ignored_whole);
    RUN_TEST(test_a_press_after_the_lock_counts_again);
    RUN_TEST(test_a_finger_still_down_when_the_screen_changes_is_not_a_new_press);
    RUN_TEST(test_a_press_that_began_in_the_lock_stays_ignored_after_it_ends);
    RUN_TEST(test_busy_while_the_button_is_down);
    return UNITY_END();
}
