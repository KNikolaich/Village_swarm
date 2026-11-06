#include <unity.h>

#include "../../lib/hornet-core/src/DisplayLayout.h"

using namespace hornet::display;

void setUp() {}
void tearDown() {}

static void test_size_is_a_share_of_the_screen_height() {
    TEST_ASSERT_EQUAL(6, pixelHeight(1, 40));  // never below the smallest font
    TEST_ASSERT_EQUAL(12, pixelHeight(3, 40));
    TEST_ASSERT_EQUAL(20, pixelHeight(5, 40));
    TEST_ASSERT_EQUAL(40, pixelHeight(10, 40));
    TEST_ASSERT_EQUAL(240, pixelHeight(5, 480));
    TEST_ASSERT_EQUAL(20, pixelHeight(1, 200));
    TEST_ASSERT_EQUAL(40, pixelHeight(42, 40));  // out of range is clamped
    TEST_ASSERT_EQUAL(6, pixelHeight(0, 40));
}

static void test_the_largest_font_that_fits_is_taken() {
    const int heights[] = {6, 9, 13, 16, 20, 24, 28, 32, 38};
    TEST_ASSERT_EQUAL(0, pickFont(heights, 9, 6));
    TEST_ASSERT_EQUAL(0, pickFont(heights, 9, 4));   // nothing fits: the smallest
    TEST_ASSERT_EQUAL(2, pickFont(heights, 9, 15));
    TEST_ASSERT_EQUAL(4, pickFont(heights, 9, 20));
    TEST_ASSERT_EQUAL(8, pickFont(heights, 9, 40));
}

static void test_align_moves_the_text_around_its_anchor() {
    TEST_ASSERT_EQUAL(10, leftEdge(10, 20, parseAlign(nullptr)));
    TEST_ASSERT_EQUAL(10, leftEdge(10, 20, parseAlign("left")));
    TEST_ASSERT_EQUAL(0, leftEdge(10, 20, parseAlign("center")));
    TEST_ASSERT_EQUAL(51, leftEdge(71, 20, parseAlign("right")));
}

static void test_out_of_range_marks_and_their_box() {
    TEST_ASSERT_TRUE(parseMark("high") == Mark::High);
    TEST_ASSERT_TRUE(parseMark("low") == Mark::Low);
    TEST_ASSERT_TRUE(parseMark(nullptr) == Mark::None);
    TEST_ASSERT_TRUE(parseMark("pink") == Mark::None);
    const Box inside = markBox(10, 10, 20, 8, 72, 40);
    TEST_ASSERT_EQUAL(9, inside.x);
    TEST_ASSERT_EQUAL(9, inside.y);
    TEST_ASSERT_EQUAL(22, inside.w);
    TEST_ASSERT_EQUAL(10, inside.h);
    const Box edge = markBox(0, 0, 72, 40, 72, 40);  // the whole screen: no pixel outside it
    TEST_ASSERT_EQUAL(0, edge.x);
    TEST_ASSERT_EQUAL(0, edge.y);
    TEST_ASSERT_EQUAL(72, edge.w);
    TEST_ASSERT_EQUAL(40, edge.h);
    TEST_ASSERT_EQUAL(0, arrowWidth(Mark::None, 16));
    TEST_ASSERT_EQUAL(6, arrowWidth(Mark::Low, 7));     // the small 5x8 font
    TEST_ASSERT_EQUAL(8, arrowWidth(Mark::High, 16));   // Logisoso 16 and up
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_size_is_a_share_of_the_screen_height);
    RUN_TEST(test_the_largest_font_that_fits_is_taken);
    RUN_TEST(test_align_moves_the_text_around_its_anchor);
    RUN_TEST(test_out_of_range_marks_and_their_box);
    return UNITY_END();
}
