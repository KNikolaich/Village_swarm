// Remote monitor on the Elecrow CrowPanel ESP32 3.7" e-paper (UC8253, 240x416 black and white, a rocker with a press, MENU and
// EXIT): a panel far from the hive with the tabs the hive draws (contracts/monitor-http.md, tabs; X-Frame-Tabs says how
// many: 0 Weather, 1 Measure (the person's own layout), 2 Calendar). No MQTT, no enrollment: added by hand on the hive's
// page and given a read-only token there, like the Waveshare 1.54" (remote_epd154.cpp, whose settings point and console it
// shares).
//
// The rocker moves a cursor over the tab bar (the panel inverts the cell itself, by X-Frame-Tab-Weights, and turns the shown tab's cell back to
// white while the cursor is elsewhere), its press chooses the tab: it is shown at once from the flash and then read anew.
// The tabs are kept in LittleFS, so flipping needs no Wi-Fi. In between the panel sleeps for its interval, or less while the
// hive monitors it (X-Frame-Refresh). Every picture is a partial refresh from the one on the screen, with a full one now
// and then against ghosting.
//
// No link: the first failed read puts a banner with the cause and the data's age on the tab at once, then the tries come later
// each time (5, 10, 20, 30 min); going to sleep offline shows the person's sleep picture kept from the last time, or a white
// "Нет связи" screen (docs/display.md, "Нет связи").
//
// Buttons: rocker up/down = cursor, rocker press = choose, EXIT short = sleep until a button (or leave the cursor), EXIT for
// 2 s = the help screen, MENU short = read now, MENU for 2 s = the book reader (reader_crowpanel37.cpp: books from the TF card,
// no Wi-Fi; EXIT for 2 s there comes back here). The settings point (Wi-Fi "VillageSwarm-xxxx", http://192.168.4.1): MENU held
// 2 s on the help screen, or by itself while the panel is not set up.
// Console (115200, lines): set ssid|pass|ssid2|pass2|url|token|interval|insecure <value>, show, go, save; as remote_epd154.cpp.
//
// Battery: the board does not bring it to a pin. With a 2 x 220 kOhm divider from B+ to GPIO8 (100 nF across the lower one)
// the panel tells it with every read (X-Battery-mV) and the hive draws it; without one it says nothing.
//
// Pins from the Elecrow examples and schematic (github.com/Elecrow-RD/CrowPanel-ESP32-3.7-E-paper-HMI-Display-with-240-416).

#include <Adafruit_GFX.h>
#include <Arduino.h>
#include <DNSServer.h>
#include <HTTPClient.h>
#include <LittleFS.h>
#include <Preferences.h>
#include <U8g2_for_Adafruit_GFX.h>
#include <WebServer.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <driver/gpio.h>
#include <driver/rtc_io.h>
#include <esp_sleep.h>
#include <esp_timer.h>
#include <soc/gpio_reg.h>
#include <sys/time.h>
#include <time.h>

#include "../../lib/hornet-core/src/RemoteMonitor.h"
#include "crowpanel37.h"

namespace {

using namespace hornet::remote;

// The panel's own size (portrait); the hive turns the landscape picture to it.
constexpr int kWidth = 240;
constexpr int kHeight = 416;
constexpr size_t kFrameBytes = frameBytes(kWidth, kHeight);

constexpr int kPinSck = 12, kPinMosi = 11, kPinCs = 45, kPinDc = 46, kPinRst = 47, kPinBusy = 48;
constexpr gpio_num_t kPinEpdPower = GPIO_NUM_7;  // HIGH = e-paper powered
constexpr gpio_num_t kPinLed = GPIO_NUM_41;      // the power LED: off, it only drains the battery
constexpr gpio_num_t kPinSdPower = GPIO_NUM_42;  // the TF card: off but in the reader
constexpr gpio_num_t kPinExit = GPIO_NUM_1;
constexpr gpio_num_t kPinMenu = GPIO_NUM_2;
constexpr gpio_num_t kPinDown = GPIO_NUM_4;
constexpr gpio_num_t kPinConf = GPIO_NUM_5;
constexpr gpio_num_t kPinUp = GPIO_NUM_6;
const char kLoadingText[] = "загрузка...";  // the mark on a tab while it is being read
constexpr int kPinBattery = 8;  // ADC1, through the divider the person solders (see above)

constexpr uint32_t kWifiTimeoutMs = 15000;
constexpr uint32_t kSleepWifiMs = 7000;  // going to sleep: one short try for Wi-Fi
constexpr uint32_t kPortalTimeoutMs = 10UL * 60 * 1000;
constexpr uint32_t kCursorTimeoutMs = 8000;
constexpr uint32_t kBadTokenSleepMin = 6 * 60;
constexpr int kPartialsBeforeFull = 30;  // a full refresh now and then clears the ghosts partial ones leave

// The hive's internet entrance (KeenDNS) has a Let's Encrypt certificate: its two roots. Inside the house the hive uses
// its own CA: tick "не проверять сертификат" or use http:// there.
const char kRootCa[] = R"PEM(-----BEGIN CERTIFICATE-----
MIIFazCCA1OgAwIBAgIRAIIQz7DSQONZRGPgu2OCiwAwDQYJKoZIhvcNAQELBQAw
TzELMAkGA1UEBhMCVVMxKTAnBgNVBAoTIEludGVybmV0IFNlY3VyaXR5IFJlc2Vh
cmNoIEdyb3VwMRUwEwYDVQQDEwxJU1JHIFJvb3QgWDEwHhcNMTUwNjA0MTEwNDM4
WhcNMzUwNjA0MTEwNDM4WjBPMQswCQYDVQQGEwJVUzEpMCcGA1UEChMgSW50ZXJu
ZXQgU2VjdXJpdHkgUmVzZWFyY2ggR3JvdXAxFTATBgNVBAMTDElTUkcgUm9vdCBY
MTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAK3oJHP0FDfzm54rVygc
h77ct984kIxuPOZXoHj3dcKi/vVqbvYATyjb3miGbESTtrFj/RQSa78f0uoxmyF+
0TM8ukj13Xnfs7j/EvEhmkvBioZxaUpmZmyPfjxwv60pIgbz5MDmgK7iS4+3mX6U
A5/TR5d8mUgjU+g4rk8Kb4Mu0UlXjIB0ttov0DiNewNwIRt18jA8+o+u3dpjq+sW
T8KOEUt+zwvo/7V3LvSye0rgTBIlDHCNAymg4VMk7BPZ7hm/ELNKjD+Jo2FR3qyH
B5T0Y3HsLuJvW5iB4YlcNHlsdu87kGJ55tukmi8mxdAQ4Q7e2RCOFvu396j3x+UC
B5iPNgiV5+I3lg02dZ77DnKxHZu8A/lJBdiB3QW0KtZB6awBdpUKD9jf1b0SHzUv
KBds0pjBqAlkd25HN7rOrFleaJ1/ctaJxQZBKT5ZPt0m9STJEadao0xAH0ahmbWn
OlFuhjuefXKnEgV4We0+UXgVCwOPjdAvBbI+e0ocS3MFEvzG6uBQE3xDk3SzynTn
jh8BCNAw1FtxNrQHusEwMFxIt4I7mKZ9YIqioymCzLq9gwQbooMDQaHWBfEbwrbw
qHyGO0aoSCqI3Haadr8faqU9GY/rOPNk3sgrDQoo//fb4hVC1CLQJ13hef4Y53CI
rU7m2Ys6xt0nUW7/vGT1M0NPAgMBAAGjQjBAMA4GA1UdDwEB/wQEAwIBBjAPBgNV
HRMBAf8EBTADAQH/MB0GA1UdDgQWBBR5tFnme7bl5AFzgAiIyBpY9umbbjANBgkq
hkiG9w0BAQsFAAOCAgEAVR9YqbyyqFDQDLHYGmkgJykIrGF1XIpu+ILlaS/V9lZL
ubhzEFnTIZd+50xx+7LSYK05qAvqFyFWhfFQDlnrzuBZ6brJFe+GnY+EgPbk6ZGQ
3BebYhtF8GaV0nxvwuo77x/Py9auJ/GpsMiu/X1+mvoiBOv/2X/qkSsisRcOj/KK
NFtY2PwByVS5uCbMiogziUwthDyC3+6WVwW6LLv3xLfHTjuCvjHIInNzktHCgKQ5
ORAzI4JMPJ+GslWYHb4phowim57iaztXOoJwTdwJx4nLCgdNbOhdjsnvzqvHu7Ur
TkXWStAmzOVyyghqpZXjFaH3pO3JLF+l+/+sKAIuvtd7u+Nxe5AW0wdeRlN8NwdC
jNPElpzVmbUq4JUagEiuTDkHzsxHpFKVK7q4+63SM1N95R1NbdWhscdCb+ZAJzVc
oyi3B43njTOQ5yOf+1CceWxG1bQVs5ZufpsMljq4Ui0/1lvh+wjChP4kqKOJ2qxq
4RgqsahDYVvTH9w7jXbyLeiNdd8XM2w9U/t7y0Ff/9yi0GE44Za4rF2LN9d11TPA
mRGunUHBcnWEvgJBQl9nJEiU0Zsnvgc/ubhPgXRR4Xq37Z0j4r7g1SgEEzwxA57d
emyPxgcYxn/eR44/KJ4EBs+lVDR3veyJm+kXQ99b21/+jh5Xos1AnX5iItreGCc=
-----END CERTIFICATE-----
-----BEGIN CERTIFICATE-----
MIICGzCCAaGgAwIBAgIQQdKd0XLq7qeAwSxs6S+HUjAKBggqhkjOPQQDAzBPMQsw
CQYDVQQGEwJVUzEpMCcGA1UEChMgSW50ZXJuZXQgU2VjdXJpdHkgUmVzZWFyY2gg
R3JvdXAxFTATBgNVBAMTDElTUkcgUm9vdCBYMjAeFw0yMDA5MDQwMDAwMDBaFw00
MDA5MTcxNjAwMDBaME8xCzAJBgNVBAYTAlVTMSkwJwYDVQQKEyBJbnRlcm5ldCBT
ZWN1cml0eSBSZXNlYXJjaCBHcm91cDEVMBMGA1UEAxMMSVNSRyBSb290IFgyMHYw
EAYHKoZIzj0CAQYFK4EEACIDYgAEzZvVn4CDCuwJSvMWSj5cz3es3mcFDR0HttwW
+1qLFNvicWDEukWVEYmO6gbf9yoWHKS5xcUy4APgHoIYOIvXRdgKam7mAHf7AlF9
ItgKbppbd9/w+kHsOdx1ymgHDB/qo0IwQDAOBgNVHQ8BAf8EBAMCAQYwDwYDVR0T
AQH/BAUwAwEB/zAdBgNVHQ4EFgQUfEKWrt5LSDv6kviejM9ti6lyN5UwCgYIKoZI
zj0EAwMDaAAwZQIwe3lORlCEwkSHRhtFcP9Ymd70/aTSVaYgLXTWNLxBo1BfASdW
tL4ndQavEi51mI38AjEAi/V3bNTIZargCyzuFJ0nN6T5U6VR5CmD1/iQMVtCnwr1
/q4AaOeMSQ+2b1tbFfLn
-----END CERTIFICATE-----
)PEM";

