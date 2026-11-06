#include <unity.h>

#include "../../lib/hornet-core/src/LegacyFs.h"

using namespace hornet::legacyfs;

void setUp() {}
void tearDown() {}

static void test_esp01_layout_has_the_old_place_before_it() {
    // eagle.flash.1m64.ld now, eagle.flash.1m256.ld (the esp01_1m default) before
    const Region old = previousFor(0xEB000, 0x10000);
    TEST_ASSERT_EQUAL_HEX32(0xBB000, old.start);
    TEST_ASSERT_EQUAL_HEX32(0x40000, old.size);
    TEST_ASSERT_EQUAL_HEX32(0xFB000, old.start + old.size);  // both end where the SDK settings begin
}

static void test_layouts_that_never_moved_have_no_old_place() {
    TEST_ASSERT_EQUAL_UINT32(0, previousFor(0xBB000, 0x40000).size);    // an old ESP-01 build itself
    TEST_ASSERT_EQUAL_UINT32(0, previousFor(0x200000, 0x1FA000).size);  // D1 mini / NodeMCU, 4 MB (eagle.flash.4m2m.ld)
}

static void test_copies_only_onto_a_board_without_a_hornet() {
    TEST_ASSERT_TRUE(shouldCopy(false, true));    // first boot after the USB flash
    TEST_ASSERT_FALSE(shouldCopy(true, true));    // already copied, or configured anew: never overwrite
    TEST_ASSERT_FALSE(shouldCopy(false, false));  // a new board: nothing to bring
    TEST_ASSERT_FALSE(shouldCopy(true, false));
}

static void test_copies_settings_but_not_the_config_or_leftovers() {
    for (const char* key : {"ssid", "pass", "mqtt", "mqttport", "mqttuser", "mqttpass", "id", "upload", "token", "boot"})
        TEST_ASSERT_TRUE_MESSAGE(copyKey(key), key);
    TEST_ASSERT_FALSE(copyKey("config"));  // retained, the hive sends it again
    TEST_ASSERT_FALSE(copyKey("\a_new?"));
    TEST_ASSERT_FALSE(copyKey(""));
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_esp01_layout_has_the_old_place_before_it);
    RUN_TEST(test_layouts_that_never_moved_have_no_old_place);
    RUN_TEST(test_copies_only_onto_a_board_without_a_hornet);
    RUN_TEST(test_copies_settings_but_not_the_config_or_leftovers);
    return UNITY_END();
}
