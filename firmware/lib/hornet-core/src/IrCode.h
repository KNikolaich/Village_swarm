#pragma once

// Infrared codes as the hive writes them (contracts/schemas/cmd-ir.schema.json) and as Tasmota does:
// protocol name, bits, data as hex ("NEC 32 0x02FD807F"). Arduino-free, so it builds in the native test env.

#include <cstdint>
#include <cstring>
#include <string>

namespace hornet {
namespace ir {

// "0x02FD807F" -> value. 1 to 16 hex digits after 0x or 0X, nothing else.
inline bool parseHex(const char* text, uint64_t& out) {
    if (text == nullptr || text[0] != '0' || (text[1] != 'x' && text[1] != 'X')) return false;
    const char* p = text + 2;
    const size_t digits = strlen(p);
    if (digits < 1 || digits > 16) return false;
    uint64_t value = 0;
    for (; *p; ++p) {
        const char c = *p;
        uint8_t d;
        if (c >= '0' && c <= '9') d = static_cast<uint8_t>(c - '0');
        else if (c >= 'a' && c <= 'f') d = static_cast<uint8_t>(c - 'a' + 10);
        else if (c >= 'A' && c <= 'F') d = static_cast<uint8_t>(c - 'A' + 10);
        else return false;
        value = (value << 4) | d;
    }
    out = value;
    return true;
}

// Upper-case hex with as many digits as the bits need: 32 bits -> 0x02FD807F, 12 bits -> 0x0A1.
inline std::string formatHex(uint64_t value, int bits) {
    static const char* kDigits = "0123456789ABCDEF";
    int digits = (bits + 3) / 4;
    if (digits < 1) digits = 1;
    if (digits > 16) digits = 16;
    std::string s = "0x";
    for (int i = digits - 1; i >= 0; --i) s += kDigits[(value >> (4 * i)) & 0xF];
    return s;
}

// What the receiver keeps in its state ir_last: "NEC 32 0x02FD807F".
inline std::string describe(const char* protocol, int bits, uint64_t value) {
    return std::string(protocol) + " " + std::to_string(bits) + " " + formatHex(value, bits);
}

// A name the hive may send (letters, digits, underscore; 2 to 24 characters).
inline bool validProtocolName(const char* name) {
    if (name == nullptr) return false;
    const size_t n = strlen(name);
    if (n < 2 || n > 24) return false;
    for (size_t i = 0; i < n; ++i) {
        const char c = name[i];
        const bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
        if (!ok) return false;
    }
    return true;
}

inline bool validBits(int bits) { return bits >= 1 && bits <= 64; }

}  // namespace ir
}  // namespace hornet
