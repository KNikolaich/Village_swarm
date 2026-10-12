#include "Ota.h"

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

bool rollBack() {
#if defined(ESP32)
    return Update.canRollBack() && Update.rollBack();
#else
    return false;
#endif
}

bool download(const String& url, const String& token, uint32_t expectedSize, const String& md5, String& error, void (*feed)()) {
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
    if (!Update.end()) {
        error = "md5 or end failed (" + updateError() + ")";
        return false;
    }
    return true;
}

}  // namespace ota
}  // namespace hornet
