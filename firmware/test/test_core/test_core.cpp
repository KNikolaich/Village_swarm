#include <unity.h>

#include <cstring>
#include <string>

#include "../../lib/hornet-core/src/CommandGuard.h"
#include "../../lib/hornet-core/src/Outbox.h"
#include "../../lib/hornet-core/src/Ulid.h"

using namespace hornet;

void setUp() {}
void tearDown() {}

static void test_ulid_matches_contract_and_round_trips_time() {
    const uint8_t random[10] = {0xFF, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x00};
    char id[27];
    const uint64_t ms = 1791487611037ULL;  // 2026-10-08
    encodeUlid(ms, random, id);
    TEST_ASSERT_EQUAL(26, std::strlen(id));
    TEST_ASSERT_TRUE(id[0] >= '0' && id[0] <= '7');  // 48-bit time fits under "7ZZZ..."
    for (char c : std::string(id)) TEST_ASSERT_NULL(std::strchr("ILOU", c));
    TEST_ASSERT_EQUAL_UINT64(ms, ulidTimestamp(id));
}

static void test_ulid_known_value() {
    // Same bytes as the C# Ulid: 0 ms and all-zero randomness encodes to 26 zeros; max time to "7ZZZZZZZZZ".
    const uint8_t zero[10] = {0};
    char id[27];
    encodeUlid(0, zero, id);
    TEST_ASSERT_EQUAL_STRING("00000000000000000000000000", id);
    encodeUlid(0xFFFFFFFFFFFFULL, zero, id);
    TEST_ASSERT_EQUAL_STRING_LEN("7ZZZZZZZZZ", id, 10);
}

static void test_command_expired_after_ttl() {
    CommandGuard<> guard;
    TEST_ASSERT_EQUAL(CommandCheck::Execute, guard.check("A", 1000000, 30, 1000000 + 29000));
    TEST_ASSERT_EQUAL(CommandCheck::Expired, guard.check("A", 1000000, 30, 1000000 + 30001));
}

static void test_command_runs_when_clock_unsynced() {
    CommandGuard<> guard;
    TEST_ASSERT_EQUAL(CommandCheck::Execute, guard.check("A", 1000000, 30, 0));
}

static void test_command_duplicate_cid_returns_stored_ack() {
    CommandGuard<2> guard;
    guard.remember("A", "t/a", "{\"status\":\"ok\"}");
    TEST_ASSERT_EQUAL(CommandCheck::Duplicate, guard.check("A", 0, 30, 0));
    const std::string* topic = nullptr;
    TEST_ASSERT_EQUAL_STRING("{\"status\":\"ok\"}", guard.ackFor("A", &topic)->c_str());
    TEST_ASSERT_EQUAL_STRING("t/a", topic->c_str());
    guard.remember("B", "t", "b");
    guard.remember("C", "t", "c");  // capacity 2: "A" is forgotten
    TEST_ASSERT_NULL(guard.ackFor("A"));
    TEST_ASSERT_EQUAL(CommandCheck::Execute, guard.check("A", 0, 30, 0));
}

static void test_outbox_keeps_order_and_drops_oldest() {
    Outbox<3> box;
    TEST_ASSERT_TRUE(box.push("t", "1"));
    TEST_ASSERT_TRUE(box.push("t", "2"));
    TEST_ASSERT_TRUE(box.push("t", "3"));
    TEST_ASSERT_FALSE(box.push("t", "4"));
    TEST_ASSERT_EQUAL(3, box.size());
    TEST_ASSERT_EQUAL_STRING("2", box.front().payload.c_str());
    box.pop();
    TEST_ASSERT_EQUAL_STRING("3", box.front().payload.c_str());
    box.pop();
    box.pop();
    TEST_ASSERT_TRUE(box.empty());
    box.pop();  // harmless on empty
    TEST_ASSERT_TRUE(box.empty());
}

static void test_backoff_grows_to_a_minute() {
    TEST_ASSERT_EQUAL_UINT32(1000, backoffMs(0));
    TEST_ASSERT_EQUAL_UINT32(2000, backoffMs(1));
    TEST_ASSERT_EQUAL_UINT32(32000, backoffMs(5));
    TEST_ASSERT_EQUAL_UINT32(60000, backoffMs(6));
    TEST_ASSERT_EQUAL_UINT32(60000, backoffMs(40));
}

static void test_cooldown() {
    Cooldown c;
    TEST_ASSERT_TRUE(c.tryFire(100, 20000));
    TEST_ASSERT_FALSE(c.tryFire(5000, 20000));
    TEST_ASSERT_TRUE(c.tryFire(20100, 20000));
    TEST_ASSERT_TRUE(c.tryFire(0xFFFFFFF0UL + 20000UL, 20000));  // survives millis() wrap
}

int main(int, char**) {
    UNITY_BEGIN();
    RUN_TEST(test_ulid_matches_contract_and_round_trips_time);
    RUN_TEST(test_ulid_known_value);
    RUN_TEST(test_command_expired_after_ttl);
    RUN_TEST(test_command_runs_when_clock_unsynced);
    RUN_TEST(test_command_duplicate_cid_returns_stored_ack);
    RUN_TEST(test_outbox_keeps_order_and_drops_oldest);
    RUN_TEST(test_backoff_grows_to_a_minute);
    RUN_TEST(test_cooldown);
    return UNITY_END();
}
