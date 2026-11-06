#include <unity.h>

#include "../../lib/hornet-core/src/HiveUrl.h"

using namespace hornet;

void setUp() {}
void tearDown() {}

static void test_a_bare_host_is_taken_as_http() {
    TEST_ASSERT_EQUAL_STRING("http://192.168.10.4", hiveUrl("192.168.10.4").c_str());
    TEST_ASSERT_EQUAL_STRING("http://hive.local:8443", hiveUrl("hive.local:8443/").c_str());
}

static void test_a_full_address_stays_as_it_is() {
    TEST_ASSERT_EQUAL_STRING("http://192.168.10.4", hiveUrl("http://192.168.10.4/").c_str());
    TEST_ASSERT_EQUAL_STRING("https://hive.example.org", hiveUrl(" https://hive.example.org ").c_str());
    TEST_ASSERT_EQUAL_STRING("", hiveUrl("  ").c_str());
}

int main(int, char**) {
    UNITY_BEGIN();
    RUN_TEST(test_a_bare_host_is_taken_as_http);
    RUN_TEST(test_a_full_address_stays_as_it_is);
    return UNITY_END();
}