// Kept across deep sleep (lost when the power goes). The pictures themselves are in LittleFS: too big for RTC memory.
RTC_DATA_ATTR int currentTab = 0;
RTC_DATA_ATTR int tabs = 3;              // how many the hive says (X-Frame-Tabs)
RTC_DATA_ATTR uint8_t tabWeights[kMaxTabs] = {};  // how wide the cells of the tab bar are (X-Frame-Tab-Weights)
RTC_DATA_ATTR int weightCount = 0;       // how many weights came: not as many as tabs = equal cells
RTC_DATA_ATTR uint32_t refreshS = 0;     // the hive's monitoring: read this often (X-Frame-Refresh), 0 = own interval
RTC_DATA_ATTR int rotation = 270;        // how the hive turned the picture (X-Frame-Rotation): the panel's marks follow it
RTC_DATA_ATTR uint32_t clockUnix = 0;    // the hive's time at the last read, plus what was slept since
RTC_DATA_ATTR uint32_t offlineMin = 0;  // minutes slept since the last read that worked: the data's age while the clock is not known
RTC_DATA_ATTR uint32_t failures = 0;    // failed reads in a row: the next try is later each time (retryMinutes)
RTC_DATA_ATTR Fail lastFail = Fail::None;  // why the last read failed (RemoteMonitor.h): the banner says it, "show" too
RTC_DATA_ATTR int lastFailCode = 0;        // its HTTP code or HTTPClient error
RTC_DATA_ATTR Offline bannerCause = Offline::None;  // what the screen says about the link now (None: nothing)
RTC_DATA_ATTR uint32_t bannerAgeMin = 0;            // the data's age it says
RTC_DATA_ATTR int partials = kPartialsBeforeFull;  // full refresh first after power-on
RTC_DATA_ATTR bool messageShown = false;
RTC_DATA_ATTR uint32_t tabReadAt[kMaxTabs] = {};  // the hive's unix time at the last read of each tab (0: not known)
RTC_DATA_ATTR bool loadingShown = false;           // the "загрузка..." mark is on the screen: the result must take it off
RTC_DATA_ATTR uint8_t apBssid[6] = {};  // the access point of the last connection: the next one skips the scan
RTC_DATA_ATTR int apChannel = 0;
RTC_DATA_ATTR int apSsid = -1;
RTC_DATA_ATTR bool asleep = false;       // put to sleep by a short EXIT: the next button only wakes the panel

struct Settings {
    String ssid[2], pass[2], url, token;
    int intervalMin = kDefaultIntervalMin;
    bool insecure = false;

    bool ready() const { return ssid[0].length() > 0 && frameUrl(url.c_str()).length() > 0 && token.length() > 0; }

    void load() {
        Preferences p;
        p.begin("rmon", true);
        for (int i = 0; i < 2; i++) {
            ssid[i] = p.getString(i ? "ssid2" : "ssid1", "");
            pass[i] = p.getString(i ? "pass2" : "pass1", "");
        }
        url = p.getString("url", "");
        token = p.getString("token", "");
        intervalMin = clampInterval(p.getInt("interval", kDefaultIntervalMin));
        insecure = p.getBool("insecure", false);
        p.end();
    }

    void save() const {
        Preferences p;
        p.begin("rmon", false);
        p.putString("ssid1", ssid[0]);
        p.putString("pass1", pass[0]);
        p.putString("ssid2", ssid[1]);
        p.putString("pass2", pass[1]);
        p.putString("url", url);
        p.putString("token", token);
        p.putInt("interval", intervalMin);
        p.putBool("insecure", insecure);
        p.end();
    }
} settings;

// ------------------------------------------------------------------ the pictures in flash

String tabPath(int tab) { return String("/t") + tab + ".bin"; }
String tagPath(int tab) { return String("/t") + tab + ".tag"; }
const char* kShownPath = "/shown.bin";  // what is on the screen: the "old" picture of a partial refresh
const char* kSleepPath = "/sleep.bin";  // the person's sleep picture as the hive last sent it: shown when the panel goes to sleep offline

bool readFile(const String& path, uint8_t* into, size_t size) {
    if (!LittleFS.exists(path)) return false;
    File f = LittleFS.open(path, "r");
    if (!f) return false;
    const bool ok = f.size() == size && f.read(into, size) == size;
    f.close();
    return ok;
}

bool writeFile(const String& path, const uint8_t* data, size_t size) {
    File f = LittleFS.open(path, "w");
    const bool ok = f && f.write(data, size) == size;
    if (f) f.close();
    if (!ok) {
        Serial.printf("[monitor] flash: could not write %s\n", path.c_str());
        LittleFS.remove(path);  // half a picture is worse than none
    }
    return ok;
}

/// A whole tab in the flash. One of another size (cut short by a power loss) goes, with its ETag: else the hive would answer 304 for a
/// picture the panel does not have.
bool haveTab(int tab) {
    if (!LittleFS.exists(tabPath(tab))) return false;
    File f = LittleFS.open(tabPath(tab), "r");
    const bool whole = f && f.size() == kFrameBytes;
    if (f) f.close();
    if (!whole) {
        Serial.printf("[monitor] flash: tab %d is broken, dropped\n", tab);
        LittleFS.remove(tabPath(tab));
        LittleFS.remove(tagPath(tab));
    }
    return whole;
}

String readTag(int tab) {
    if (!LittleFS.exists(tagPath(tab))) return String();
    File f = LittleFS.open(tagPath(tab), "r");
    if (!f) return String();
    String tag = f.readString();
    f.close();
    return tag;
}

void writeTag(int tab, const String& tag) {
    File f = LittleFS.open(tagPath(tab), "w");
    if (!f) return;
    f.print(tag);
    f.close();
}

