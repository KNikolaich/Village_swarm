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

static void test_offline_banner_at_once_then_on_change_or_every_half_hour() {
    TEST_ASSERT_TRUE(bannerDue(Offline::NoWifi, Offline::None, 5, 0));     // the first failure says so at once
    TEST_ASSERT_FALSE(bannerDue(Offline::NoWifi, Offline::NoWifi, 20, 5)); // the next tries do not flash the screen
    TEST_ASSERT_TRUE(bannerDue(Offline::NoWifi, Offline::NoWifi, 35, 5));  // half an hour behind: the age is redrawn
    TEST_ASSERT_TRUE(bannerDue(Offline::NoHive, Offline::NoWifi, 6, 5));   // another cause: at once
    TEST_ASSERT_TRUE(bannerDue(Offline::NoWifi, Offline::NoWifi, 2, 40));  // the clock went back: say it again
    TEST_ASSERT_FALSE(bannerDue(Offline::None, Offline::NoWifi, 50, 5));
}

static void test_retry_backs_off_to_half_an_hour() {
    TEST_ASSERT_EQUAL_UINT32(5u, retryMinutes(1, 60));
    TEST_ASSERT_EQUAL_UINT32(10u, retryMinutes(2, 60));
    TEST_ASSERT_EQUAL_UINT32(20u, retryMinutes(3, 60));
    TEST_ASSERT_EQUAL_UINT32(30u, retryMinutes(4, 60));
    TEST_ASSERT_EQUAL_UINT32(30u, retryMinutes(500, 60));
    TEST_ASSERT_EQUAL_UINT32(30u, retryMinutes(9, 120));
    TEST_ASSERT_EQUAL_UINT32(10u, retryMinutes(3, 10));  // never longer than the panel's own interval
    TEST_ASSERT_EQUAL_UINT32(5u, retryMinutes(4, 5));
    TEST_ASSERT_EQUAL_UINT32(5u, retryMinutes(0, 10));
}

static void test_age_of_the_data() {
    const Age exact = dataAge(10000u, 10000u - 40 * 60, 99);
    TEST_ASSERT_TRUE(exact.exact);
    TEST_ASSERT_EQUAL_UINT32(40u, exact.minutes);
    const Age counted = dataAge(0u, 5000u, 25);  // no clock: the minutes slept offline
    TEST_ASSERT_FALSE(counted.exact);
    TEST_ASSERT_EQUAL_UINT32(25u, counted.minutes);
    TEST_ASSERT_FALSE(dataAge(1000u, 0u, 7).exact);     // never read
    TEST_ASSERT_FALSE(dataAge(1000u, 2000u, 7).exact);  // the clock went back
    TEST_ASSERT_EQUAL_STRING("40 мин", ageText({40, true}).c_str());
    TEST_ASSERT_EQUAL_STRING("~40 мин", ageText({40, false}).c_str());
    TEST_ASSERT_EQUAL_STRING("3 ч", ageText({200, true}).c_str());
    TEST_ASSERT_EQUAL_STRING("47 ч", ageText({47 * 60 + 59, true}).c_str());
    TEST_ASSERT_EQUAL_STRING("2 дн", ageText({48 * 60, true}).c_str());
    TEST_ASSERT_EQUAL_STRING("нет Wi-Fi, данные 40 мин назад", offlineBanner(Offline::NoWifi, {40, true}, false).c_str());
    TEST_ASSERT_EQUAL_STRING("Улей не отвечает, ~3 ч", offlineBanner(Offline::NoHive, {180, false}, true).c_str());
    TEST_ASSERT_EQUAL_STRING("не тот кадр", causeText(Offline::WrongFrame));
    TEST_ASSERT_EQUAL_STRING("", causeText(Offline::None));
}

