// Remote monitor on the Waveshare ESP32-S3 1.54" e-paper (200x200, black and white): a panel far from the hive (the city
// flat, say) that shows the picture the hive draws for it (contracts/monitor-http.md, frame.bin). No MQTT, no enrollment:
// the monitor is added by hand on the hive's page ("Добавить удалённый монитор") and given a read-only token there.
//
// It sleeps almost all the time: wake by the timer, join Wi-Fi, ask the hive for frame.bin with the last ETag, draw a new
// picture only when it changed (304 = keep the old one), back to deep sleep. The e-paper keeps the picture without power.
// When a read fails, the last picture gets a banner at once ("нет Wi-Fi, 40 мин": the cause and how old the picture is), so an old
// number is never taken for a fresh one; it is redrawn only when the cause changes or every half hour, and the next try comes later
// each time (5, 10, 20, then 30 min). With no picture at all the whole screen says what is wrong (docs/display.md, "Нет связи").
//
// Settings (two Wi-Fi networks, the hive's address, the token, how often to read) come in two ways:
//  - over USB, from the hive's page right after flashing: the console commands below. Nothing is typed by hand;
//  - on the panel's own Wi-Fi point: hold BOOT for 2 s (or start it with nothing set), join "VillageSwarm-xxxx", open
//    192.168.4.1. The hive's page shows a QR code with the address and the token: scan it with the phone's camera once
//    joined and the form opens filled in.
// A short press of BOOT reads the picture now; PWR turns the panel off (on battery) and shows the sleep picture: the hive's,
// kept in the flash for the times there is no link; with none, a white screen saying so.
//
// Console (115200, lines): set ssid|pass|ssid2|pass2|url|token|interval|rot|insecure <value>, show, go, save. "go" saves
// and reads the picture at once, answering "[monitor] frame <http code>" (200 new, 304 same) or "[monitor] wifi failed".
// "save" only saves (the panel is to stand at another Wi-Fi) and sleeps until its first read.
// After a power-on or a reset the panel waits 12 s for the console before it reads, repeating "[monitor] ready"; a line sent (or a USB host
// that is attached) keeps the window open (ConsoleWindow in RemoteMonitor.h). `show` also prints why the last read failed (DNS, TLS, refused, timeout, HTTP code).
//
// Battery: every read tells the cell (X-Battery-mV, GPIO4 through a 1:2 divider) and the hive draws it in the top right corner.
// Wired mode: on external power the panel does not deep-sleep but waits out its interval awake (USB stays up, buttons answer at once):
// "set wired auto|off|on" (auto: a USB host, or the cell's voltage rising or held high; see wiredNow in RemoteMonitor.h and docs/display.md).
//
// Pins from the Waveshare examples for the V2 board (github.com/waveshareteam/ESP32-S3-ePaper-1.54).

#include <Arduino.h>
#include <DNSServer.h>
#include <GxEPD2_BW.h>
#include <HTTPClient.h>
#include <LittleFS.h>
#include <Preferences.h>
#include <SPI.h>
#include <U8g2_for_Adafruit_GFX.h>
#include <WebServer.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <driver/gpio.h>
#include <driver/rtc_io.h>
#include <esp_sleep.h>
#include <sys/time.h>
#include <time.h>

#include "../../lib/hornet-core/src/RemoteMonitor.h"