void forgetTabs() {
    for (int t = 0; t < kMaxTabs; t++) {
        LittleFS.remove(tagPath(t));
        tabReadAt[t] = 0;
    }
}

// ------------------------------------------------------------------ UC8253 (as the Elecrow examples drive it)

void epdWrite(uint8_t value) {
    digitalWrite(kPinCs, LOW);
    for (int i = 0; i < 8; i++) {
        digitalWrite(kPinSck, LOW);
        digitalWrite(kPinMosi, (value & 0x80) ? HIGH : LOW);
        digitalWrite(kPinSck, HIGH);
        value <<= 1;
    }
    digitalWrite(kPinCs, HIGH);
}

void epdCommand(uint8_t command) {
    digitalWrite(kPinDc, LOW);
    epdWrite(command);
    digitalWrite(kPinDc, HIGH);
}

void epdData(uint8_t data) { epdWrite(data); }

/// BUSY is low while the panel works; a refresh takes up to a few seconds. Bounded, so a missing panel cannot hang the board.
void epdWait() {
    const uint32_t started = millis();
    while (digitalRead(kPinBusy) == LOW && millis() - started < 10000) delay(5);
}

void epdReset() {
    digitalWrite(kPinRst, LOW);
    delay(20);
    digitalWrite(kPinRst, HIGH);
    delay(20);
    epdWait();
}

/// Fast (full) or partial waveform; the panel keeps nothing after its deep sleep, so both get the old picture again.
void epdInit(bool partial) {
    epdReset();
    epdCommand(0x00);
    epdData(0x1B);
    epdCommand(0xE0);
    epdData(0x02);
    epdCommand(0xE5);
    epdData(partial ? 0x6E : 0x5F);
    if (partial) {
        epdCommand(0x50);  // white border
        epdData(0xD7);
    }
}

/// Our pictures: a set bit is black. The panel's: a set bit is white. With the gate scan set as above the panel
/// fills its rows from the far end, so the rows go last first, or the picture comes out mirrored.
void epdSend(uint8_t command, const uint8_t* bits) {
    constexpr size_t row = kWidth / 8;
    epdCommand(command);
    for (int y = kHeight - 1; y >= 0; y--)
        for (size_t i = 0; i < row; i++) epdData(static_cast<uint8_t>(~bits[y * row + i]));
}

void epdSleep() {
    epdCommand(0x02);  // power off
    epdWait();
    epdCommand(0x07);  // deep sleep
    epdData(0xA5);
}

uint8_t shown[kFrameBytes];     // on the screen now
uint8_t picture[kFrameBytes];   // to be shown
bool haveShown = false;
bool shownUnsaved = false;      // the reader turns pages faster than the flash should be written: kept when going to sleep

/// Puts <picture> on the screen: partial from what is there, full now and then (and when nothing is known of the screen).
void present() {
    const bool full = !haveShown || partials >= kPartialsBeforeFull;
    epdInit(!full);
    epdSend(0x10, haveShown ? shown : picture);
    epdSend(0x13, picture);
    epdCommand(0x04);  // power on
    epdWait();
    epdCommand(0x12);  // refresh
    epdWait();
    epdSleep();
    partials = full ? 0 : partials + 1;
    memcpy(shown, picture, kFrameBytes);
    haveShown = true;
    shownUnsaved = true;
}

/// What is on the screen into the flash (the old picture of the next partial refresh after deep sleep).
void keepShown() {
    if (shownUnsaved && writeFile(kShownPath, shown, kFrameBytes)) shownUnsaved = false;
}

// ------------------------------------------------------------------ marks the panel draws itself (in the hive's turn)

GFXcanvas1 canvas(kWidth, kHeight);  // its buffer is a frame: 30 bytes a row, high bit first, 1 = black
U8G2_FOR_ADAFRUIT_GFX text;

/// Starts drawing on <picture> in the hive's turn (GFX rotation 1 = 90° clockwise, as the hive turns it).
void beginMarks() {
    memcpy(canvas.getBuffer(), picture, kFrameBytes);
    canvas.setRotation((rotation / 90) & 3);
    text.begin(canvas);
    text.setFontMode(1);
}

void endMarks() { memcpy(picture, canvas.getBuffer(), kFrameBytes); }

void invertCell(int tab) {
    const Cell cell = tabCell(tab, tabs, canvas.width(), canvas.height(), weightCount == tabs ? tabWeights : nullptr);
    for (int y = cell.y + 1; y < cell.y + cell.h; y++)
        for (int x = cell.x; x < cell.x + cell.w; x++) canvas.drawPixel(x, y, canvas.getPixel(x, y) ? 0 : 1);
}

/// The cursor: only its cell is black. The hive drew the shown tab's cell black: away from it, that one turns white again.
void markCursor(int tab) {
    if (tab == currentTab) return;
    invertCell(tab);
    invertCell(currentTab);
}

void markBanner(const String& line) {
    const int top = canvas.height() - kTabBarHeight - 20;
    canvas.fillRect(0, top, canvas.width(), 20, 1);
    text.setFont(u8g2_font_9x15_t_cyrillic);
    text.setForegroundColor(0);
    text.setBackgroundColor(1);
    text.setCursor(6, top + 15);
    text.print(line);
}

/// A full-screen message, landscape: a title and up to six short lines.
void showMessage(const char* title, std::initializer_list<String> lines) {
    memset(picture, 0, kFrameBytes);
    beginMarks();
    text.setForegroundColor(1);
    text.setBackgroundColor(0);
    text.setFont(u8g2_font_10x20_t_cyrillic);
    text.setCursor(12, 32);
    text.print(title);
    canvas.drawFastHLine(12, 42, canvas.width() - 24, 1);
    text.setFont(u8g2_font_9x15_t_cyrillic);
    int y = 70;
    for (const String& line : lines) {
        text.setCursor(12, y);
        text.print(line);
        y += 22;
    }
    endMarks();
    present();
    keepShown();
    messageShown = true;
    loadingShown = false;
    bannerCause = Offline::None;
}

/// <picture> (a tab) on the screen, with the cursor or a banner when asked.
void showPicture(int cursor, const String& banner) {
    if (cursor >= 0 || banner.length()) {
        beginMarks();
        if (cursor >= 0) markCursor(cursor);
        if (banner.length()) markBanner(banner);
        endMarks();
    }
    present();
    keepShown();
    messageShown = false;
    loadingShown = banner == kLoadingText;
    bannerCause = Offline::None;
}

/// A tab from the flash on the screen, with the cursor or a banner when asked.
bool showTab(int tab, int cursor, const String& banner) {
    if (!readFile(tabPath(tab), picture, kFrameBytes)) return false;
    showPicture(cursor, banner);
    return true;
}

/// The battery for a message: "Батарея 80%", or nothing without the divider.
String batteryLine(int mv) {
    const int percent = batteryPercent(mv);
    return percent < 0 ? String() : "Батарея " + String(percent) + "%";
}

// ------------------------------------------------------------------ console over USB

#ifndef HORNET_FW_VERSION
#define HORNET_FW_VERSION ""
#endif

String deviceMac();  // below, with the headers of a read

[[noreturn]] void readAndSleep(bool loading = false);

String consoleLine;

enum class Order { None, Go, Save };

Order runCommand(String line) {
    line.trim();
    if (line.isEmpty()) return Order::None;
    if (line == "go" || line == "save") {
        if (!settings.ready()) {
            Serial.println("[monitor] error: need ssid, url (https://...) and token first");
            return Order::None;
        }
        settings.save();
        forgetTabs();
        Serial.println("[monitor] saved");
        return line == "go" ? Order::Go : Order::Save;
    }
    if (line == "show") {
        Serial.printf("[monitor] ssid=%s ssid2=%s url=%s token=%s interval=%d insecure=%d tab=%d\n", settings.ssid[0].c_str(),
                      settings.ssid[1].c_str(), settings.url.c_str(), settings.token.isEmpty() ? "-" : "set", settings.intervalMin,
                      settings.insecure, currentTab);
        Serial.printf("[monitor] link: %s, code %d, failures %lu, mac=%s, fw=%s\n", lastFail == Fail::None ? "ok" : failureText(lastFail, lastFailCode).c_str(),
                      lastFailCode, static_cast<unsigned long>(failures), deviceMac().c_str(), HORNET_FW_VERSION);
        return Order::None;
    }
    if (!line.startsWith("set ")) {
        Serial.println("[monitor] error: commands are set <key> <value>, show, go");
        return Order::None;
    }
    const int space = line.indexOf(' ', 4);
    const String key = space < 0 ? line.substring(4) : line.substring(4, space);
    String value = space < 0 ? "" : line.substring(space + 1);
    if (key == "ssid") settings.ssid[0] = value;
    else if (key == "pass") settings.pass[0] = value;
    else if (key == "ssid2") settings.ssid[1] = value;
    else if (key == "pass2") settings.pass[1] = value;
    else if (key == "url") settings.url = value;
    else if (key == "token") settings.token = value;
    else if (key == "interval") settings.intervalMin = clampInterval(value.toInt());
    else if (key == "rot") {
        // the hive turns the picture (its page); kept so the hive's setup page may send it like for the 1.54"
    } else if (key == "insecure") settings.insecure = value == "1";
    else {
        Serial.printf("[monitor] error: unknown key %s\n", key.c_str());
        return Order::None;
    }
    Serial.println("[monitor] ok");
    return Order::None;
}

