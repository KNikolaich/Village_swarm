#pragma once

// ULID (https://github.com/ulid/spec): 48-bit unix ms + 80 random bits, Crockford base32, 26 chars.
// Message ids and photo ids in the MQTT contract are ULIDs (contracts/schemas/common.schema.json).
// Arduino-free: the caller passes the time and the random bytes (esp_random on the device).

#include <cstddef>
#include <cstdint>

namespace hornet {

constexpr size_t kUlidLength = 26;

// Writes 26 chars plus '\0' into out (at least 27 bytes).
inline void encodeUlid(uint64_t unixMs, const uint8_t random[10], char* out) {
    static const char kAlphabet[] = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    uint8_t bytes[16];
    for (int i = 5; i >= 0; --i) {
        bytes[i] = static_cast<uint8_t>(unixMs & 0xFF);
        unixMs >>= 8;
    }
    for (int i = 0; i < 10; ++i) bytes[6 + i] = random[i];

    // 128 bits -> 26 base32 digits, most significant first; the first digit holds the top 3 bits.
    for (int i = 25; i >= 0; --i) {
        const int bit = (25 - i) * 5;  // bit offset from the least significant end
        uint32_t value = 0;
        for (int b = 0; b < 5; ++b) {
            const int pos = bit + b;
            if (pos >= 128) break;
            const int byteIndex = 15 - pos / 8;
            if ((bytes[byteIndex] >> (pos % 8)) & 1) value |= 1u << b;
        }
        out[i] = kAlphabet[value];
    }
    out[kUlidLength] = '\0';
}

// Decodes the timestamp part; returns 0 for a malformed id.
inline uint64_t ulidTimestamp(const char* id) {
    uint64_t ms = 0;
    for (int i = 0; i < 10; ++i) {
        const char c = id[i];
        int v;
        if (c >= '0' && c <= '9') v = c - '0';
        else if (c >= 'A' && c <= 'H') v = c - 'A' + 10;
        else if (c == 'J' || c == 'K') v = c - 'J' + 18;
        else if (c == 'M' || c == 'N') v = c - 'M' + 20;
        else if (c >= 'P' && c <= 'T') v = c - 'P' + 22;
        else if (c >= 'V' && c <= 'Z') v = c - 'V' + 27;
        else return 0;
        ms = (ms << 5) | static_cast<uint64_t>(v);
    }
    return ms;
}

}  // namespace hornet