namespace {

using namespace hornet::remote;

constexpr int kWidth = 200;
constexpr int kHeight = 200;
constexpr size_t kFrameBytes = frameBytes(kWidth, kHeight);

constexpr int kPinDc = 10, kPinCs = 11, kPinSck = 12, kPinMosi = 13, kPinRst = 9, kPinBusy = 8;
constexpr gpio_num_t kPinEpdPower = GPIO_NUM_6;   // LOW = e-paper powered
constexpr gpio_num_t kPinPowerHold = GPIO_NUM_17; // HIGH keeps the battery switched on
constexpr gpio_num_t kPinAudioOff = GPIO_NUM_42;  // HIGH = audio amplifier off
constexpr gpio_num_t kPinBoot = GPIO_NUM_0;
constexpr gpio_num_t kPinPwr = GPIO_NUM_18;
constexpr int kPinBattery = 4;  // ADC through a 1:2 divider

constexpr uint32_t kWifiTimeoutMs = 15000;
constexpr uint32_t kSleepWifiMs = 7000;  // switching off: one short try for Wi-Fi
constexpr uint32_t kPortalTimeoutMs = 10UL * 60 * 1000;
constexpr uint32_t kBadTokenSleepMin = 6 * 60;  // a new token needs the settings point anyway: just look now and then
constexpr int kLowBatteryMv = 3450;

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

GxEPD2_BW<GxEPD2_154_D67, GxEPD2_154_D67::HEIGHT> display(GxEPD2_154_D67(kPinCs, kPinDc, kPinRst, kPinBusy));
U8G2_FOR_ADAFRUIT_GFX text;
SPIClass epdSpi(FSPI);

// Kept across deep sleep (lost when the power goes): the picture on the screen and how it came to be there.
RTC_DATA_ATTR uint8_t lastFrame[kFrameBytes];
RTC_DATA_ATTR bool haveFrame = false;
RTC_DATA_ATTR char lastEtag[72] = "";
RTC_DATA_ATTR uint32_t offlineMin = 0;  // minutes slept since the last read that worked: the picture's age while the clock is not known
RTC_DATA_ATTR uint32_t failures = 0;    // failed reads in a row: the next try is later each time (retryMinutes)
RTC_DATA_ATTR uint32_t lastOkUnix = 0;  // the hive's time of the last read that worked (0: not known)
RTC_DATA_ATTR Fail lastFail = Fail::None;  // why the last read failed (RemoteMonitor.h): the banner says it, "show" too
RTC_DATA_ATTR int lastFailCode = 0;        // its HTTP code or HTTPClient error
RTC_DATA_ATTR Offline bannerCause = Offline::None;  // what the screen says about the link now (None: nothing)
RTC_DATA_ATTR uint32_t bannerAgeMin = 0;            // the picture's age it says
RTC_DATA_ATTR uint8_t apBssid[6] = {};  // the access point of the last connection: the next one skips the scan
RTC_DATA_ATTR int apChannel = 0;
RTC_DATA_ATTR int apSsid = -1;
RTC_DATA_ATTR bool switchedOff = false;  // PWR put it to sleep on USB power: the next PWR switches it on
RTC_DATA_ATTR bool messageShown = false; // a message replaced the picture: draw the picture again even on 304
RTC_DATA_ATTR int wiredLastMv = 0;       // the cell at the previous read: a rise says the charger works (wiredNow)
RTC_DATA_ATTR bool wiredRestart = false; // the interval of wired mode ended (or a button was pressed): this start is not a power-on
RTC_DATA_ATTR uint64_t wiredButtons = 0; // the button that ended it, as the wake-up status would say
RTC_DATA_ATTR uint32_t refreshS = 0;     // the hive's monitoring: read this often (X-Frame-Refresh), 0 = own interval

struct Settings {
    String ssid[2], pass[2], url, token;
    int intervalMin = kDefaultIntervalMin;
    int rotation = 0;
    bool insecure = false;
    WiredMode wired = WiredMode::Auto;

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
        rotation = p.getInt("rot", 0) & 3;
        insecure = p.getBool("insecure", false);
        wired = static_cast<WiredMode>(p.getUChar("wired", static_cast<uint8_t>(WiredMode::Auto)) % 3);
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
        p.putInt("rot", rotation);
        p.putBool("insecure", insecure);
        p.putUChar("wired", static_cast<uint8_t>(wired));
        p.end();
    }
} settings;

[[noreturn]] void readAndSleep();

// ------------------------------------------------------------------ console over USB

#ifndef HORNET_FW_VERSION
#define HORNET_FW_VERSION ""
#endif

String deviceMac();  // below, with the headers of a read
bool usbHostAttached();
int batteryReading();

String consoleLine;

enum class Order { None, Go, Save };