Order pollConsole(bool& activity) {
    while (Serial.available()) {
        const char c = static_cast<char>(Serial.read());
        activity = true;
        if (c == '\n') {
            const String line = consoleLine;
            consoleLine = "";
            const Order order = runCommand(line);
            if (order != Order::None) return order;
        } else if (c != '\r' && consoleLine.length() < 300) {
            consoleLine += c;
        }
    }
    return Order::None;
}

// ------------------------------------------------------------------ power and buttons

constexpr uint32_t kHelpTimeoutMs = 30000;  // the help screen waits this long for a button

constexpr gpio_num_t kButtons[] = {kPinExit, kPinMenu, kPinDown, kPinConf, kPinUp};

// Presses that come while the panel is busy (Wi-Fi, the hive's answer, the screen refresh) are counted by interrupts, so none is lost:
// the rocker's up and down and the press. A falling edge counts when the button had been up for 40 ms (bounce is not a press).
// The handler runs from IRAM and may come while the flash is written, so it touches only DRAM and IRAM-safe calls
// (the input register, esp_timer_get_time), and the counters are shared under a spinlock.
volatile uint8_t pendingPress[3] = {0, 0, 0};  // up, down, press
volatile int64_t lastEdgeUs[3] = {0, 0, 0};
DRAM_ATTR const gpio_num_t kWatched[3] = {kPinUp, kPinDown, kPinConf};  // all below GPIO 32
portMUX_TYPE pressMux = portMUX_INITIALIZER_UNLOCKED;

void IRAM_ATTR onEdge(void* arg) {
    const int i = static_cast<int>(reinterpret_cast<intptr_t>(arg));
    const int64_t now = esp_timer_get_time();
    const bool down = (REG_READ(GPIO_IN_REG) & (1UL << kWatched[i])) == 0;
    portENTER_CRITICAL_ISR(&pressMux);
    if (down && now - lastEdgeUs[i] > 40000 && pendingPress[i] < 8) pendingPress[i] = pendingPress[i] + 1;
    lastEdgeUs[i] = now;
    portEXIT_CRITICAL_ISR(&pressMux);
}

void watchButtons() {
    for (int i = 0; i < 3; i++) attachInterruptArg(kWatched[i], onEdge, reinterpret_cast<void*>(static_cast<intptr_t>(i)), CHANGE);
}

/// Steps of the cursor asked for meanwhile: down minus up; takes them.
int takeSteps() {
    portENTER_CRITICAL(&pressMux);
    const int steps = static_cast<int>(pendingPress[1]) - static_cast<int>(pendingPress[0]);
    pendingPress[0] = 0;
    pendingPress[1] = 0;
    portEXIT_CRITICAL(&pressMux);
    return steps;
}

bool takePress() {
    portENTER_CRITICAL(&pressMux);
    const bool was = pendingPress[2] > 0;
    pendingPress[2] = 0;
    portEXIT_CRITICAL(&pressMux);
    return was;
}

/// The person moved the rocker while the panel was busy: what it is busy with may no longer be wanted.
bool movedMeanwhile() { return pendingPress[0] != pendingPress[1]; }

void powerOn() {
    for (gpio_num_t pin : {kPinEpdPower, kPinLed, kPinSdPower}) gpio_hold_dis(pin);
    pinMode(kPinEpdPower, OUTPUT);
    digitalWrite(kPinEpdPower, HIGH);
    pinMode(kPinLed, OUTPUT);
    digitalWrite(kPinLed, LOW);
    pinMode(kPinSdPower, OUTPUT);
    digitalWrite(kPinSdPower, LOW);
    for (gpio_num_t pin : kButtons) pinMode(pin, INPUT);  // 4.7 kOhm pull-ups on the board, pressed = LOW
    pinMode(kPinSck, OUTPUT);
    pinMode(kPinMosi, OUTPUT);
    pinMode(kPinCs, OUTPUT);
    pinMode(kPinDc, OUTPUT);
    pinMode(kPinRst, OUTPUT);
    pinMode(kPinBusy, INPUT);
    digitalWrite(kPinCs, HIGH);
    digitalWrite(kPinRst, HIGH);
}

/// The battery through the divider on GPIO8, or -1 without one: a free pin floats, so it is first pulled down for a moment.
/// Behind the divider (110 kOhm) the pin still shows a few hundred millivolts then; a bare pin drops to nothing.
int readBatteryMv() {
    analogReadMilliVolts(kPinBattery);  // makes the pin an ADC input
    gpio_pulldown_en(static_cast<gpio_num_t>(kPinBattery));
    delay(20);
    const uint32_t probe = analogReadMilliVolts(kPinBattery);
    gpio_pulldown_dis(static_cast<gpio_num_t>(kPinBattery));
    if (probe < 200) return -1;
    delay(30);  // the 100 nF settles
    uint32_t sum = 0;
    for (int i = 0; i < 8; i++) sum += analogReadMilliVolts(kPinBattery);
    return batteryMv(static_cast<int>(sum / 8));
}

/// Deep sleep until the timer (seconds, 0 = none) or a button. The e-paper keeps its picture without power.
[[noreturn]] void sleepFor(uint32_t seconds) {
    WiFi.disconnect(true);
    WiFi.mode(WIFI_OFF);
    if (clockUnix) clockUnix += seconds + millis() / 1000;  // roughly: the next read sets it right
    digitalWrite(kPinEpdPower, LOW);
    for (gpio_num_t pin : {kPinEpdPower, kPinLed, kPinSdPower}) gpio_hold_en(pin);
    gpio_deep_sleep_hold_en();
    uint64_t mask = 0;
    for (gpio_num_t pin : kButtons) mask |= 1ULL << pin;
    esp_sleep_enable_ext1_wakeup(mask, ESP_EXT1_WAKEUP_ANY_LOW);
    if (seconds > 0) esp_sleep_enable_timer_wakeup(static_cast<uint64_t>(seconds) * 1000000ULL);
    Serial.flush();
    esp_deep_sleep_start();
}

/// Until the next read of the shown tab: the panel's interval, sooner while the hive monitors it.
uint32_t nextReadSeconds() { return hornet::remote::nextReadSeconds(settings.intervalMin, refreshS); }

bool pressed(gpio_num_t pin) { return digitalRead(pin) == LOW; }

/// Waits for every button to be let go (bounded: a stuck one must not keep the board awake).
void waitRelease() {
    const uint32_t started = millis();
    for (;;) {
        bool any = false;
        for (gpio_num_t pin : kButtons) any = any || pressed(pin);
        if (!any || millis() - started > 3000) break;
        delay(10);
    }
    delay(30);  // bounce
}

// ------------------------------------------------------------------ settings point (as remote_epd154.cpp)

WebServer server(80);
DNSServer dns;
String apName;
String networksHtml;
uint32_t portalStartedMs = 0;
uint32_t restartAtMs = 0;
bool portalMode = false;

String escapeHtml(const String& s) {
    String out;
    out.reserve(s.length() + 8);
    for (size_t i = 0; i < s.length(); i++) {
        const char c = s[i];
        if (c == '&') out += "&amp;";
        else if (c == '<') out += "&lt;";
        else if (c == '>') out += "&gt;";
        else if (c == '"') out += "&quot;";
        else out += c;
    }
    return out;
}

