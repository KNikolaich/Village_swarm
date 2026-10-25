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

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_frame_url_from_what_people_paste);
    RUN_TEST(test_interval_stays_sane);
    RUN_TEST(test_banner_after_half_an_hour_then_hourly);
    RUN_TEST(test_answers);
    return UNITY_END();
}
