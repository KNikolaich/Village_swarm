#pragma once

// The few things that differ between ESP32 (classic, C3, CAM) and ESP8266 (ESP-01, NodeMCU, D1 mini).
// Everything else in hornet-core is the same code for both.

#include <Arduino.h>

#include <utility>
#include <vector>

#if defined(ESP32)
#include <WiFi.h>
#elif defined(ESP8266)
#include <ESP8266WiFi.h>
#else
#error "hornet-core supports ESP32 and ESP8266"
#endif

namespace hornet {
namespace platform {

// Restart if the main loop hangs (spec 4.5: 30 s on ESP32; ESP8266 has a fixed hardware watchdog of a few seconds).
void watchdogBegin(uint32_t timeoutMs);
void watchdogFeed();

void fillRandom(uint8_t* buf, size_t len);

// poweron | software | panic | watchdog | brownout | deepsleep | external | unknown (health.reset_reason).
const char* resetReason();

// esp32 | esp32c3 | esp32s3 | esp8266
const char* chip();

bool hasPsram();
uint32_t freePsram();

using Headers = std::vector<std::pair<String, String>>;

// Plain HTTP POST; returns the status code (negative on connection errors) and fills response.
int httpPost(const String& url, const char* contentType, const uint8_t* body, size_t len, const Headers& headers,
             String* response = nullptr, uint16_t timeoutMs = 10000);

}  // namespace platform
}  // namespace hornet
