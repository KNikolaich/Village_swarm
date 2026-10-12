#pragma once

// Over-the-air update (spec 5.5): download the image the hive points to, check its MD5, write it to the update
// partition (ESP32) or the free flash behind the sketch (ESP8266, the bootloader copies it on restart).

#include <Arduino.h>

namespace hornet {
namespace ota {

// Downloads `url` (plain HTTP, Bearer token when given), installs it and returns true when the image is complete and
// its MD5 matches. On failure `error` says why and nothing is installed. `feed` runs while downloading (watchdog).
// The caller restarts the chip.
bool download(const String& url, const String& token, uint32_t expectedSize, const String& md5, String& error, void (*feed)());

// Bytes available for a new image.
uint32_t freeSpace();

// ESP32 only: switch back to the previous image. Returns false when there is none (or on ESP8266).
bool rollBack();

}  // namespace ota
}  // namespace hornet