String pageHtml(const char* note) {
    String h;
    h.reserve(4096);
    h += F("<!doctype html><html lang=ru><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'>"
           "<title>Монитор Village Swarm</title><style>body{font-family:sans-serif;max-width:28em;margin:1em auto;padding:0 1em}"
           "label{display:block;margin-top:.8em;font-size:.9em}input,select{width:100%;padding:.4em;box-sizing:border-box}"
           "small{color:#666}button{margin-top:1.2em;padding:.6em 1.2em}</style><h2>Монитор Village Swarm</h2>");
    if (note && *note) h += String("<p><b>") + note + "</b></p>";
    h += F("<form method=post action=/save><datalist id=nets>");
    h += networksHtml;
    h += F("</datalist>");
    for (int i = 0; i < 2; i++) {
        h += String("<label>Wi-Fi ") + (i + 1) + (i ? " (запасная, можно пусто)" : "") + "<input name=ssid" + (i + 1) +
             " list=nets value=\"" + escapeHtml(settings.ssid[i]) + "\"></label>";
        h += String("<label>Пароль Wi-Fi ") + (i + 1) + "<input name=pass" + (i + 1) + " value=\"" + escapeHtml(settings.pass[i]) + "\"></label>";
    }
    h += "<label>Адрес Улья<input name=url placeholder='https://hive.имя.keenetic.pro' value=\"" + escapeHtml(settings.url) +
         "\"><small>Адрес, по которому Улей открывается из интернета.</small></label>";
    h += "<label>Токен экрана<input name=token autocapitalize=off placeholder='xxxx-xxxx-xxxx-xxxx-xxxx' value=\"" + escapeHtml(settings.token) +
         "\"><small>Выдаётся на странице монитора в Улье; там же QR-код, который заполняет эту форму.</small></label>";
    h += F("<label>Обновлять вкладки<select name=interval>");
    for (int m : {5, 10, 15, 30, 60, 120}) {
        h += String("<option value=") + m + (m == settings.intervalMin ? " selected>" : ">") + m + " мин</option>";
    }
    h += F("</select><small>Часы и измерения обновляются раз в минуту.</small></label>");
    h += String("<label><input type=checkbox name=insecure style='width:auto'") + (settings.insecure ? " checked" : "") +
         "> не проверять сертификат <small>(только для адреса внутри дома)</small></label>";
    h += F("<button>Сохранить</button></form></html>");
    return h;
}

void handleSave() {
    for (int i = 0; i < 2; i++) {
        const String n = String(i + 1);
        settings.ssid[i] = server.arg("ssid" + n);
        settings.ssid[i].trim();
        settings.pass[i] = server.arg("pass" + n);
    }
    settings.url = server.arg("url");
    settings.url.trim();
    settings.token = server.arg("token");
    settings.token.trim();
    settings.intervalMin = clampInterval(server.arg("interval").toInt());
    settings.insecure = server.hasArg("insecure");
    if (!settings.ready()) {
        server.send(200, "text/html; charset=utf-8", pageHtml("Нужны сеть Wi-Fi, адрес Улья (https://...) и токен."));
        return;
    }
    settings.save();
    forgetTabs();
    server.send(200, "text/html; charset=utf-8",
                "<!doctype html><meta charset=utf-8><p>Сохранено. Монитор перезапускается и читает картинку из Улья.</p>");
    restartAtMs = millis() + 1500;
}

void startPortal() {
    const uint64_t mac = ESP.getEfuseMac();
    char suffix[5];
    snprintf(suffix, sizeof suffix, "%04x", static_cast<unsigned>((mac >> 32) & 0xffff));
    apName = String("VillageSwarm-") + suffix;

    WiFi.mode(WIFI_AP_STA);
    const int found = WiFi.scanNetworks();
    for (int i = 0; i < found && i < 20; i++) networksHtml += "<option value=\"" + escapeHtml(WiFi.SSID(i)) + "\">";
    WiFi.scanDelete();
    WiFi.softAP(apName.c_str());
    dns.start(53, "*", WiFi.softAPIP());
    server.on("/", HTTP_GET, [] {
        const char* note = nullptr;
        if (server.hasArg("url") || server.hasArg("token") || server.hasArg("ssid")) {
            if (server.hasArg("ssid")) settings.ssid[0] = server.arg("ssid");
            if (server.hasArg("pass")) settings.pass[0] = server.arg("pass");
            if (server.hasArg("url")) settings.url = server.arg("url");
            if (server.hasArg("token")) settings.token = server.arg("token");
            if (server.hasArg("interval")) settings.intervalMin = clampInterval(server.arg("interval").toInt());
            note = "Поля заполнены из QR-кода. Проверьте Wi-Fi и нажмите «Сохранить».";
        }
        server.send(200, "text/html; charset=utf-8", pageHtml(note));
    });
    server.on("/save", HTTP_POST, handleSave);
    server.onNotFound([] {
        server.sendHeader("Location", "http://192.168.4.1/", true);
        server.send(302, "text/plain", "");
    });
    server.begin();
    portalStartedMs = millis();
    showMessage("Настройка", {"Подключитесь к Wi-Fi " + apName, "и откройте http://192.168.4.1", "", "10 минут на настройку"});
}

// ------------------------------------------------------------------ reading a tab

bool wifiUp = false;

/// Waits for the connection to come up (or the time to pass); true when it is up, and then remembers the access point. A network that
/// is plainly not there (no such name after a scan) is given up after 5 s: no use burning the battery for the rest of the wait.
bool connectedWithin(uint32_t limitMs, int ssidIndex) {
    const uint32_t started = millis();
    while (WiFi.status() != WL_CONNECTED && millis() - started < limitMs) {
        if (WiFi.status() == WL_NO_SSID_AVAIL && millis() - started > 5000) break;
        delay(50);
    }
    if (WiFi.status() != WL_CONNECTED) {
        Serial.printf("[monitor] wifi %s: status %d after %lu ms\n", settings.ssid[ssidIndex].c_str(), static_cast<int>(WiFi.status()),
                      static_cast<unsigned long>(millis() - started));
        WiFi.disconnect(true);
        return false;
    }
    memcpy(apBssid, WiFi.BSSID(), sizeof apBssid);
    apChannel = WiFi.channel();
    apSsid = ssidIndex;
    return wifiUp = true;
}

/// Connects to the first network that answers within <budgetMs> in all. The access point of the last connection is tried first by its
/// channel and address (no scan: a second or two instead of five), and forgotten when it does not answer. An empty second network is
/// skipped.
bool joinWifi(uint32_t budgetMs = 2 * kWifiTimeoutMs) {
    if (wifiUp && WiFi.status() == WL_CONNECTED) return true;
    WiFi.persistent(false);
    WiFi.mode(WIFI_STA);
    const uint32_t started = millis();
    auto left = [&](uint32_t want) -> uint32_t {
        const uint32_t used = millis() - started;
        return used >= budgetMs ? 0 : (budgetMs - used < want ? budgetMs - used : want);
    };
    if (apSsid >= 0 && apSsid < 2 && apChannel > 0 && settings.ssid[apSsid].length()) {
        WiFi.begin(settings.ssid[apSsid].c_str(), settings.pass[apSsid].c_str(), apChannel, apBssid);
        if (connectedWithin(left(4000), apSsid)) return true;
        apChannel = 0;  // it moved or is gone: scan next time
    }
    for (int i = 0; i < 2; i++) {
        if (settings.ssid[i].isEmpty()) continue;
        const uint32_t limit = left(kWifiTimeoutMs);
        if (limit < 1000) break;
        WiFi.begin(settings.ssid[i].c_str(), settings.pass[i].c_str());
        if (connectedWithin(limit, i)) return true;
    }
    return false;
}

/// The hive's unix time now, or 0 while the clock has not been set from a reply (the system clock runs through deep sleep).
uint32_t nowUnix() {
    const time_t t = time(nullptr);
    return t > 1700000000 ? static_cast<uint32_t>(t) : 0;
}

class FrameSink : public Stream {
public:
    explicit FrameSink(uint8_t* buffer) : buffer_(buffer) {}
    size_t write(uint8_t b) override { return write(&b, 1); }
    size_t write(const uint8_t* data, size_t size) override {
        if (size_ + size > kFrameBytes) {
            overflow_ = true;
            return 0;
        }
        memcpy(buffer_ + size_, data, size);
        size_ += size;
        return size;
    }
    int available() override { return 0; }
    int read() override { return -1; }
    int peek() override { return -1; }
    size_t size() const { return overflow_ ? kFrameBytes + 1 : size_; }

private:
    uint8_t* buffer_;
    size_t size_ = 0;
    bool overflow_ = false;
};

uint8_t incoming[kFrameBytes];

struct Reply {
    int code = -1;
    Fail fail = Fail::None;  // why it failed, when it did
    int tlsError = 0;
    uint32_t tookMs = 0;
    Answer answer = Answer::Retry;
    int width = 0, height = 0;  // the size the hive said
};

