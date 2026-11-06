#pragma once

// ESP-01 builds moved their file system (the settings) from the board default, 256 KB at 0xBB000
// (eagle.flash.1m256.ld), to 64 KB at 0xEB000 (eagle.flash.1m64.ld) so an over-the-air image fits (docs/ota.md).
// A board flashed by USB from an older build would find nothing at the new place and lose its Wi-Fi, hive address and
// broker login. On its first boot the new image copies them from the old place (platform::adoptOldSettings).
// The decisions live here, Arduino-free, so native tests cover them.

#include <cstdint>
#include <string>

namespace hornet {
namespace legacyfs {

struct Region {
    uint32_t start;
    uint32_t size;
};

// Where builds before the 64 KB layout kept the settings, or an empty region when this layout never moved.
constexpr Region previousFor(uint32_t start, uint32_t size) {
    return start == 0xEB000 && size == 0x10000 ? Region{0xBB000, 0x40000} : Region{0, 0};
}

// Copy only onto a board that has no hornet at the new place yet, from an old place that still holds one.
constexpr bool shouldCopy(bool currentHasId, bool previousHasId) { return !currentHasId && previousHasId; }

// Preferences keeps one file per key in /nvs/<namespace>/. The retained config is not copied: the hive sends it again
// when the hornet connects, and its blocks may sit in the part the new file system already took. Names starting with
// '\a' are leftovers of an interrupted write, not settings.
inline bool copyKey(const std::string& name) { return !name.empty() && name[0] != '\a' && name != "config"; }

}  // namespace legacyfs
}  // namespace hornet
