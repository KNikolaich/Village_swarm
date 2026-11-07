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
    TEST_ASSERT_TRUE(out[0].first >= 330 && out[0].first <= 340);  // after the release has been steady for 30 ms
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
    TEST_ASSERT_EQUAL(0, play(b, {{100, true}, {130, false}}, 1000).size());  // 30 ms is a touch, not a press
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
    const auto out = play(b, {{1100, true}, {1250, false}}, 1000, 2000);
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
    b.lockFor(0, 400);
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

// ---- the rocker's contacts, read from their edges (PressCounter)

struct Edge {
    int64_t ms;
    bool down;
};

/// How many presses the counter accepts out of these edges (milliseconds in, microseconds inside).
static int accepted(PressCounter& c, const std::vector<Edge>& edges) {
    int n = 0;
    for (const Edge& e : edges)
        if (c.onEdge(e.down, e.ms * 1000)) n++;
    return n;
}

static void test_one_click_counts_once_and_the_release_never_counts() {
    PressCounter c;
    TEST_ASSERT_EQUAL(1, accepted(c, {{0, true}, {120, false}}));
}

static void test_chatter_around_a_click_is_one_press() {
    PressCounter c;
    // bounce on the press (5 ms apart), then the release with chatter of its own
    TEST_ASSERT_EQUAL(1, accepted(c, {{0, true}, {5, false}, {10, true}, {15, false}, {20, true}, {150, false}, {154, true}, {158, false}, {163, true}, {168, false}}));
}

static void test_a_bounce_after_the_release_but_inside_the_gap_is_ignored() {
    PressCounter c;
    // the release at 100, then a bounce at 145: let go for 45 ms (more than the 40 ms quiet) but only 145 ms after the press
    TEST_ASSERT_EQUAL(1, accepted(c, {{0, true}, {100, false}, {145, true}, {190, false}}));
}

static void test_a_second_click_after_the_gap_counts() {
    PressCounter c;
    TEST_ASSERT_EQUAL(2, accepted(c, {{0, true}, {100, false}, {300, true}, {400, false}}));  // 300 ms apart: two clicks
    PressCounter d;
    TEST_ASSERT_EQUAL(1, accepted(d, {{0, true}, {80, false}, {120, true}, {200, false}}));  // 120 ms: inside the 150 ms gap
}

static void test_a_new_press_needs_the_contact_let_go_and_quiet() {
    PressCounter c;
    // 400 ms after the first press, but the contact was let go only 30 ms before: still chatter
    TEST_ASSERT_EQUAL(1, accepted(c, {{0, true}, {100, false}, {350, false}, {370, true}}) + 0);
    PressCounter d;
    TEST_ASSERT_EQUAL(2, accepted(d, {{0, true}, {100, false}, {350, false}, {420, true}}));
}

static void test_a_contact_held_at_start_gives_no_press_until_it_was_let_go() {
    PressCounter c;
    TEST_ASSERT_EQUAL(1, accepted(c, {{200, false}, {600, true}, {700, false}}));  // the first edge seen is the release of the start
}

static void test_edges_stacked_during_a_refresh_are_mostly_chatter() {
    PressCounter c;
    // one click, then a second 1.2 s later (during a slow refresh the interrupt saw both, plus chatter on each)
    const int n = accepted(c, {{0, true}, {4, false}, {9, true}, {100, false}, {104, true}, {110, false}, {1200, true}, {1203, false}, {1210, true}, {1300, false}});
    TEST_ASSERT_EQUAL(2, n);  // two clicks: the caller keeps at most one of them pending
}

static void test_the_debug_counters_say_what_the_log_needs() {
    PressCounter c;
    c.onEdge(true, 0);                 // press 1
    c.onEdge(false, 100000);
    c.onEdge(true, 120000);            // rejected (gap)
    c.onEdge(false, 150000);
    TEST_ASSERT_TRUE(c.onEdge(true, 600000));  // press 2, 600 ms after the first
    TEST_ASSERT_EQUAL_UINT32(600, c.lastGapMs);
    TEST_ASSERT_EQUAL_UINT32(4, c.lastEdges);   // the 3 edges since press 1 plus this one (the first press's own edge counted before)
}