/// The panel's MAC (its Wi-Fi station address, from the chip: readable before Wi-Fi is up), as the hive's device page shows it.
String deviceMac() {
    const uint64_t efuse = ESP.getEfuseMac();
    return String(macText(reinterpret_cast<const uint8_t*>(&efuse)).c_str());
}

/// Who is asking, with every read (contracts/monitor-http.md): a panel that speaks only HTTP never tells the hive its MAC and build otherwise.
void addIdentity(HTTPClient& http) {
    http.addHeader("X-Device-Mac", deviceMac());
    if (HORNET_FW_VERSION[0]) http.addHeader("X-Fw-Version", HORNET_FW_VERSION);
}

/// A USB host on the other side of the console (a computer, not a charger): the console window waits for it.
bool usbHostAttached() {
#if ARDUINO_USB_CDC_ON_BOOT
    return Serial.isConnected();
#else
    return false;
#endif
}

/// Whether the hive's name resolves (an address needs no lookup): told apart from a TLS or a connect failure.
bool resolves(const std::string& url) {
    const std::string host = hostOf(url);
    if (host.empty()) return true;  // not an address: begin() refuses it below
    IPAddress ip;
    return WiFi.hostByName(host.c_str(), ip) == 1;
}

/// Reads one tab; a new picture lands in the flash (and its ETag beside it). <sleep>: the sleeping picture, which is only shown (it lands in <incoming>).
Reply fetchTab(int tab, int batteryMv, bool sleep = false) {
    Reply reply;
    const std::string url = tabUrl(settings.url.c_str(), tab, sleep);
    const uint32_t connectTimeoutMs = sleep ? 5000 : 8000;
    const uint32_t startedMs = millis();
    if (!resolves(url)) {
        reply.fail = Fail::Dns;
        reply.tookMs = millis() - startedMs;
        return reply;
    }
    WiFiClientSecure secure;
    WiFiClient plain;
    HTTPClient http;
    http.setTimeout(sleep ? 6000 : 10000);  // going to sleep: the screen must not sit on "засыпаю..." for long
    http.setConnectTimeout(connectTimeoutMs);
    http.setReuse(false);
    bool begun;
    if (isHttps(url)) {
        if (settings.insecure) secure.setInsecure();
        else secure.setCACert(kRootCa);
        begun = http.begin(secure, url.c_str());
    } else {
        begun = http.begin(plain, url.c_str());
    }
    if (!begun) {
        reply.fail = Fail::Other;
        return reply;
    }
    addIdentity(http);
    http.addHeader("Authorization", "Bearer " + settings.token);
    const String tag = readTag(tab);
    if (!sleep && tag.length() && haveTab(tab)) http.addHeader("If-None-Match", tag);
    if (batteryMv > 0) http.addHeader("X-Battery-mV", String(batteryMv));
    const char* keys[] = {"ETag", "X-Frame-Width", "X-Frame-Height", "X-Frame-Rotation", "Date", "X-Frame-Tabs", "X-Frame-Refresh", "X-Frame-Tab-Weights"};
    http.collectHeaders(keys, 8);
    reply.code = http.GET();
    if (isHttps(url)) {
        char why[64] = "";
        reply.tlsError = secure.lastError(why, sizeof why);  // 0 unless the handshake went wrong
    }
    size_t bytes = 0;
    if (reply.code == 200) {
        FrameSink sink(incoming);
        http.writeToStream(&sink);
        bytes = sink.size();
    }
    if (const uint32_t now = httpDate(http.header("Date").c_str())) {
        clockUnix = now - millis() / 1000;
        const timeval tv = {static_cast<time_t>(now), 0};
        settimeofday(&tv, nullptr);  // the system clock keeps counting through deep sleep: how old a tab is
    }
    if (http.hasHeader("X-Frame-Rotation")) rotation = http.header("X-Frame-Rotation").toInt() % 360;
    if (reply.code == 200 || reply.code == 304) {
        tabs = tabCount(http.header("X-Frame-Tabs").toInt(), tabs);
        refreshS = http.hasHeader("X-Frame-Refresh") ? static_cast<uint32_t>(http.header("X-Frame-Refresh").toInt()) : 0;
        if (currentTab >= tabs) currentTab = 0;
        weightCount = http.hasHeader("X-Frame-Tab-Weights") ? parseTabWeights(http.header("X-Frame-Tab-Weights").c_str(), tabWeights, kMaxTabs) : 0;
    }
    reply.width = http.header("X-Frame-Width").toInt();
    reply.height = http.header("X-Frame-Height").toInt();
    reply.answer = classify(reply.code, bytes, reply.width, reply.height, kWidth, kHeight);
    reply.tookMs = millis() - startedMs;
    if (reply.answer == Answer::Retry) reply.fail = classifyFailure(reply.code, true, reply.tlsError, reply.tookMs, connectTimeoutMs);
    if (reply.code == 200 && reply.answer != Answer::Draw)
        Serial.printf("[monitor] frame of %dx%d, %u bytes: expected %dx%d\n", reply.width, reply.height, static_cast<unsigned>(bytes), kWidth, kHeight);
    if (reply.answer == Answer::Draw && !sleep) {
        if (writeFile(tabPath(tab), incoming, kFrameBytes)) writeTag(tab, http.header("ETag"));
        else LittleFS.remove(tagPath(tab));
    }
    if (!sleep && (reply.answer == Answer::Draw || reply.answer == Answer::Keep)) tabReadAt[tab] = nowUnix();
    http.end();
    return reply;
}

/// The person moved the rocker while the panel was reading: the cursor goes on from here, this reading is of no use any more.
[[noreturn]] void chooseTab(int firstStep);

const char* const kCauseNames[] = {"none", "no wifi", "hive does not answer", "wrong frame size"};

/// No tab to put a banner on (a fresh panel, or the flash was lost): the whole screen says what is wrong and what to do.
void showOfflineMessage(Offline cause, const Reply& reply, uint32_t retryMin, int battery) {
    const String retry = "Пробую снова через " + String(retryMin) + " мин";
    switch (cause) {
        case Offline::NoWifi:
            showMessage("Нет Wi-Fi", {"Сеть " + settings.ssid[0] + (settings.ssid[1].length() ? " и " + settings.ssid[1] : String()) + " не отвечает.",
                                      "Проверьте роутер (Wi-Fi 2,4 ГГц).", retry, "EXIT 2 с, потом MENU 2 с: настройка", batteryLine(battery)});
            break;
        case Offline::WrongFrame:
            showMessage("Не тот кадр", {"Улей прислал " + String(reply.width) + "x" + String(reply.height) + ", нужно " + String(kWidth) + "x" + String(kHeight) + ":",
                                        "на странице монитора выберите", "плату CrowPanel 3,7\".", retry, batteryLine(battery)});
            break;
        default:
            showMessage("Нет связи с Ульем", {"Wi-Fi есть, Улей не отвечает (" + String(failureText(reply.fail, reply.code).c_str()) + ").",
                                              "Проверьте Улей и интернет у него.", retry, "EXIT 2 с, потом MENU 2 с: настройка", batteryLine(battery)});
            break;
    }
}

/// A read that worked: the next failure starts the backoff over.
void readWorked() {
    failures = 0;
    offlineMin = 0;
}

/// The read failed: the screen says so at once (on the old tab, or the whole screen when there is none), then only when the cause changes
/// or every half hour (e-ink flashes); the next try comes later each time (5, 10, 20, 30 min).
[[noreturn]] void offline(Offline cause, const Reply& reply, int battery) {
    failures++;
    const uint32_t retryMin = retryMinutes(failures, settings.intervalMin);
    const Age age = dataAge(nowUnix(), tabReadAt[currentTab], offlineMin);
    const bool have = haveTab(currentTab);
    const bool due = bannerDue(cause, bannerCause, age.minutes, bannerAgeMin) || loadingShown || (have && messageShown);
    const Fail fail = cause == Offline::NoWifi ? Fail::NoWifi : cause == Offline::WrongFrame ? Fail::Frame : reply.fail;
    const std::string reason = failureText(fail, reply.code);
    lastFail = fail;
    lastFailCode = reply.code;
    Serial.printf("[monitor] why: %s, http %d, tls %d, took %lu ms, host %s\n", reason.c_str(), reply.code, reply.tlsError,
                  static_cast<unsigned long>(reply.tookMs), hostOf(settings.url.c_str()).c_str());
    Serial.printf("[monitor] offline: %s (http %d), failure %lu, data %lu min old%s, next try in %lu min, %s\n", kCauseNames[static_cast<int>(cause)],
                  reply.code, static_cast<unsigned long>(failures), static_cast<unsigned long>(age.minutes), age.exact ? "" : " (counted)",
                  static_cast<unsigned long>(retryMin), due ? "screen redrawn" : "screen kept");
    if (due) {
        if (!have || !showTab(currentTab, -1, String(offlineBanner(cause, age, false, reason).c_str()))) showOfflineMessage(cause, reply, retryMin, battery);
        bannerCause = cause;
        bannerAgeMin = age.minutes;
    }
    offlineMin += retryMin;
    sleepFor(retryMin * 60);
}

