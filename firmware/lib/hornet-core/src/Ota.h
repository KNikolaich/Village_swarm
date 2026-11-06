#pragma once

// Over-the-air update (spec 5.5): download the image the hive points to, check its MD5, write it to the update
// partition (ESP32) or the free flash behind the sketch (ESP8266, the bootloader copies it on restart).

#include <Arduino.h>

namespace hornet {
namespace ota {

// Downloads `url` (plain HTTP, Bearer token when given), installs it and returns true when the image is complete and
// its MD5 matches. On failure `error` says why and nothing is installed. `feed` runs while downloading (watchdog).
// The caller restarts the chip.
// `signatureHex` (Ed25519 over the image's SHA-512, 128 hex characters) is checked against the public key built into
// the firmware before the image is marked for the bootloader; see OtaSignature.h.
bool download(const String& url, const String& token, uint32_t expectedSize, const String& md5, const String& signatureHex,
              String& error, void (*feed)());

// This build carries a public key and only accepts images signed with the matching private key (unless it was built
// to accept unsigned images too, HORNET_ALLOW_UNSIGNED=1 for development).
bool signatureRequired();

// Bytes available for a new image.
uint32_t freeSpace();

// ESP32 only: switch back to the previous image. Returns false when there is none (or on ESP8266).
bool rollBack();

}  // namespace ota
}  // namespace hornet
