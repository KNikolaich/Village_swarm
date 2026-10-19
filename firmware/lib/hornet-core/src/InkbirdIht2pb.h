#pragma once

// Notification frames of the Inkbird IHT-2PB kitchen thermometer (GATT service FFE0, notifications on FFE4).
// The layout is read from the open ESPHome and MQTT projects for this device, not from a datasheet:
//   byte 2        probe selector: 2, 4 or 6 (probe 1, 2, 3)
//   bytes 4..5    temperature in tenths of a degree Celsius, big endian, two's complement
// Bytes 0, 1 and 3 are not used (0x55 0xAA is the header). A frame that does not look like this is ignored.
// Turn on "dump" in the hornet's config to see the raw frames in the hive's log and check them against the app.

#include <stddef.h>
#include <stdint.h>

namespace hornet::inkbird {

constexpr int kProbes = 3;

// What a probe that is not plugged in reports is not known; these are never a real kitchen temperature.
constexpr float kMinC = -50.0F;
constexpr float kMaxC = 300.0F;

struct Reading {
    int probe;      // 0..2
    float celsius;
};

inline bool parse(const uint8_t* d, size_t n, Reading& out) {
    if (d == nullptr || n < 6) return false;
    if (d[2] != 2 && d[2] != 4 && d[2] != 6) return false;
    const int16_t raw = static_cast<int16_t>((static_cast<uint16_t>(d[4]) << 8) | d[5]);
    if (raw == -1) return false;  // 0xFFFF: no value
    const float c = raw / 10.0F;
    if (c < kMinC || c > kMaxC) return false;
    out.probe = d[2] / 2 - 1;
    out.celsius = c;
    return true;
}

// Metric of a probe: temperature, temperature_2, temperature_3 (like the other roles).
inline const char* metricName(int probe) {
    switch (probe) {
        case 0: return "temperature";
        case 1: return "temperature_2";
        default: return "temperature_3";
    }
}

}  // namespace hornet::inkbird