static void test_a_button_tells_its_edges_and_gap_for_the_log() {
    Button b;
    play(b, {{100, true}, {110, false}, {120, true}, {500, false}}, 1000);  // chatter on the press, then a clean release
    TEST_ASSERT_TRUE(b.lastEdges() >= 3);  // down, up, down, up: the raw edges it saw before accepting
    TEST_ASSERT_EQUAL_UINT32(0, b.lastGapMs());
}

// ---- what the keys do in the reader

static void test_in_the_settings_one_rocker_press_changes_nothing() {
    TEST_ASSERT_TRUE(settingsAction(Key::Press) == SettingsAction::None);
    // even a press heard twice (the old fault) changes no value
    int changes = 0;
    for (const Key k : {Key::Press, Key::Press}) changes += settingsAction(k) == SettingsAction::Change;
    TEST_ASSERT_EQUAL(0, changes);
}

static void test_in_the_settings_menu_changes_one_row_per_press_and_the_rocker_moves() {
    TEST_ASSERT_TRUE(settingsAction(Key::Menu) == SettingsAction::Change);
    TEST_ASSERT_TRUE(settingsAction(Key::Up) == SettingsAction::MoveUp);
    TEST_ASSERT_TRUE(settingsAction(Key::Down) == SettingsAction::MoveDown);
    TEST_ASSERT_TRUE(settingsAction(Key::Exit) == SettingsAction::Back);
    TEST_ASSERT_TRUE(settingsAction(Key::MenuLong) == SettingsAction::None);
    TEST_ASSERT_TRUE(settingsAction(Key::None) == SettingsAction::None);
}

static void test_a_book_opens_its_settings_by_the_press_or_a_held_menu() {
    TEST_ASSERT_TRUE(opensSettings(Key::Press));
    TEST_ASSERT_TRUE(opensSettings(Key::MenuLong));
    TEST_ASSERT_FALSE(opensSettings(Key::Menu));  // a short MENU turns the page
    TEST_ASSERT_FALSE(opensSettings(Key::Exit));
}

static void test_one_tap_through_the_whole_chain_is_one_event_and_one_change() {
    // the rocker contact: a tap with chatter, then a late bounce while the screen refreshes: one press accepted
    PressCounter c;
    const int presses = accepted(c, {{0, true}, {6, false}, {12, true}, {110, false}, {125, true}, {130, false}});
    TEST_ASSERT_EQUAL(1, presses);
    // in a book that press opens the settings; in the settings it changes nothing; MENU is what changes a value
    TEST_ASSERT_TRUE(opensSettings(Key::Press));
    TEST_ASSERT_TRUE(settingsAction(Key::Press) == SettingsAction::None);
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
    RUN_TEST(test_one_click_counts_once_and_the_release_never_counts);
    RUN_TEST(test_chatter_around_a_click_is_one_press);
    RUN_TEST(test_a_bounce_after_the_release_but_inside_the_gap_is_ignored);
    RUN_TEST(test_a_second_click_after_the_gap_counts);
    RUN_TEST(test_a_new_press_needs_the_contact_let_go_and_quiet);
    RUN_TEST(test_a_contact_held_at_start_gives_no_press_until_it_was_let_go);
    RUN_TEST(test_edges_stacked_during_a_refresh_are_mostly_chatter);
    RUN_TEST(test_the_debug_counters_say_what_the_log_needs);
    RUN_TEST(test_a_button_tells_its_edges_and_gap_for_the_log);
    RUN_TEST(test_in_the_settings_one_rocker_press_changes_nothing);
    RUN_TEST(test_in_the_settings_menu_changes_one_row_per_press_and_the_rocker_moves);
    RUN_TEST(test_a_book_opens_its_settings_by_the_press_or_a_held_menu);
    RUN_TEST(test_one_tap_through_the_whole_chain_is_one_event_and_one_change);
    return UNITY_END();
}
