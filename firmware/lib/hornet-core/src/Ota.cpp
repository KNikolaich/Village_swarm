#include "Ota.h"

#include "OtaSignature.h"
#include "Platform.h"

#if defined(ESP32)
#include <HTTPClient.h>
#include <Update.h>
#elif defined(ESP8266)
#include <ESP8266HTTPClient.h>
#include <Updater.h>
#endif

namespace hornet {
namespace ota {

namespace {
constexpr uint32_t kStallMs = 15000;

// Set by scripts/version.py from firmware/ota_public_key.hex; a build without it is a development build.
#if defined(HORNET_OTA_PUBKEY)
constexpr const char* kPublicKey = HORNET_OTA_PUBKEY;
#else
constexpr const char* kPublicKey = "";
#endif
#if defined(HORNET_OTA_ALLOW_UNSIGNED)
constexpr bool kAllowUnsigned = true;
#else
constexpr bool kAllowUnsigned = false;
#endif
constexpr bool hasKey() { return kPublicKey[0] != 0; }

String updateError() { return "update error " + String(static_cast<int>(Update.getError())); }

// Drops a half-written image. The ESP8266 Updater has no abort(): ending it with the MD5 set fails on partial
// data and, like abort(), never marks the image for the bootloader.
void abortUpdate() {
#if defined(ESP32)
    Update.abort();
#else
    Update.end(true);
#endif
}
}  // namespace

uint32_t freeSpace() { return ESP.getFreeSketchSpace(); }

bool signatureRequired() { return hasKey() && !kAllowUnsigned; }

bool rollBack() {
#if defined(ESP32)
    return Update.canRollBack() && Update.rollBack();
#else
    return false;
#endif
}

bool download(const String& url, const String& token, uint32_t expectedSize, const String& md5, const String& signatureHex,
              String& error, void (*feed)()) {
    if (otasig::checkCommand(hasKey(), kAllowUnsigned, signatureHex.c_str()) == otasig::Verdict::RefuseUnsigned) {
        error = "unsigned image refused";
        return false;
    }
    WiFiClient plain;
    HTTPClient http;
    http.setTimeout(kStallMs);
#if defined(ESP8266)
    const bool began = http.begin(plain, url);
#else
    const bool began = http.begin(url);
#endif
    if (!began) {
        error = "bad url";
        return false;
    }
    if (token.length()) http.addHeader("Authorization", "Bearer " + token);

    const int code = http.GET();
    if (code != 200) {
        error = "HTTP " + String(code);
        http.end();
        return false;
    }
    const int length = http.getSize();
    if (length <= 0 || static_cast<uint32_t>(length) != expectedSize) {
        error = "size " + String(length) + " != " + String(expectedSize);
        http.end();
        return false;
    }
    if (!Update.begin(static_cast<size_t>(length))) {
        error = "does not fit (" + updateError() + ")";
        http.end();
        return false;
    }
    Update.setMD5(md5.c_str());

    WiFiClient* stream = http.getStreamPtr();
    otasig::ImageHasher hasher;
    uint8_t buffer[512];
    uint32_t written = 0;
    uint32_t lastData = millis();
    while (written < static_cast<uint32_t>(length)) {
        if (feed) feed();
        const size_t available = stream->available();
        if (available) {
            const size_t n = stream->readBytes(buffer, min(available, sizeof buffer));
            if (Update.write(buffer, n) != n) {
                error = "flash write failed (" + updateError() + ")";
                abortUpdate();
                http.end();
                return false;
            }
            hasher.update(buffer, n);
            written += n;
            lastData = millis();
        } else {
            if (!stream->connected()) break;
            if (millis() - lastData > kStallMs) {
                error = "download stalled at " + String(written);
                abortUpdate();
                http.end();
                return false;
            }
            delay(1);
        }
    }
    http.end();
    if (written != static_cast<uint32_t>(length)) {
        error = "incomplete: " + String(written) + " of " + String(length);
        abortUpdate();
        return false;
    }
    // The signature is judged on everything that was received, and before Update.end() marks the image for the
    // bootloader: a rejected image is dropped and the running firmware is untouched.
    if (hasKey() && signatureHex.length()) {
        uint8_t digest[otasig::kDigestBytes];
        hasher.finish(digest);
        if (!otasig::verify(digest, signatureHex.c_str(), kPublicKey)) {
            error = "signature does not match";
            abortUpdate();
            return false;
        }
    }
    if (!Update.end()) {
        error = "md5 or end failed (" + updateError() + ")";
        return false;
    }
    return true;
}

}  // namespace ota
}  // namespace hornet