/// Applies one console line; Go = save and read now, Save = save and sleep.
Order runCommand(String line) {
    line.trim();
    if (line.isEmpty()) return Order::None;
    if (line == "go" || line == "save") {
        if (!settings.ready()) {
            Serial.println("[monitor] error: need ssid, url (https://...) and token first");
            return Order::None;
        }
        settings.save();
        lastEtag[0] = '\0';
        Serial.println("[monitor] saved");
        return line == "go" ? Order::Go : Order::Save;
    }
    if (line == "show") {
        Serial.printf("[monitor] ssid=%s ssid2=%s url=%s token=%s interval=%d rot=%d insecure=%d\n", settings.ssid[0].c_str(),
                      settings.ssid[1].c_str(), settings.url.c_str(), settings.token.isEmpty() ? "-" : "set", settings.intervalMin,
                      settings.rotation, settings.insecure);
        Serial.printf("[monitor] wired=%s battery=%d mV usb=%d\n", wiredModeName(settings.wired), batteryReading(), usbHostAttached() ? 1 : 0);
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
    String value = space < 0 ? "" : line.substring(space + 1);  // the value may hold spaces (Wi-Fi names)
    if (key == "ssid") settings.ssid[0] = value;
    else if (key == "pass") settings.pass[0] = value;
    else if (key == "ssid2") settings.ssid[1] = value;
    else if (key == "pass2") settings.pass[1] = value;
    else if (key == "url") settings.url = value;
    else if (key == "token") settings.token = value;
    else if (key == "interval") settings.intervalMin = clampInterval(value.toInt());
    else if (key == "rot") settings.rotation = value.toInt() & 3;
    else if (key == "insecure") settings.insecure = value == "1";
    else if (key == "wired") {
        value.trim();
        const WiredMode mode = parseWiredMode(value.c_str(), settings.wired);
        if (std::string(wiredModeName(mode)) != value.c_str()) {
            Serial.println("[monitor] error: wired is auto, off or on");
            return Order::None;
        }
        settings.wired = mode;
    }
    else {
        Serial.printf("[monitor] error: unknown key %s\n", key.c_str());
        return Order::None;
    }
    Serial.println("[monitor] ok");
    return Order::None;
}

/// Reads what came over USB; Go or Save when that order arrived. Any input says somebody is at the console.
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

// ------------------------------------------------------------------ power

void powerOn() {
    gpio_hold_dis(kPinPowerHold);
    gpio_hold_dis(kPinEpdPower);
    gpio_hold_dis(kPinAudioOff);
    pinMode(kPinPowerHold, OUTPUT);
    digitalWrite(kPinPowerHold, HIGH);
    pinMode(kPinAudioOff, OUTPUT);
    digitalWrite(kPinAudioOff, HIGH);
    pinMode(kPinEpdPower, OUTPUT);
    digitalWrite(kPinEpdPower, LOW);
}

/// The cell in millivolts through the 1:2 divider, or -1 when it makes no sense (no divider): what a read tells the hive (X-Battery-mV).
int batteryReading() { return hornet::remote::batteryMv(static_cast<int>(analogReadMilliVolts(kPinBattery))); }

int batteryMv() {
    return static_cast<int>(analogReadMilliVolts(kPinBattery)) * 2;
}

/// The battery for a message: "Батарея 80%", or nothing when the reading makes no sense.
String batteryLine() {
    const int percent = batteryPercent(hornet::remote::batteryMv(static_cast<int>(analogReadMilliVolts(kPinBattery))));
    return percent < 0 ? String() : "Батарея " + String(percent) + "%";
}


/// On external power: waits out the interval awake (Wi-Fi off, the CPU slowed, no deep sleep: USB stays up and the buttons answer at once),
/// then starts again through a restart that remembers it is not a power-on (no console window) and which button, if any, ended it.
/// A console line that sends the panel to read ("go") ends it too. Never delays longer than 20 ms.
[[noreturn]] void wiredWait(uint32_t minutes) {
    Serial.printf("[monitor] wired: staying awake, next read in %lu min\n", static_cast<unsigned long>(minutes));
    setCpuFrequencyMhz(80);
    pinMode(kPinBoot, INPUT_PULLUP);
    pinMode(kPinPwr, INPUT_PULLUP);
    const uint32_t started = millis();
    const uint32_t waitMs = minutes * 60UL * 1000UL;
    uint64_t pressed = 0;
    while (pressed == 0 && millis() - started < waitMs) {
        if (digitalRead(kPinBoot) == LOW) pressed |= 1ULL << kPinBoot;
        if (digitalRead(kPinPwr) == LOW) pressed |= 1ULL << kPinPwr;
        bool activity = false;
        if (pollConsole(activity) != Order::None) break;  // go or save over USB: read now
        delay(20);
    }
    wiredRestart = true;
    wiredButtons = pressed;
    Serial.flush();
    ESP.restart();
    for (;;) delay(1000);
}

/// Deep sleep until the timer (minutes, 0 = none) or a button. The e-paper keeps its picture without power.
/// On external power (wiredNow) a timed sleep is spent awake instead (wiredWait).
[[noreturn]] void sleepFor(uint32_t minutes) {
    display.hibernate();
    WiFi.disconnect(true);
    WiFi.mode(WIFI_OFF);
    if (minutes > 0) {
        const int mv = batteryReading();  // Wi-Fi is off: no sag from its bursts
        const bool wired = wiredNow(settings.wired, mv, wiredLastMv, usbHostAttached());
        if (mv > 0) wiredLastMv = mv;
        if (wired) wiredWait(minutes);
    }
    digitalWrite(kPinEpdPower, HIGH);
    gpio_hold_en(kPinEpdPower);
    gpio_hold_en(kPinPowerHold);
    gpio_hold_en(kPinAudioOff);
    gpio_deep_sleep_hold_en();
    rtc_gpio_pullup_en(kPinBoot);
    rtc_gpio_pullup_en(kPinPwr);
    esp_sleep_enable_ext1_wakeup((1ULL << kPinBoot) | (1ULL << kPinPwr), ESP_EXT1_WAKEUP_ANY_LOW);
    if (minutes > 0) esp_sleep_enable_timer_wakeup(static_cast<uint64_t>(minutes) * 60ULL * 1000000ULL);
    Serial.flush();
    esp_deep_sleep_start();
}

// ------------------------------------------------------------------ screen

void initScreen() {
    epdSpi.begin(kPinSck, -1, kPinMosi, kPinCs);
    display.epd2.selectSPI(epdSpi, SPISettings(4000000, MSBFIRST, SPI_MODE0));
    // The panel lost power while asleep: every picture is a full refresh (a partial one needs the old image in its RAM).
    display.init(0, true, 2, false);
    display.setRotation(settings.rotation);
    display.setTextColor(GxEPD_BLACK);
    text.begin(display);
    text.setFontMode(1);
    text.setForegroundColor(GxEPD_BLACK);
    text.setBackgroundColor(GxEPD_WHITE);
}

void drawBanner(const String& line) {
    display.fillRect(0, kHeight - 22, kWidth, 22, GxEPD_BLACK);
    text.setFont(u8g2_font_7x13_t_cyrillic);  // 28 characters fit: "Улей не отвечает, ~3 ч"
    text.setForegroundColor(GxEPD_WHITE);
    text.setBackgroundColor(GxEPD_BLACK);
    text.setCursor(4, kHeight - 6);
    text.print(line);
    text.setForegroundColor(GxEPD_BLACK);
    text.setBackgroundColor(GxEPD_WHITE);
}

void showBits(const uint8_t* bits, const String& banner) {
    display.setFullWindow();
    display.firstPage();
    do {
        display.fillScreen(GxEPD_WHITE);
        display.drawBitmap(0, 0, bits, kWidth, kHeight, GxEPD_BLACK);
        if (banner.length()) drawBanner(banner);
    } while (display.nextPage());
    bannerCause = Offline::None;
}

void showFrame(const String& banner) {
    showBits(lastFrame, banner);
    messageShown = false;
}

/// A full-screen message: a title and up to six short lines (about 21 characters fit a line).
void showMessage(const char* title, std::initializer_list<String> lines) {
    display.setFullWindow();
    display.firstPage();
    do {
        display.fillScreen(GxEPD_WHITE);
        text.setFont(u8g2_font_10x20_t_cyrillic);
        text.setCursor(6, 26);
        text.print(title);
        display.drawFastHLine(6, 34, kWidth - 12, GxEPD_BLACK);
        text.setFont(u8g2_font_6x13_t_cyrillic);
        int y = 56;
        for (const String& line : lines) {
            text.setCursor(6, y);
            text.print(line);
            y += 18;
        }
    } while (display.nextPage());
    messageShown = true;
    bannerCause = Offline::None;
}

String batteryNote() {
    const int mv = batteryMv();
    return mv > 1000 && mv < kLowBatteryMv ? String("батарея ") + String(mv / 1000.0f, 2) + " В" : String();
}

// ------------------------------------------------------------------ settings point (captive portal)

WebServer server(80);
DNSServer dns;
String apName;
String networksHtml;
uint32_t portalStartedMs = 0;
uint32_t restartAtMs = 0;

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
    h += F("<label>Как часто обновлять<select name=interval>");
    for (int m : {5, 10, 15, 30, 60, 120}) {
        h += String("<option value=") + m + (m == settings.intervalMin ? " selected>" : ">") + m + " мин</option>";
    }
    h += F("</select><small>Чаще — свежее, реже — дольше живёт батарея.</small></label><label>Поворот экрана<select name=rot>");
    for (int r = 0; r < 4; r++) {
        h += String("<option value=") + r + (r == settings.rotation ? " selected>" : ">") + (r * 90) + "°</option>";
    }
    h += String("</select></label><label><input type=checkbox name=insecure style='width:auto'") + (settings.insecure ? " checked" : "") +
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
    settings.rotation = server.arg("rot").toInt() & 3;
    settings.insecure = server.hasArg("insecure");
    if (!settings.ready()) {
        server.send(200, "text/html; charset=utf-8",
                    pageHtml("Нужны сеть Wi-Fi, адрес Улья (https://...) и токен."));
        return;
    }
    settings.save();
    lastEtag[0] = '\0';  // another hive or token: read the picture anew
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
        // The hive's page shows a QR code with these in the address: the phone's camera opens the form filled in.
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
    server.onNotFound([] {  // phones probe some page to find a captive portal: send them to the form
        server.sendHeader("Location", "http://192.168.4.1/", true);
        server.send(302, "text/plain", "");
    });
    server.begin();
    portalStartedMs = millis();
    showMessage("Настройка", {"Подключитесь к Wi-Fi", apName, "и откройте", "http://192.168.4.1", "", "10 минут на настройку"});
}

