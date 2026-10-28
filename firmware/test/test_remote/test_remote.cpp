#include <unity.h>

#include "../../lib/hornet-core/src/RemoteMonitor.h"

using namespace hornet::remote;

void setUp() {}
void tearDown() {}

static void test_frame_url_from_what_people_paste() {
    TEST_ASSERT_EQUAL_STRING("https://hive.x.keenetic.pro/api/monitor/v1/frame.bin", frameUrl(" https://hive.x.keenetic.pro/ ").c_str());
    TEST_ASSERT_EQUAL_STRING("https://hive.x.keenetic.pro/api/monitor/v1/frame.bin", frameUrl("https://hive.x.keenetic.pro").c_str());
    TEST_ASSERT_EQUAL_STRING("http://192.168.1.5:8088/api/monitor/v1/frame.bin", frameUrl("http://192.168.1.5:8088/api/monitor/v1/frame").c_str());
    TEST_ASSERT_EQUAL_STRING("https://h/api/monitor/v1/frame.bin", frameUrl("https://h/api/monitor/v1/frame.bin").c_str());
    TEST_ASSERT_EQUAL_STRING("", frameUrl("hive.x.keenetic.pro").c_str());
    TEST_ASSERT_EQUAL_STRING("", frameUrl("https://").c_str());
    TEST_ASSERT_TRUE(isHttps(frameUrl("https://h")));
    TEST_ASSERT_FALSE(isHttps(frameUrl("http://h")));
}

static void test_interval_stays_sane() {
    TEST_ASSERT_EQUAL(kDefaultIntervalMin, clampInterval(0));
    TEST_ASSERT_EQUAL(5, clampInterval(1));
    TEST_ASSERT_EQUAL(30, clampInterval(30));
    TEST_ASSERT_EQUAL(120, clampInterval(1000));
}

static void test_banner_after_half_an_hour_then_hourly() {
    TEST_ASSERT_FALSE(bannerDue(20, 0));
    TEST_ASSERT_TRUE(bannerDue(30, 0));
    TEST_ASSERT_FALSE(bannerDue(40, 30));
    TEST_ASSERT_TRUE(bannerDue(90, 30));
}

static void test_answers() {
    TEST_ASSERT_EQUAL(5000u, frameBytes(200, 200));
    TEST_ASSERT_EQUAL(30u, frameBytes(9, 15));  // rows padded to whole bytes
    TEST_ASSERT_TRUE(classify(200, 5000, 200, 200, 200, 200) == Answer::Draw);
    TEST_ASSERT_TRUE(classify(200, 4999, 200, 200, 200, 200) == Answer::Retry);   // cut short
    TEST_ASSERT_TRUE(classify(200, 5000, 100, 400, 200, 200) == Answer::Retry);   // another screen
    TEST_ASSERT_TRUE(classify(304, 0, 0, 0, 200, 200) == Answer::Keep);
    TEST_ASSERT_TRUE(classify(401, 0, 0, 0, 200, 200) == Answer::BadToken);
    TEST_ASSERT_TRUE(classify(404, 0, 0, 0, 200, 200) == Answer::NotDrawn);
    TEST_ASSERT_TRUE(classify(429, 0, 0, 0, 200, 200) == Answer::Retry);
    TEST_ASSERT_TRUE(classify(-1, 0, 0, 0, 200, 200) == Answer::Retry);
}

static void test_tabs() {
    TEST_ASSERT_EQUAL_STRING("https://h/api/monitor/v1/frame.bin?tab=2", tabUrl("https://h/", 2).c_str());
    TEST_ASSERT_EQUAL_STRING("", tabUrl("h", 2).c_str());
    TEST_ASSERT_EQUAL(1, stepTab(0, 1, 4));
    TEST_ASSERT_EQUAL(3, stepTab(0, -1, 4));  // round at the ends
    TEST_ASSERT_EQUAL(0, stepTab(3, 1, 4));
    TEST_ASSERT_EQUAL_INT(3, tabCount(3, 4));
    TEST_ASSERT_EQUAL_INT(4, tabCount(0, 4));  // no header: as before
    TEST_ASSERT_EQUAL_INT(4, tabCount(9, 4));
    TEST_ASSERT_EQUAL_UINT32(900u, nextReadSeconds(15, 0));    // its own interval
    TEST_ASSERT_EQUAL_UINT32(120u, nextReadSeconds(15, 120));  // monitored: sooner
    TEST_ASSERT_EQUAL_UINT32(300u, nextReadSeconds(5, 600));   // never later than its own
    TEST_ASSERT_EQUAL_UINT32(60u, nextReadSeconds(15, 5));     // at least a minute
    const Cell cell = tabCell(2, 4, 416, 240);
    TEST_ASSERT_EQUAL(208, cell.x);
    TEST_ASSERT_EQUAL(218, cell.y);
    TEST_ASSERT_EQUAL(104, cell.w);
    TEST_ASSERT_EQUAL(22, cell.h);
}

static void test_clock_and_battery() {
    TEST_ASSERT_EQUAL_UINT32(1793133662u, httpDate("Tue, 27 Oct 2026 20:41:02 GMT"));
    TEST_ASSERT_EQUAL_UINT32(1772323200u, httpDate("Sun, 01 Mar 2026 00:00:00 GMT"));
    TEST_ASSERT_EQUAL_UINT32(0u, httpDate("Tue, 29 Feb 2000 00:00:00 GMT"));  // before 2020: not a clock to trust
    TEST_ASSERT_EQUAL_UINT32(0u, httpDate("garbage"));
    TEST_ASSERT_EQUAL_UINT32(59u, secondsToNextMinute(1793133662u));  // :02 -> wake at :01 of the next minute
    TEST_ASSERT_EQUAL_UINT32(61u, secondsToNextMinute(1793133660u));
    TEST_ASSERT_EQUAL(3900, batteryMv(1950));
    TEST_ASSERT_EQUAL(-1, batteryMv(100));   // no divider: the pin floats low
    TEST_ASSERT_EQUAL(-1, batteryMv(3000));  // or reads 3.3 V
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_frame_url_from_what_people_paste);
    RUN_TEST(test_interval_stays_sane);
    RUN_TEST(test_banner_after_half_an_hour_then_hourly);
    RUN_TEST(test_answers);
    RUN_TEST(test_tabs);
    RUN_TEST(test_clock_and_battery);
    return UNITY_END();
}