/// Reads the shown tab and puts it on the screen when it changed; then sleeps until its next read. <loading>: the person is waiting
/// (a button was pressed): the screen says so at once.
[[noreturn]] void readAndSleep(bool loading) {
    const int battery = readBatteryMv();
    if (loading && !loadingShown) showTab(currentTab, -1, String(kLoadingText));  // no tab in the flash yet: nothing to mark
    const uint32_t startedMs = millis();
    if (!joinWifi()) {
        if (movedMeanwhile()) chooseTab(0);
        Serial.println("[monitor] wifi failed");
        offline(Offline::NoWifi, Reply(), battery);
    }
    const uint32_t wifiMs = millis() - startedMs;
    if (movedMeanwhile()) chooseTab(0);  // the connection is up for the tab the person chooses next

    const Reply reply = fetchTab(currentTab, battery);
    const uint32_t gotMs = millis() - startedMs - wifiMs;
    Serial.printf("[monitor] frame %d\n", reply.code);  // the hive's page waits for this line after flashing
    // chosen another tab meanwhile: this one is in the flash now, but it is not worth a screen refresh
    if ((reply.answer == Answer::Draw || reply.answer == Answer::Keep) && movedMeanwhile()) {
        readWorked();
        chooseTab(0);
    }
    switch (reply.answer) {
        case Answer::Draw:
            readWorked();
            memcpy(picture, incoming, kFrameBytes);  // not from the flash: it may have failed to keep it
            showPicture(-1, String());
            break;
        case Answer::Keep:
            readWorked();
            if (bannerCause != Offline::None || messageShown || loadingShown) showTab(currentTab, -1, String());  // take the banner off
            break;
        case Answer::BadToken:
            showMessage("Токен не принят", {"Выдайте новый на странице", "монитора в Улье и введите его:", "EXIT 2 с, потом MENU 2 с: настройка", batteryLine(battery)});
            sleepFor(kBadTokenSleepMin * 60);
        case Answer::NotDrawn:
            showMessage("Нет картинки", {"Улей не рисует этот экран:", "на странице монитора выберите", "плату CrowPanel 3,7\".", batteryLine(battery)});
            sleepFor(kBadTokenSleepMin * 60);
        case Answer::Retry:
            offline(reply.code == 200 ? Offline::WrongFrame : Offline::NoHive, reply, battery);
    }
    Serial.printf("[monitor] timing: wifi %lu ms, hive %lu ms, screen %lu ms\n", static_cast<unsigned long>(wifiMs), static_cast<unsigned long>(gotMs),
                  static_cast<unsigned long>(millis() - startedMs - wifiMs - gotMs));
    sleepFor(secondsToDue(nextReadSeconds(), nowUnix(), tabReadAt[currentTab]));
}

// ------------------------------------------------------------------ the rocker

/// The tab the person chose: from the flash at once; read again unless it was read lately (switching between tabs needs no trip to the
/// hive each time). The same tab chosen again is a request to refresh it.
[[noreturn]] void chosen(int tab) {
    const bool same = tab == currentTab;
    currentTab = tab;
    const bool have = haveTab(tab);
    if (!same && have && isFresh(nowUnix(), tabReadAt[tab], freshSeconds(refreshS))) {
        showTab(tab, -1, String());
        sleepFor(secondsToDue(nextReadSeconds(), nowUnix(), tabReadAt[tab]));
    }
    if (have) showTab(tab, -1, String(kLoadingText));
    else showMessage("Загрузка", {String("Вкладка ") + (tab + 1)});
    readAndSleep();
}

/// The cursor over the tab bar: up/down move it, the press chooses, EXIT or a few quiet seconds leave it. Presses that came while the
/// panel was busy (even during the screen refresh) are counted by interrupts and taken here, so none is lost.
[[noreturn]] void chooseTab(int firstStep) {
    int cursor = stepTab(currentTab, firstStep, tabs);
    cursor = stepTab(cursor, takeSteps(), tabs);
    if (takePress()) chosen(cursor);
    showTab(currentTab, cursor, String());
    uint32_t lastInput = millis();
    while (millis() - lastInput < kCursorTimeoutMs) {
        if (const int step = takeSteps(); step != 0) {
            cursor = stepTab(cursor, step, tabs);
            showTab(currentTab, cursor, String());
            lastInput = millis();
            continue;
        }
        if (takePress()) chosen(cursor);
        if (pressed(kPinExit)) {
            waitRelease();
            break;
        }
        delay(20);
    }
    showTab(currentTab, -1, String());
    sleepFor(nextReadSeconds());
}

/// Woken by a button: held for 2 s = the long press (MENU: the settings point, EXIT: the help screen).
bool held(gpio_num_t pin) {
    const uint32_t started = millis();
    while (millis() - started < 2000) {
        if (!pressed(pin)) return false;
        delay(20);
    }
    return true;
}

bool menuHeld() { return held(kPinMenu); }

/// Whether a sleeping picture from the hive is the shown tab marked "спит" (the person has loaded no sleep picture) rather than the
/// person's own: a tab always ends in the tab bar with the shown tab's cell black, the person's picture sits in the middle of white.
/// Only the person's picture is kept for offline: an old tab would show old numbers.
bool isSleepingTab(const uint8_t* bits) {
    memcpy(canvas.getBuffer(), bits, kFrameBytes);
    canvas.setRotation((rotation / 90) & 3);
    const Cell cell = tabCell(currentTab, tabs, canvas.width(), canvas.height(), weightCount == tabs ? tabWeights : nullptr);
    int black = 0, all = 0;
    for (int y = cell.y + 2; y < cell.y + cell.h - 1; y += 2)
        for (int x = cell.x + 2; x < cell.x + cell.w - 2; x += 2) {
            all++;
            black += canvas.getPixel(x, y) ? 1 : 0;
        }
    return all > 0 && black * 10 >= all * 6;  // the cell's name is white on it
}

/// A short EXIT: the "засыпаю..." mark at once, then the sleeping picture, then deep sleep until any button. Wi-Fi gets one short try
/// (the screen must not sit on "засыпаю..."). The picture: online, the hive's (the person's own, then kept in the flash, or the shown
/// tab saying "спит"); offline, the person's picture kept from the last time; without one, a white screen that says there is no link
/// and that any button wakes the panel.
[[noreturn]] void sleepUntilButton() {
    Serial.println("[monitor] sleep: EXIT");
    if (!showTab(currentTab, -1, String("засыпаю..."))) showMessage("Засыпаю...", {});  // the screen answers at once
    const int battery = readBatteryMv();
    Offline cause = Offline::NoWifi;
    bool drawn = false;
    if (joinWifi(kSleepWifiMs)) {
        const Reply reply = fetchTab(currentTab, battery, true);
        Serial.printf("[monitor] sleep frame %d (%s)\n", reply.code, failureText(reply.fail, reply.code).c_str());
        if (reply.answer == Answer::Draw) {
            if (isSleepingTab(incoming)) {
                Serial.println("[monitor] sleep: the hive's sleeping tab (no picture of the person's)");
                LittleFS.remove(kSleepPath);
            } else if (!readFile(kSleepPath, picture, kFrameBytes) || memcmp(picture, incoming, kFrameBytes) != 0) {
                if (writeFile(kSleepPath, incoming, kFrameBytes)) Serial.println("[monitor] sleep: the picture is kept for offline");
            }
            memcpy(picture, incoming, kFrameBytes);
            present();
            keepShown();
            drawn = true;
        } else if (reply.answer == Answer::NotDrawn || reply.answer == Answer::BadToken) {
            cause = Offline::None;  // the hive answered: the link is there
            if (reply.answer == Answer::NotDrawn) LittleFS.remove(kSleepPath);
        } else {
            cause = reply.code == 200 ? Offline::WrongFrame : Offline::NoHive;
        }
    }
    if (!drawn && cause != Offline::None && readFile(kSleepPath, picture, kFrameBytes)) {
        Serial.println("[monitor] sleep: offline, the kept sleep picture");
        present();
        keepShown();
        drawn = true;
    }
    if (!drawn && cause != Offline::None) {
        Serial.printf("[monitor] sleep: offline (%s), no kept picture\n", kCauseNames[static_cast<int>(cause)]);
        showMessage("Нет связи", {cause == Offline::NoWifi ? "Нет Wi-Fi: " + settings.ssid[0] + " не отвечает." : String("Wi-Fi есть, Улей не отвечает."),
                                  "", "Панель засыпает.", "Любая кнопка будит её.", "", batteryLine(battery)});
        drawn = true;
    }
    if (!drawn) {
        Serial.println("[monitor] sleep: the hive has no sleep picture");
        if (!showTab(currentTab, -1, "спит")) showMessage("Спит", {"Любая кнопка будит панель", batteryLine(battery)});
    }
    partials = kPartialsBeforeFull;  // whatever comes next is a different picture: wipe the screen
    messageShown = true;             // the tab is not on the screen: the next read draws it even on 304
    loadingShown = false;
    bannerCause = Offline::None;
    asleep = true;
    waitRelease();  // a button still down would wake the panel at once
    Serial.println("[monitor] asleep until a button");
    sleepFor(0);
}