static void test_battery_percent() {
    TEST_ASSERT_EQUAL(-1, batteryPercent(-1));
    TEST_ASSERT_EQUAL(0, batteryPercent(3000));
    TEST_ASSERT_EQUAL(0, batteryPercent(3300));
    TEST_ASSERT_EQUAL(50, batteryPercent(3700));
    TEST_ASSERT_EQUAL(65, batteryPercent(3800));
    TEST_ASSERT_EQUAL(90, batteryPercent(4100));
    TEST_ASSERT_EQUAL(57, batteryPercent(3750));  // between 50 and 65
    // a cell on its charger reads 4.17-4.20 V: full
    TEST_ASSERT_EQUAL(100, batteryPercent(4180));
    TEST_ASSERT_EQUAL(100, batteryPercent(4200));
    TEST_ASSERT_EQUAL(100, batteryPercent(4300));
    TEST_ASSERT_TRUE(batteryPercent(4170) >= 97);
    // never down as the voltage goes up
    int last = -1;
    for (int mv = 3000; mv <= 4400; mv += 10) {
        const int now = batteryPercent(mv);
        TEST_ASSERT_TRUE(now >= last);
        last = now;
    }
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
    // the sleeping picture is asked for beside the tab it follows
    TEST_ASSERT_EQUAL_STRING("https://h/api/monitor/v1/frame.bin?tab=1&sleep=1", tabUrl("https://h/", 1, true).c_str());
    // a panel without tabs asks for the sleeping picture alone
    TEST_ASSERT_EQUAL_STRING("https://h/api/monitor/v1/frame.bin?sleep=1", sleepUrl("https://h/").c_str());
    TEST_ASSERT_EQUAL_STRING("", sleepUrl("h").c_str());
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

static void test_tab_weights() {
    uint8_t w[kMaxTabs] = {};
    TEST_ASSERT_EQUAL(3, parseTabWeights("2,3,3", w, kMaxTabs));
    TEST_ASSERT_EQUAL(2, w[0]);
    TEST_ASSERT_EQUAL(3, w[2]);
    TEST_ASSERT_EQUAL(3, parseTabWeights(" 2, 3 ,3 ", w, kMaxTabs));
    TEST_ASSERT_EQUAL(0, parseTabWeights("", w, kMaxTabs));
    TEST_ASSERT_EQUAL(0, parseTabWeights(nullptr, w, kMaxTabs));
    TEST_ASSERT_EQUAL(0, parseTabWeights("2,,3", w, kMaxTabs));
    TEST_ASSERT_EQUAL(0, parseTabWeights("2,x", w, kMaxTabs));
    TEST_ASSERT_EQUAL(0, parseTabWeights("0,3,3", w, kMaxTabs));
    TEST_ASSERT_EQUAL(0, parseTabWeights("1,1,1,1,1", w, kMaxTabs));  // more than the panel keeps
    // the same cells the hive draws (ScreenTabs.CellEdges): 2:3:3 in 240 and in 416 pixels
    const uint8_t weights[3] = {2, 3, 3};
    TEST_ASSERT_EQUAL(0, tabCell(0, 3, 240, 416, weights).x);
    TEST_ASSERT_EQUAL(60, tabCell(0, 3, 240, 416, weights).w);
    TEST_ASSERT_EQUAL(60, tabCell(1, 3, 240, 416, weights).x);
    TEST_ASSERT_EQUAL(90, tabCell(1, 3, 240, 416, weights).w);
    TEST_ASSERT_EQUAL(150, tabCell(2, 3, 240, 416, weights).x);
    TEST_ASSERT_EQUAL(90, tabCell(2, 3, 240, 416, weights).w);
    TEST_ASSERT_EQUAL(104, tabCell(1, 3, 416, 240, weights).x);
    TEST_ASSERT_EQUAL(156, tabCell(1, 3, 416, 240, weights).w);
    TEST_ASSERT_EQUAL(260, tabCell(2, 3, 416, 240, weights).x);
    TEST_ASSERT_EQUAL(156, tabCell(2, 3, 416, 240, weights).w);
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

static void test_a_tab_read_lately_is_not_asked_for_again() {
    TEST_ASSERT_EQUAL_UINT32(300u, freshSeconds(0));   // the panel's own pace: five minutes
    TEST_ASSERT_EQUAL_UINT32(120u, freshSeconds(120)); // monitored: as often as the hive asks
    TEST_ASSERT_EQUAL_UINT32(60u, freshSeconds(20));
    TEST_ASSERT_EQUAL_UINT32(300u, freshSeconds(600)); // monitored slower: still five minutes at most
    TEST_ASSERT_TRUE(isFresh(1000u, 800u, 300u));
    TEST_ASSERT_FALSE(isFresh(1000u, 700u, 300u));     // exactly as old as the limit is not fresh
    TEST_ASSERT_FALSE(isFresh(0u, 800u, 300u));        // no clock: ask
    TEST_ASSERT_FALSE(isFresh(1000u, 0u, 300u));       // never read: ask
    TEST_ASSERT_FALSE(isFresh(700u, 800u, 300u));      // the clock went back: ask
    TEST_ASSERT_EQUAL_UINT32(780u, secondsToDue(900u, 1000u, 880u));
    TEST_ASSERT_EQUAL_UINT32(60u, secondsToDue(900u, 2000u, 880u));   // overdue: in a minute
    TEST_ASSERT_EQUAL_UINT32(900u, secondsToDue(900u, 0u, 880u));     // age not known: the usual pause
}

static void test_why_the_hive_did_not_answer() {
    // name first: a failed lookup is DNS whatever else came back
    TEST_ASSERT_EQUAL(Fail::Dns, classifyFailure(-1, false, 0, 50, 10000));
    // the hive answered, just not with a picture
    TEST_ASSERT_EQUAL(Fail::Http, classifyFailure(502, true, 0, 300, 10000));
    TEST_ASSERT_EQUAL(Fail::Http, classifyFailure(429, true, 0, 300, 10000));
    TEST_ASSERT_EQUAL(Fail::Frame, classifyFailure(200, true, 0, 300, 10000));
    // a handshake that went wrong shows as a refused connect to HTTPClient, but the TLS library knows
    TEST_ASSERT_EQUAL(Fail::Tls, classifyFailure(kHttpRefused, true, -0x7780, 900, 10000));
    // refused at once, or nothing for the whole connect time
    TEST_ASSERT_EQUAL(Fail::Refused, classifyFailure(kHttpRefused, true, 0, 120, 10000));
    TEST_ASSERT_EQUAL(Fail::Timeout, classifyFailure(kHttpRefused, true, 0, 9800, 10000));
    TEST_ASSERT_EQUAL(Fail::Timeout, classifyFailure(kHttpReadTimeout, true, 0, 15000, 10000));
    TEST_ASSERT_EQUAL(Fail::Other, classifyFailure(-7, true, 0, 100, 10000));

    TEST_ASSERT_EQUAL_STRING("DNS failed", failureText(Fail::Dns).c_str());
    TEST_ASSERT_EQUAL_STRING("TLS failed", failureText(Fail::Tls).c_str());
    TEST_ASSERT_EQUAL_STRING("refused", failureText(Fail::Refused).c_str());
    TEST_ASSERT_EQUAL_STRING("timeout", failureText(Fail::Timeout).c_str());
    TEST_ASSERT_EQUAL_STRING("HTTP 502", failureText(Fail::Http, 502).c_str());
    TEST_ASSERT_EQUAL_STRING("no WiFi", failureText(Fail::NoWifi).c_str());
    TEST_ASSERT_EQUAL_STRING("", failureText(Fail::None).c_str());
    for (const Fail f : {Fail::NoWifi, Fail::Dns, Fail::Tls, Fail::Refused, Fail::Timeout, Fail::Http, Fail::Frame, Fail::Other})
        for (const char c : failureText(f, 503)) TEST_ASSERT_TRUE(static_cast<unsigned char>(c) < 128);  // ASCII only: the e-ink fonts may lack more
}

static void test_the_banner_says_the_reason_in_ascii() {
    TEST_ASSERT_EQUAL_STRING("Улей: DNS failed, 40 мин", offlineBanner(Offline::NoHive, {40, true}, true, "DNS failed").c_str());
    TEST_ASSERT_EQUAL_STRING("Улей: HTTP 502, данные 3 ч назад", offlineBanner(Offline::NoHive, {180, true}, false, "HTTP 502").c_str());
    TEST_ASSERT_EQUAL_STRING("Улей не отвечает, ~3 ч", offlineBanner(Offline::NoHive, {180, false}, true, "").c_str());  // no reason known: as before
    TEST_ASSERT_EQUAL_STRING("нет Wi-Fi, 40 мин", offlineBanner(Offline::NoWifi, {40, true}, true, "no WiFi").c_str());    // the cause says it already
}

static void test_the_host_of_an_address() {
    TEST_ASSERT_EQUAL_STRING("hive.x.keenetic.pro", hostOf("https://hive.x.keenetic.pro/api/monitor/v1/frame.bin").c_str());
    TEST_ASSERT_EQUAL_STRING("192.168.1.5", hostOf("http://192.168.1.5:8088/api").c_str());
    TEST_ASSERT_EQUAL_STRING("h", hostOf("https://user:pw@h:8443").c_str());
    TEST_ASSERT_EQUAL_STRING("::1", hostOf("http://[::1]:80/x").c_str());
    TEST_ASSERT_EQUAL_STRING("h.example", hostOf("https://h.example?x=1").c_str());
    TEST_ASSERT_EQUAL_STRING("", hostOf("hive.x").c_str());
}

static void test_mac_as_the_hive_shows_it() {
    const uint8_t bytes[6] = {0xA0, 0xB7, 0x65, 0x12, 0x34, 0x0F};
    TEST_ASSERT_EQUAL_STRING("A0:B7:65:12:34:0F", macText(bytes).c_str());
}

// The console window after a power-on or a reset (the hive's page flashes and configures the panel in it).
static void test_console_window_stays_open_while_somebody_is_there() {
    ConsoleWindow w;
    TEST_ASSERT_TRUE(w.open(0, false));
    TEST_ASSERT_TRUE(w.open(11999, false));
    TEST_ASSERT_FALSE(w.open(12000, false));  // nobody came: as before, 12 s
    // a line typed at 10 s keeps it open for a minute more
    w.touch(10000);
    TEST_ASSERT_TRUE(w.open(12000, false));
    TEST_ASSERT_TRUE(w.open(69999, false));
    TEST_ASSERT_FALSE(w.open(70000, false));
    // more input moves the end on
    w.touch(60000);
    TEST_ASSERT_TRUE(w.open(119999, false));
    TEST_ASSERT_FALSE(w.open(120000, false));
}

static void test_console_window_waits_for_a_usb_host_but_not_forever() {
    ConsoleWindow w;
    TEST_ASSERT_TRUE(w.open(12000, true));    // a computer is attached: no end at 12 s
    TEST_ASSERT_TRUE(w.open(200000, true));
    TEST_ASSERT_FALSE(w.open(ConsoleWindow::kMaxMs, true));  // a panel left on a computer still reads, after five minutes
    w.touch(290000);
    TEST_ASSERT_FALSE(w.open(ConsoleWindow::kMaxMs + 1, false));  // typing never goes past the limit either
    TEST_ASSERT_FALSE(w.open(30000, false));  // no host, no input: closed
    TEST_ASSERT_TRUE(ConsoleWindow::kActiveMs >= 60000);
}

static void test_console_says_ready_twice_a_second() {
    TEST_ASSERT_TRUE(ConsoleWindow::readyDue(0, 0));
    TEST_ASSERT_FALSE(ConsoleWindow::readyDue(499, 1));
    TEST_ASSERT_TRUE(ConsoleWindow::readyDue(501, 1));
    TEST_ASSERT_FALSE(ConsoleWindow::readyDue(700, 400));
}

// Wired mode: a table of what the panel sees (mode, cell mV now, cell mV at the previous read, USB host) and what it decides.
static void test_wired_mode_table() {
    struct Row {
        WiredMode mode;
        int mv, last;
        bool usb;
        bool wired;
        const char* why;
    };
    const Row rows[] = {
        {WiredMode::Off, 4200, 4100, true, false, "off never stays awake"},
        {WiredMode::On, 3600, 3700, false, true, "on always does"},
        {WiredMode::Auto, 3700, 3700, true, true, "a USB host is external power"},
        {WiredMode::Auto, 3700, 0, false, false, "on the cell, first read"},
        {WiredMode::Auto, 3700, 3705, false, false, "on the cell, slowly falling"},
        {WiredMode::Auto, 3700, 3698, false, false, "on the cell, steady"},
        {WiredMode::Auto, 3750, 3700, false, true, "rising 50 mV: the charger works"},
        {WiredMode::Auto, 3708, 3700, false, true, "rising exactly 8 mV counts"},
        {WiredMode::Auto, 3707, 3700, false, false, "7 mV is noise"},
        {WiredMode::Auto, 4200, 4200, false, true, "full on the charger, steady"},
        {WiredMode::Auto, 4160, 0, false, true, "first read, high: counts as wired"},
        {WiredMode::Auto, 4150, 4155, false, true, "high and nearly flat"},
        {WiredMode::Auto, 4150, 4165, false, false, "high but falling 15 mV: unplugged"},
        {WiredMode::Auto, 4140, 4140, false, false, "just under the full level, flat"},
        {WiredMode::Auto, 4300, 4300, false, true, "a high reading in range"},
        {WiredMode::Auto, 5200, 4100, false, false, "nonsense above range"},
        {WiredMode::Auto, 0, 0, false, false, "no divider soldered"},
        {WiredMode::Auto, 0, 0, true, true, "no divider, but a USB host"},
        {WiredMode::Auto, 3000, 3500, false, false, "falling a lot"},
    };
    for (const Row& r : rows) {
        char message[96];
        snprintf(message, sizeof message, "%s", r.why);
        TEST_ASSERT_EQUAL_MESSAGE(r.wired, wiredNow(r.mode, r.mv, r.last, r.usb), message);
    }
}

static void test_wired_mode_setting() {
    TEST_ASSERT_TRUE(parseWiredMode("auto", WiredMode::Off) == WiredMode::Auto);
    TEST_ASSERT_TRUE(parseWiredMode("off", WiredMode::Auto) == WiredMode::Off);
    TEST_ASSERT_TRUE(parseWiredMode("on", WiredMode::Auto) == WiredMode::On);
    TEST_ASSERT_TRUE(parseWiredMode("sometimes", WiredMode::Off) == WiredMode::Off);  // junk keeps what there was
    TEST_ASSERT_EQUAL_STRING("auto", wiredModeName(WiredMode::Auto));
    TEST_ASSERT_EQUAL_STRING("off", wiredModeName(WiredMode::Off));
    TEST_ASSERT_EQUAL_STRING("on", wiredModeName(WiredMode::On));
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_frame_url_from_what_people_paste);
    RUN_TEST(test_interval_stays_sane);
    RUN_TEST(test_offline_banner_at_once_then_on_change_or_every_half_hour);
    RUN_TEST(test_retry_backs_off_to_half_an_hour);
    RUN_TEST(test_age_of_the_data);
    RUN_TEST(test_battery_percent);
    RUN_TEST(test_answers);
    RUN_TEST(test_tabs);
    RUN_TEST(test_tab_weights);
    RUN_TEST(test_clock_and_battery);
    RUN_TEST(test_a_tab_read_lately_is_not_asked_for_again);
    RUN_TEST(test_why_the_hive_did_not_answer);
    RUN_TEST(test_the_banner_says_the_reason_in_ascii);
    RUN_TEST(test_the_host_of_an_address);
    RUN_TEST(test_mac_as_the_hive_shows_it);
    RUN_TEST(test_console_window_stays_open_while_somebody_is_there);
    RUN_TEST(test_console_window_waits_for_a_usb_host_but_not_forever);
    RUN_TEST(test_console_says_ready_twice_a_second);
    RUN_TEST(test_wired_mode_table);
    RUN_TEST(test_wired_mode_setting);
    return UNITY_END();
}