bool portalMode = false;

// ------------------------------------------------------------------ reading the picture

/// Waits for the connection to come up (or the time to pass); true when it is up, and then remembers the access point. A network that
/// is plainly not there (no such name after a scan) is given up after 5 s.
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
    return true;
}

/// Connects to the first network that answers within <budgetMs> in all: the access point of the last connection first, by its channel
/// and address (no scan), then each network by name. An empty second network is skipped.
bool joinWifi(uint32_t budgetMs = 2 * kWifiTimeoutMs) {
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

/// Collects the body of a 200 into lastFrame-sized memory, chunked or not (HTTPClient::writeToStream).
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
    String etag;
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

/// Reads the picture; <sleep>: the person's sleep picture for a panel being switched off (no monitoring interval, short timeouts).
Reply fetchFrame(bool sleep = false) {
    Reply reply;
    const std::string url = sleep ? sleepUrl(settings.url.c_str()) : frameUrl(settings.url.c_str());
    const uint32_t connectTimeoutMs = sleep ? 5000 : 10000;
    const uint32_t startedMs = millis();
    if (!resolves(url)) {
        reply.fail = Fail::Dns;
        reply.tookMs = millis() - startedMs;
        return reply;
    }
    WiFiClientSecure secure;
    WiFiClient plain;
    HTTPClient http;
    http.setTimeout(sleep ? 6000 : 15000);
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
    if (const int mv = batteryReading(); mv > 0) http.addHeader("X-Battery-mV", String(mv));
    if (!sleep && lastEtag[0] && haveFrame) http.addHeader("If-None-Match", lastEtag);
    const char* keys[] = {"ETag", "X-Frame-Width", "X-Frame-Height", "X-Frame-Refresh", "Date"};
    http.collectHeaders(keys, 5);
    reply.code = http.GET();
    if (isHttps(url)) {
        char why[64] = "";
        reply.tlsError = secure.lastError(why, sizeof why);  // 0 unless the handshake went wrong
    }
    if (!sleep && (reply.code == 200 || reply.code == 304))
        refreshS = http.hasHeader("X-Frame-Refresh") ? static_cast<uint32_t>(http.header("X-Frame-Refresh").toInt()) : 0;
    if (const uint32_t now = httpDate(http.header("Date").c_str())) {
        const timeval tv = {static_cast<time_t>(now), 0};
        settimeofday(&tv, nullptr);  // the system clock keeps counting through deep sleep: how old the picture is
    }
    size_t bytes = 0;
    if (reply.code == 200) {
        FrameSink sink(incoming);
        http.writeToStream(&sink);
        bytes = sink.size();
        reply.etag = http.header("ETag");
    }
    reply.width = http.header("X-Frame-Width").toInt();
    reply.height = http.header("X-Frame-Height").toInt();
    reply.answer = classify(reply.code, bytes, reply.width, reply.height, kWidth, kHeight);
    reply.tookMs = millis() - startedMs;
    if (reply.answer == Answer::Retry) reply.fail = classifyFailure(reply.code, true, reply.tlsError, reply.tookMs, connectTimeoutMs);
    if (reply.code == 200 && reply.answer != Answer::Draw)
        Serial.printf("[monitor] frame of %dx%d, %u bytes: expected %dx%d\n", reply.width, reply.height, static_cast<unsigned>(bytes), kWidth, kHeight);
    http.end();
    return reply;
}

const char* const kCauseNames[] = {"none", "no wifi", "hive does not answer", "wrong frame size"};

/// No picture to put a banner on (a fresh panel, or the power was lost): the whole screen says what is wrong and what to do.
void showOfflineMessage(Offline cause, const Reply& reply, uint32_t retryMin) {
    const String retry = "Пробую снова через " + String(retryMin) + " мин";
    switch (cause) {
        case Offline::NoWifi:
            showMessage("Нет Wi-Fi", {"Сеть " + settings.ssid[0], "не отвечает.", "Проверьте роутер 2,4 ГГц.", retry, "BOOT 2 с — настройка", batteryLine()});
            break;
        case Offline::WrongFrame:
            showMessage("Не тот кадр", {"Улей прислал " + String(reply.width) + "x" + String(reply.height) + ".", "На странице монитора", "выберите плату",
                                        "Waveshare 1,54\".", retry, batteryLine()});
            break;
        default:
            showMessage("Нет связи с Ульем", {"Wi-Fi есть, Улей", "не отвечает.",
                                              "Причина: " + String(failureText(reply.fail, reply.code).c_str()), retry, "BOOT 2 с — настройка", batteryLine()});
            break;
    }
}

/// The read failed: the screen says so at once (on the old picture, or the whole screen when there is none), then only when the cause
/// changes or every half hour (e-ink flashes); the next try comes later each time (5, 10, 20, 30 min).
[[noreturn]] void offline(Offline cause, const Reply& reply) {
    failures++;
    const uint32_t retryMin = retryMinutes(failures, settings.intervalMin);
    const Age age = dataAge(nowUnix(), lastOkUnix, offlineMin);
    const bool due = bannerDue(cause, bannerCause, age.minutes, bannerAgeMin) || (haveFrame && messageShown);
    const Fail fail = cause == Offline::NoWifi ? Fail::NoWifi : cause == Offline::WrongFrame ? Fail::Frame : reply.fail;
    const std::string reason = failureText(fail, reply.code);
    lastFail = fail;
    lastFailCode = reply.code;
    Serial.printf("[monitor] why: %s, http %d, tls %d, took %lu ms, host %s\n", reason.c_str(), reply.code, reply.tlsError,
                  static_cast<unsigned long>(reply.tookMs), hostOf(settings.url.c_str()).c_str());
    Serial.printf("[monitor] offline: %s (http %d), failure %lu, picture %lu min old%s, next try in %lu min, %s\n", kCauseNames[static_cast<int>(cause)],
                  reply.code, static_cast<unsigned long>(failures), static_cast<unsigned long>(age.minutes), age.exact ? "" : " (counted)",
                  static_cast<unsigned long>(retryMin), due ? "screen redrawn" : "screen kept");
    if (due) {
        if (haveFrame) showFrame(String(offlineBanner(cause, age, true, reason).c_str()));
        else showOfflineMessage(cause, reply, retryMin);
        bannerCause = cause;
        bannerAgeMin = age.minutes;
    }
    offlineMin += retryMin;
    sleepFor(retryMin);
}

/// A read that worked: the next failure starts the backoff over, and the picture's age counts from now.
void readWorked() {
    failures = 0;
    offlineMin = 0;
    lastOkUnix = nowUnix();
}

[[noreturn]] void readAndSleep() {
    const String battery = batteryNote();
    switchedOff = false;
    if (!joinWifi()) {
        Serial.println("[monitor] wifi failed");
        offline(Offline::NoWifi, Reply());
    }

    const Reply reply = fetchFrame();
    Serial.printf("[monitor] frame %d\n", reply.code);  // the hive's page waits for this line after flashing
    switch (reply.answer) {
        case Answer::Draw:
            memcpy(lastFrame, incoming, kFrameBytes);
            haveFrame = true;
            snprintf(lastEtag, sizeof lastEtag, "%s", reply.etag.c_str());
            readWorked();
            showFrame(battery);
            break;
        case Answer::Keep:
            if (bannerCause != Offline::None || messageShown) showFrame(battery);  // take the banner off
            readWorked();
            break;
        case Answer::BadToken:
            showMessage("Токен не принят", {"Выдайте новый на", "странице монитора", "в Улье и введите его:", "BOOT 2 с — настройка", "", batteryLine()});
            sleepFor(kBadTokenSleepMin);
        case Answer::NotDrawn:
            showMessage("Нет картинки", {"Улей не рисует этот", "экран: на странице", "монитора выберите", "плату Waveshare 1,54\".", "", batteryLine()});
            sleepFor(kBadTokenSleepMin);
        case Answer::Retry:
            offline(reply.code == 200 ? Offline::WrongFrame : Offline::NoHive, reply);
    }
    sleepFor(nextReadSeconds(settings.intervalMin, refreshS) / 60);  // sooner while the hive monitors the panel
}

// ------------------------------------------------------------------ switched off (PWR)

const char* kSleepPath = "/sleep.bin";  // the person's sleep picture as the hive last sent it: shown when switched off offline

/// The flash for the sleep picture (the spiffs partition of huge_app.csv, as LittleFS), mounted only when switching off.
bool mountFlash() {
    static bool mounted = false;
    if (!mounted && !(mounted = LittleFS.begin(true))) Serial.println("[monitor] flash: no file system, the sleep picture is not kept");
    return mounted;
}

bool readSleepPicture(uint8_t* into) {
    if (!mountFlash() || !LittleFS.exists(kSleepPath)) return false;
    File f = LittleFS.open(kSleepPath, "r");
    const bool ok = f && f.size() == kFrameBytes && f.read(into, kFrameBytes) == kFrameBytes;
    if (f) f.close();
    return ok;
}

/// Keeps the sleep picture for the times there is no link; written only when it changed (the flash wears).
void keepSleepPicture(const uint8_t* bits) {
    static uint8_t kept[kFrameBytes];
    if (readSleepPicture(kept) && memcmp(kept, bits, kFrameBytes) == 0) return;
    if (!mountFlash()) return;
    File f = LittleFS.open(kSleepPath, "w");
    const bool ok = f && f.write(bits, kFrameBytes) == kFrameBytes;
    if (f) f.close();
    if (ok) Serial.println("[monitor] off: the sleep picture is kept for offline");
    else LittleFS.remove(kSleepPath);
}

/// PWR: the sleep picture, then off. Online, the hive's (kept in the flash too) or, without one, "Выключено"; offline, the kept one, or
/// a white screen that says there is no link. Wi-Fi gets one short try. On battery the board switches off; on USB it sleeps until a
/// button (PWR then switches it on again).
[[noreturn]] void switchOff() {
    Serial.println("[monitor] off: PWR");
    Offline cause = settings.ready() ? Offline::NoWifi : Offline::None;  // not set up: nothing to ask, nothing to say about the link
    bool drawn = false;
    if (cause != Offline::None && joinWifi(kSleepWifiMs)) {
        const Reply reply = fetchFrame(true);
        Serial.printf("[monitor] sleep frame %d (%s)\n", reply.code, failureText(reply.fail, reply.code).c_str());
        if (reply.answer == Answer::Draw) {
            keepSleepPicture(incoming);
            showBits(incoming, String());
            drawn = true;
        } else if (reply.answer == Answer::NotDrawn || reply.answer == Answer::BadToken) {
            cause = Offline::None;  // the hive answered: the link is there
            if (reply.answer == Answer::NotDrawn && mountFlash()) LittleFS.remove(kSleepPath);  // the person took the picture away
        } else {
            cause = reply.code == 200 ? Offline::WrongFrame : Offline::NoHive;
        }
    }
    if (!drawn && cause != Offline::None && readSleepPicture(incoming)) {
        Serial.println("[monitor] off: offline, the kept sleep picture");
        showBits(incoming, String());
        drawn = true;
    }
    if (!drawn && cause != Offline::None) {
        Serial.printf("[monitor] off: offline (%s), no kept picture\n", kCauseNames[static_cast<int>(cause)]);
        showMessage("Нет связи", {cause == Offline::NoWifi ? String("Нет Wi-Fi.") : String("Улей не отвечает."), "", "Панель выключается.",
                                  "Включение — кнопка PWR.", "", batteryLine()});
        drawn = true;
    }
    if (!drawn) showMessage("Выключено", {"Включение — кнопкой PWR", "", batteryLine()});
    messageShown = true;  // the picture is not on the screen: the next read draws it even on 304
    switchedOff = true;
    pinMode(kPinPwr, INPUT_PULLUP);
    for (const uint32_t started = millis(); digitalRead(kPinPwr) == LOW && millis() - started < 3000;) delay(10);  // still down: it would wake the board at once
    display.hibernate();
    digitalWrite(kPinPowerHold, LOW);  // on battery the board switches off here
    Serial.println("[monitor] off: asleep until a button (on USB power)");
    sleepFor(0);                        // on USB it keeps power: sleep until a button
}

/// Woken by BOOT: held for 2 s = the settings point, a short press = read now.
bool bootHeld() {
    pinMode(kPinBoot, INPUT_PULLUP);
    const uint32_t started = millis();
    while (millis() - started < 2000) {
        if (digitalRead(kPinBoot) == HIGH) return false;
        delay(20);
    }
    return true;
}

[[noreturn]] void saveAndSleep() {
    showMessage("Настроено", {"Wi-Fi: " + settings.ssid[0], "Первое чтение через", String(settings.intervalMin) + " мин у этой сети", "", "BOOT 2 с — настройка"});
    sleepFor(settings.intervalMin);
}

/// A moment for the console after a power-on or a reset (not after a timer or a button): the hive's page flashes the
/// board and sends the settings over USB. Says "ready" twice a second until somebody types, then waits for "go".
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

    // A start that ended the wired wait is a timer or a button, not a power-on: no console window, the button it remembered.
    const bool fromWired = wiredRestart;
    wiredRestart = false;
    const esp_sleep_wakeup_cause_t cause = fromWired ? (wiredButtons ? ESP_SLEEP_WAKEUP_EXT1 : ESP_SLEEP_WAKEUP_TIMER) : esp_sleep_get_wakeup_cause();
    const Order order = cause == ESP_SLEEP_WAKEUP_UNDEFINED ? consoleWindow() : Order::None;
    initScreen();
    if (order == Order::Save) saveAndSleep();

    const uint64_t buttons = cause == ESP_SLEEP_WAKEUP_EXT1 ? (fromWired ? wiredButtons : esp_sleep_get_ext1_wakeup_status()) : 0;
    Serial.printf("[monitor] wake: cause %d, buttons 0x%llx, offline tries %lu%s\n", static_cast<int>(cause), static_cast<unsigned long long>(buttons),
                  static_cast<unsigned long>(failures), switchedOff ? ", was switched off" : "");
    if (buttons & (1ULL << kPinPwr)) {
        if (!switchedOff) switchOff();
        switchedOff = false;  // on USB power PWR sleeps the panel and wakes it again: on as after BOOT
    }
    const bool wantPortal = !settings.ready() || ((buttons & (1ULL << kPinBoot)) && bootHeld());
    if (wantPortal) {
        portalMode = true;
        startPortal();
        return;
    }
    readAndSleep();
}

void loop() {
    if (!portalMode) return;
    dns.processNextRequest();
    server.handleClient();
    bool activity = false;
    if (const Order order = pollConsole(activity); order != Order::None) {  // settings came over USB: leave the Wi-Fi point
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
        showMessage("Не настроено", {"Панель не знает Wi-Fi", "и адрес Улья.", "", "BOOT 2 с — настройка", "с телефона, или прошейте", "со страницы монитора."});
        sleepFor(0);
    }
    delay(2);
}
