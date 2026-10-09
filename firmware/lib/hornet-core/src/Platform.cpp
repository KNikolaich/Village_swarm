#include "Platform.h"

#if defined(ESP32)
#include <HTTPClient.h>
#include <esp_random.h>
#include <esp_system.h>
#include <esp_task_wdt.h>
#elif defined(ESP8266)
#include <ESP8266HTTPClient.h>
#include <user_interface.h>
#endif

namespace hornet {
namespace platform {

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

#endif

}  // namespace platform
}  // namespace hornet
