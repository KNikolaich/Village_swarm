#include <unity.h>

#include <string>
#include <vector>

#include "../../lib/hornet-core/src/PhotoArchive.h"

using namespace hornet;

void setUp() {}
void tearDown() {}

static constexpr uint64_t MB = 1024ULL * 1024;

static void test_reserve_is_5_percent_but_at_least_32_mb() {
    TEST_ASSERT_EQUAL_UINT64(32 * MB, sdReserveBytes(128 * MB));
    TEST_ASSERT_EQUAL_UINT64(32 * MB, sdReserveBytes(640 * MB));
    TEST_ASSERT_EQUAL_UINT64(1600 * MB, sdReserveBytes(32000 * MB));
}

static void test_needs_room_when_the_write_eats_the_reserve() {
    const uint64_t total = 1000 * MB;  // reserve 50 MB
    TEST_ASSERT_FALSE(sdNeedsRoom(total, 900 * MB, 1 * MB));
    TEST_ASSERT_TRUE(sdNeedsRoom(total, 950 * MB, 1));
    TEST_ASSERT_TRUE(sdNeedsRoom(total, 949 * MB + 1, 1 * MB));
    TEST_ASSERT_TRUE(sdNeedsRoom(total, total + 1, 0));  // used over total: treated as full
}

static void test_bucket_is_the_ulid_prefix() {
    TEST_ASSERT_EQUAL_STRING("01JBQZ", archiveBucket("01JBQZ5M8X0000000000000000-2").c_str());
    TEST_ASSERT_EQUAL_STRING("000000", archiveBucket("abc").c_str());
}

static void test_keep_oldest_collects_the_smallest_sorted() {
    std::vector<std::string> oldest;
    for (const char* n : {"05", "02", "09", "01", "07", "03", "02b"}) keepOldest(oldest, n, 3);
    TEST_ASSERT_EQUAL(3, oldest.size());
    TEST_ASSERT_EQUAL_STRING("01", oldest[0].c_str());
    TEST_ASSERT_EQUAL_STRING("02", oldest[1].c_str());
    TEST_ASSERT_EQUAL_STRING("02b", oldest[2].c_str());
    keepOldest(oldest, "00", 0);
    TEST_ASSERT_EQUAL(3, oldest.size());
}

static void test_unsynced_ids_sort_before_synced_ones() {
    std::vector<std::string> oldest;
    keepOldest(oldest, "01JBQZ5M8X0000000000000000-0", 1);
    keepOldest(oldest, "0000000000ABCDEFGHJKMNPQRS-0", 1);
    TEST_ASSERT_EQUAL_STRING("0000000000ABCDEFGHJKMNPQRS-0", oldest[0].c_str());
}

static void test_retry_pauses_without_sd() {
    TEST_ASSERT_EQUAL(3, kUploadAttemptsWithoutSd);
    TEST_ASSERT_EQUAL_UINT32(0, uploadRetryPauseMs(0));
    TEST_ASSERT_EQUAL_UINT32(2000, uploadRetryPauseMs(1));
    TEST_ASSERT_EQUAL_UINT32(5000, uploadRetryPauseMs(2));
}

static void test_settled_codes_follow_the_ingest_contract() {
    TEST_ASSERT_TRUE(uploadSettled(201));
    TEST_ASSERT_TRUE(uploadSettled(200));
    TEST_ASSERT_TRUE(uploadSettled(400));
    TEST_ASSERT_TRUE(uploadSettled(413));
    TEST_ASSERT_FALSE(uploadSettled(401));
    TEST_ASSERT_FALSE(uploadSettled(408));
    TEST_ASSERT_FALSE(uploadSettled(429));
    TEST_ASSERT_FALSE(uploadSettled(500));
    TEST_ASSERT_FALSE(uploadSettled(-11));  // HTTPClient read timeout
    TEST_ASSERT_FALSE(uploadSettled(0));
}

int main(int, char**) {
    UNITY_BEGIN();
    RUN_TEST(test_reserve_is_5_percent_but_at_least_32_mb);
    RUN_TEST(test_needs_room_when_the_write_eats_the_reserve);
    RUN_TEST(test_bucket_is_the_ulid_prefix);
    RUN_TEST(test_keep_oldest_collects_the_smallest_sorted);
    RUN_TEST(test_unsynced_ids_sort_before_synced_ones);
    RUN_TEST(test_retry_pauses_without_sd);
    RUN_TEST(test_settled_codes_follow_the_ingest_contract);
    return UNITY_END();
}
