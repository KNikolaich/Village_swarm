#include "Platform.h"

#if defined(ESP32)
#include <HTTPClient.h>
#include <esp_random.h>
#include <esp_sleep.h>
#include <esp_system.h>
#include <esp_task_wdt.h>
#elif defined(ESP8266)
#include <ESP8266HTTPClient.h>
#include <LittleFS.h>
#include <user_interface.h>

#include "LegacyFs.h"
#endif

namespace hornet {
namespace platform {

namespace {
constexpr uint32_t kHintMagic = 0x56534831;  // "VSH1": RTC memory holds a hint written by this firmware
struct RtcHint {
    uint32_t magic;
    uint8_t channel;
    uint8_t bssid[6];
    uint8_t pad;  // RTC user memory is read and written in 4-byte words
};
}  // namespace

#if defined(ESP32)

void watchdogBegin(uint32_t timeoutMs) {
    esp_task_wdt_config_t wdt = {.timeout_ms = timeoutMs, .idle_core_mask = 0, .trigger_panic = true};
    esp_task_wdt_reconfigure(&wdt);
    esp_task_wdt_add(nullptr);
}

void watchdogFeed() { esp_task_wdt_reset(); }

void fillRandom(uint8_t* buf, size_t len) { esp_fill_random(buf, len); }

const char* resetReason() {
    switch (esp_reset_reason()) {
        case ESP_RST_POWERON: return "poweron";
        case ESP_RST_SW: return "software";
        case ESP_RST_PANIC: return "panic";
        case ESP_RST_INT_WDT:
        case ESP_RST_TASK_WDT:
        case ESP_RST_WDT: return "watchdog";
        case ESP_RST_BROWNOUT: return "brownout";
        case ESP_RST_DEEPSLEEP: return "deepsleep";
        case ESP_RST_EXT: return "external";
        default: return "unknown";
    }
}

const char* chip() {
#if CONFIG_IDF_TARGET_ESP32C3
    return "esp32c3";
#elif CONFIG_IDF_TARGET_ESP32S3
    return "esp32s3";
#else
    return "esp32";
#endif
}

RTC_DATA_ATTR RtcHint rtcHint;

void deepSleep(uint64_t microseconds) {
    esp_sleep_enable_timer_wakeup(microseconds);
    esp_deep_sleep_start();
}

bool loadWifiHint(WifiHint& hint) {
    if (rtcHint.magic != kHintMagic || rtcHint.channel == 0) return false;
    hint.channel = rtcHint.channel;
    memcpy(hint.bssid, rtcHint.bssid, sizeof hint.bssid);
    return true;
}

void saveWifiHint(const WifiHint& hint) {
    rtcHint.magic = kHintMagic;
    rtcHint.channel = hint.channel;
    memcpy(rtcHint.bssid, hint.bssid, sizeof hint.bssid);
}

void clearWifiHint() { rtcHint.magic = 0; }

bool hasPsram() { return psramFound(); }
uint32_t freePsram() { return psramFound() ? ESP.getFreePsram() : 0; }

int httpPost(const String& url, const char* contentType, const uint8_t* body, size_t len, const Headers& headers,
             String* response, uint16_t timeoutMs) {
    HTTPClient http;
    http.setTimeout(timeoutMs);
    if (!http.begin(url)) return -1;
    http.addHeader("Content-Type", contentType);
    for (const auto& [k, v] : headers) http.addHeader(k, v);
    const int code = http.POST(const_cast<uint8_t*>(body), len);
    if (response && code > 0) *response = http.getString();
    http.end();
    return code;
}

bool adoptOldSettings() { return false; }  // NVS partitions have never moved

#elif defined(ESP8266)

// The ESP8266 hardware watchdog (~6 s) cannot be stretched to 30 s; the loop must stay short anyway.
void watchdogBegin(uint32_t) { ESP.wdtEnable(WDTO_8S); }

void watchdogFeed() { ESP.wdtFeed(); }

void fillRandom(uint8_t* buf, size_t len) {
    for (size_t i = 0; i < len; i += 4) {
        const uint32_t r = ESP.random();
        for (size_t b = 0; b < 4 && i + b < len; ++b) buf[i + b] = static_cast<uint8_t>(r >> (8 * b));
    }
}

const char* resetReason() {
    const rst_info* info = ESP.getResetInfoPtr();
    switch (info->reason) {
        case REASON_DEFAULT_RST: return "poweron";
        case REASON_SOFT_RESTART: return "software";
        case REASON_EXCEPTION_RST: return "panic";
        case REASON_WDT_RST:
        case REASON_SOFT_WDT_RST: return "watchdog";
        case REASON_DEEP_SLEEP_AWAKE: return "deepsleep";
        case REASON_EXT_SYS_RST: return "external";
        default: return "unknown";
    }
}

const char* chip() { return "esp8266"; }

void deepSleep(uint64_t microseconds) {
    ESP.deepSleep(microseconds);
    for (;;) delay(100);  // not reached: the chip is off until GPIO16 pulls RST low
}

bool loadWifiHint(WifiHint& hint) {
    RtcHint rtc{};
    if (!ESP.rtcUserMemoryRead(0, reinterpret_cast<uint32_t*>(&rtc), sizeof rtc) || rtc.magic != kHintMagic || rtc.channel == 0)
        return false;
    hint.channel = rtc.channel;
    memcpy(hint.bssid, rtc.bssid, sizeof hint.bssid);
    return true;
}

void saveWifiHint(const WifiHint& hint) {
    RtcHint rtc{kHintMagic, hint.channel, {}, 0};
    memcpy(rtc.bssid, hint.bssid, sizeof rtc.bssid);
    ESP.rtcUserMemoryWrite(0, reinterpret_cast<uint32_t*>(&rtc), sizeof rtc);
}

void clearWifiHint() {
    RtcHint rtc{};
    ESP.rtcUserMemoryWrite(0, reinterpret_cast<uint32_t*>(&rtc), sizeof rtc);
}

bool hasPsram() { return false; }
uint32_t freePsram() { return 0; }

int httpPost(const String& url, const char* contentType, const uint8_t* body, size_t len, const Headers& headers,
             String* response, uint16_t timeoutMs) {
    WiFiClient client;
    HTTPClient http;
    http.setTimeout(timeoutMs);
    if (!http.begin(client, url)) return -1;
    http.addHeader("Content-Type", contentType);
    for (const auto& [k, v] : headers) http.addHeader(k, v);
    const int code = http.POST(body, len);
    if (response && code > 0) *response = http.getString();
    http.end();
    return code;
}

namespace {

constexpr const char* kSettingsDir = "/nvs/hornet";  // Preferences namespace "hornet" (Settings.cpp)

struct SettingFile {
    String name;
    std::vector<uint8_t> data;
};

// Reads the settings files worth copying; true when a device id is among them.
bool readSettings(fs::FS& fs, std::vector<SettingFile>& out) {
    bool hasId = false;
    Dir dir = fs.openDir(kSettingsDir);
    while (dir.next()) {
        if (!dir.isFile() || !legacyfs::copyKey(dir.fileName().c_str())) continue;
        File f = dir.openFile("r");
        if (!f) return false;
        SettingFile s{dir.fileName(), std::vector<uint8_t>(f.size())};
        const size_t n = f.read(s.data.data(), s.data.size());
        f.close();
        if (n != s.data.size()) return false;
        if (s.name == "id" && !s.data.empty()) hasId = true;
        out.push_back(std::move(s));
    }
    return hasId;
}

}  // namespace

bool adoptOldSettings() {
    const legacyfs::Region old = legacyfs::previousFor(FS_PHYS_ADDR, FS_PHYS_SIZE);
    if (old.size == 0) return false;

    // Look without formatting: a format at the new place would wipe the tail of the old one.
    LittleFS.setConfig(LittleFSConfig(false));
    const bool currentHasId = LittleFS.begin() && LittleFS.exists(String(kSettingsDir) + "/id");
    LittleFS.end();
    LittleFS.setConfig(LittleFSConfig(true));
    if (currentHasId) return false;

    // All of it into RAM first (a few hundred bytes): the two places overlap.
    std::vector<SettingFile> saved;
    bool previousHasId = false;
    {
        fs::FS previous(fs::FSImplPtr(new littlefs_impl::LittleFSImpl(old.start, old.size, FS_PHYS_PAGE, FS_PHYS_BLOCK, 5)));
        previous.setConfig(LittleFSConfig(false));  // never format the old place: no file system there is an answer
        if (!previous.begin()) return false;
        previousHasId = readSettings(previous, saved);
        previous.end();
    }
    if (!legacyfs::shouldCopy(currentHasId, previousHasId)) return false;

    if (!LittleFS.begin()) return false;  // formats the new place when it holds no file system yet
    bool ok = true;
    for (const auto& s : saved) {
        File f = LittleFS.open(String(kSettingsDir) + "/" + s.name, "w");
        ok = f && f.write(s.data.data(), s.data.size()) == s.data.size();
        f.close();
        if (!ok) break;
    }
    LittleFS.end();
    if (!ok) return false;

    // The old place is room for over-the-air images now. Wiping its superblocks keeps the copy from ever happening
    // again, so settings cleared on purpose do not come back.
    ESP.flashEraseSector(old.start / FS_PHYS_BLOCK);
    ESP.flashEraseSector(old.start / FS_PHYS_BLOCK + 1);
    return true;
}

#endif

}  // namespace platform
}  // namespace hornet