/// EXIT held for 2 s: what the buttons do. EXIT again gives the screen back; a few quiet seconds do the same. True: MENU held, the settings point is up.
bool showHelp() {
    showMessage("Кнопки", {"Вверх/вниз: вкладки, нажать: выбрать", "MENU: обновить, MENU 2 с: читалка", "EXIT: сон до кнопки",
                           "EXIT 2 с: эта справка", "здесь MENU 2 с: настройки", "  http://192.168.4.1", "здесь EXIT: назад"});
    waitRelease();
    const uint32_t started = millis();
    while (millis() - started < kHelpTimeoutMs) {
        if (pressed(kPinExit)) {
            waitRelease();
            break;
        }
        if (pressed(kPinMenu)) {
            if (menuHeld()) return true;
            readAndSleep(true);
        }
        if (const int step = takeSteps(); step != 0) chooseTab(step);
        if (takePress()) chooseTab(0);
        delay(20);
    }
    if (!showTab(currentTab, -1, String())) readAndSleep();
    sleepFor(secondsToDue(nextReadSeconds(), nowUnix(), tabReadAt[currentTab]));
}

[[noreturn]] void saveAndSleep() {
    showMessage("Настроено", {"Wi-Fi: " + settings.ssid[0], "Первое чтение через минуту у этой сети", "EXIT 2 с, потом MENU 2 с: настройка"});
    sleepFor(60);
}

Order consoleWindow() {
    const uint32_t started = millis();
    ConsoleWindow window;
    uint32_t lastReady = 0;
    for (;;) {
        bool activity = false;
        const Order order = pollConsole(activity);
        if (order != Order::None) return order;
        const uint32_t now = millis() - started;
        if (activity) window.touch(now);  // a line typed keeps the window open for a minute; so does a USB host that is attached
        if (!window.open(now, usbHostAttached())) return Order::None;
        if (ConsoleWindow::readyDue(now, lastReady)) {
            lastReady = now == 0 ? 1 : now;
            Serial.println("[monitor] ready");
        }
        delay(20);
    }
}

}  // namespace

void setup() {
    powerOn();
    Serial.begin(115200);
    settings.load();
    if (!LittleFS.begin(true)) Serial.println("[monitor] flash: no file system, tabs are read anew every time");
    haveShown = readFile(kShownPath, shown, kFrameBytes);

    const esp_sleep_wakeup_cause_t cause = esp_sleep_get_wakeup_cause();
    const Order order = cause == ESP_SLEEP_WAKEUP_UNDEFINED ? consoleWindow() : Order::None;
    if (order == Order::Save) saveAndSleep();
    if (cause == ESP_SLEEP_WAKEUP_UNDEFINED) partials = kPartialsBeforeFull;  // after power-on the screen may hold anything

    const uint64_t buttons = cause == ESP_SLEEP_WAKEUP_EXT1 ? esp_sleep_get_ext1_wakeup_status() : 0;
    Serial.printf("[monitor] wake: cause %d, buttons 0x%llx, %s, offline tries %lu\n", static_cast<int>(cause), static_cast<unsigned long long>(buttons),
                  asleep ? "was asleep" : "awake", static_cast<unsigned long>(failures));
    if (crowpanel37::readerOn()) crowpanel37::runReader(buttons, false);  // it was reading: it goes on, no Wi-Fi
    if ((buttons & (1ULL << kPinMenu)) && menuHeld()) {
        asleep = false;
        crowpanel37::runReader(0, true);  // MENU 2 s: the reader
    }
    if (!settings.ready()) {
        asleep = false;
        portalMode = true;
        startPortal();
        return;
    }
    watchButtons();  // from here on a press that comes while the panel is busy is counted, not lost
    if (asleep && buttons) {
        // the panel was put to sleep: any button only wakes it, with the tab it had
        asleep = false;
        loadingShown = false;
        readAndSleep(true);
    }
    if (buttons & (1ULL << kPinExit)) {
        if (!held(kPinExit)) sleepUntilButton();  // a short press puts the panel to sleep, held 2 s shows the help
        if (showHelp()) {
            portalMode = true;
            startPortal();
            return;
        }
    }
    if (buttons & (1ULL << kPinDown)) chooseTab(1);
    if (buttons & (1ULL << kPinUp)) chooseTab(-1);
    if (buttons & (1ULL << kPinConf)) chooseTab(0);
    readAndSleep((buttons & (1ULL << kPinMenu)) != 0);  // the timer, a short MENU, power-on
}

void loop() {
    if (!portalMode) return;
    dns.processNextRequest();
    server.handleClient();
    bool activity = false;
    if (const Order order = pollConsole(activity); order != Order::None) {
        server.stop();
        dns.stop();
        WiFi.softAPdisconnect(true);
        portalMode = false;
        if (order == Order::Save) saveAndSleep();
        readAndSleep();
    }
    if (restartAtMs && static_cast<int32_t>(millis() - restartAtMs) >= 0) ESP.restart();
    if (millis() - portalStartedMs > kPortalTimeoutMs) {
        server.stop();
        dns.stop();
        WiFi.softAPdisconnect(true);
        portalMode = false;
        if (settings.ready()) readAndSleep();  // the old settings stay: read with them (says so if there is no link)
        // nothing set: the "Настройка" screen must not stay as if the point were still up
        showMessage("Не настроено", {"Панель не знает Wi-Fi и Улей.", "Настройка с телефона: EXIT 2 с,", "потом MENU 2 с; или прошейте", "со страницы монитора.", "MENU 2 с: читалка книг с карты", "Панель спит до кнопки."});
        sleepFor(0);
    }
    delay(2);
}

// ------------------------------------------------------------------ for the reader (crowpanel37.h)

namespace crowpanel37 {

GFXcanvas1& canvas() { return ::canvas; }
U8G2_FOR_ADAFRUIT_GFX& text() { return ::text; }

void present(bool keepInFlash) {
    memcpy(picture, ::canvas.getBuffer(), kFrameBytes);
    ::present();
    if (keepInFlash) keepShown();
    messageShown = true;  // the hive's tab is not on the screen: the next read draws it even on 304
    loadingShown = false;
    bannerCause = Offline::None;
}

void wipeNext() { partials = kPartialsBeforeFull; }
int batteryMv() { return readBatteryMv(); }
int batteryPercent(int mv) { return hornet::remote::batteryPercent(mv); }
bool pressed(gpio_num_t pin) { return ::pressed(pin); }
bool held(gpio_num_t pin) { return ::held(pin); }
void waitRelease() { ::waitRelease(); }
void watchButtons() { ::watchButtons(); }
int takeSteps() { return ::takeSteps(); }
bool takePress() { return ::takePress(); }

[[noreturn]] void sleepUntilButton() {
    keepShown();
    sleepFor(0);
}

[[noreturn]] void backToMonitor() {
    wipeNext();
    if (settings.ready()) readAndSleep(true);
    showMessage("Не настроено", {"Панель не знает Wi-Fi и Улей.", "Настройка с телефона: EXIT 2 с,", "потом MENU 2 с.", "MENU 2 с: читалка книг с карты", "",
                                 "Панель спит до кнопки."});
    sleepFor(0);
}

}  // namespace crowpanel37
