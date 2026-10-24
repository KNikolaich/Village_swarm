// Role guard-cam (spec 5.4) on ESP32-CAM AI-Thinker:
//   PIR rising edge -> cooldown -> burst of N frames into PSRAM -> event/motion with photo ids
//   -> each frame written to SD /outbox (so it survives a reboot), then POSTed to {upload}/api/ingest/photo;
//   delivered photos move to /archive/<ULID prefix>/, undelivered ones stay in /outbox and are retried.
//   The SD card is a ring: before each write the oldest archive folders are deleted to keep
//   max(5% of the card, 32 MB) free, and only with an empty archive the oldest undelivered photos.
//   Without a card (or when the write fails) a photo gets 3 upload attempts (now, +2 s, +5 s) and is then lost.
//   Upload failures, lost photos and SD trouble go to the hive journal (log, via loop()).
//   cmd/relay {channel, set: on|off, for_s} switches one output (white flash LED, or a relay for a floodlight)
//   on hw.relay_pin (default GPIO4), like the relay role: off at every boot, retained state/{channel}.
// Uploads run in their own task so the main loop (MQTT, watchdog) never waits on HTTP.
//
// Pins: PIR on GPIO13 (spec). The SD card runs in 1-bit mode so GPIO4/12/13 stay free
// (GPIO4 is also the white flash LED, which 1-bit mode leaves alone). The output pin may be any free one;
// camera, SD, PSRAM and PIR pins are refused, the output stays on the old pin then.

#include <HTTPClient.h>
#include <HornetCore.h>
#include <Outbox.h>
#include <PhotoArchive.h>
#include <RelayTimer.h>
#include <SD_MMC.h>
#include <WiFi.h>
#include <esp_camera.h>
#include <esp_timer.h>

#include <cstdarg>
#include <string>
#include <vector>

namespace {

using namespace hornet;

constexpr int kPirPin = 13;
constexpr const char* kOutboxDir = "/outbox";
constexpr const char* kArchiveDir = "/archive";
constexpr uint32_t kSdRetryMs = 30000;
constexpr uint16_t kUploadTimeoutMs = 15000;
// Undelivered photos retried per pass, and dropped at once when the card is full and the archive is empty.
constexpr size_t kOutboxBatch = 5;
// Upload results that never reached the hive as an HTTP status.
constexpr int kNoUploadUrl = -1000;
constexpr int kNoWifi = -1001;
constexpr int kBadUploadUrl = -1002;
// Grabs per photo while the driver keeps handing out a frame started before the request.
constexpr int kFreshFrameTries = 3;

// Pins the output must never take: camera, SD (1-bit), PSRAM, PIR, and the input-only 34..39.
bool outputPinAllowed(int pin) {
    switch (pin) {
        case 0: case 2: case 5: case 13: case 14: case 15: case 16: case 17: case 18: case 19:
        case 21: case 22: case 23: case 25: case 26: case 27: case 32:
            return false;
        default:
            return pin >= 0 && pin < 34;
    }
}

// AI-Thinker camera pins.
camera_config_t cameraConfig() {
    camera_config_t c = {};
    c.pin_pwdn = 32;
    c.pin_reset = -1;
    c.pin_xclk = 0;
    c.pin_sccb_sda = 26;
    c.pin_sccb_scl = 27;
    c.pin_d7 = 35;
    c.pin_d6 = 34;
    c.pin_d5 = 39;
    c.pin_d4 = 36;
    c.pin_d3 = 21;
    c.pin_d2 = 19;
    c.pin_d1 = 18;
    c.pin_d0 = 5;
    c.pin_vsync = 25;
    c.pin_href = 23;
    c.pin_pclk = 22;
    // 10 MHz and one buffer: frames are grabbed on demand. With two buffers the driver streams non-stop and logs
    // "cam_hal: DMA overflow" 25 times a second whenever Wi-Fi or PSRAM traffic holds the DMA up.
    c.xclk_freq_hz = 10000000;
    c.ledc_timer = LEDC_TIMER_0;
    c.ledc_channel = LEDC_CHANNEL_0;
    c.pixel_format = PIXFORMAT_JPEG;
    c.frame_size = FRAMESIZE_SVGA;
    c.jpeg_quality = 12;
    c.fb_count = 1;
    c.fb_location = CAMERA_FB_IN_PSRAM;
    c.grab_mode = CAMERA_GRAB_WHEN_EMPTY;
    return c;
}

framesize_t frameSize(const char* name) {
    if (!name) return FRAMESIZE_SVGA;
    const String n = name;
    if (n == "QVGA") return FRAMESIZE_QVGA;
    if (n == "VGA") return FRAMESIZE_VGA;
    if (n == "XGA") return FRAMESIZE_XGA;
    if (n == "HD") return FRAMESIZE_HD;
    if (n == "SXGA") return FRAMESIZE_SXGA;
    if (n == "UXGA") return FRAMESIZE_UXGA;
    return FRAMESIZE_SVGA;
}

// One JPEG waiting for upload. The buffer is a PSRAM copy owned by the upload task.
struct Photo {
    char photoId[32];
    char eventId[28];
    char eventType[16];
    uint64_t ts;
    uint8_t* jpeg;
    size_t len;
};

// A journal line from the upload task: Core is not thread-safe, so loop() hands these to core.log.
struct Note {
    LogLevel level;
    char text[160];
};

class GuardCam : public Role {
public:
    const char* type() const override { return "guard-cam"; }
    const char* hw() const override { return HORNET_BOARD; }
    std::vector<const char*> caps() const override { return {"camera", "motion.pir", "sd", "relay"}; }

    void setup(Core& core) override {
        core_ = &core;
        pinMode(kPirPin, INPUT_PULLDOWN);
        configureOutput(core, HW_RELAY_PIN, HW_RELAY_ACTIVE_LOW != 0);

        cameraErr_ = esp_camera_init(&cameraConfigured());
        cameraOk_ = cameraErr_ == ESP_OK;
        if (!cameraOk_) Serial.printf("[guard-cam] camera init failed: 0x%x\n", static_cast<unsigned>(cameraErr_));
        sdOk_ = SD_MMC.begin("/sdcard", true) && (SD_MMC.exists(kOutboxDir) || SD_MMC.mkdir(kOutboxDir)) &&
                (SD_MMC.exists(kArchiveDir) || SD_MMC.mkdir(kArchiveDir));
        if (sdOk_) sdPending_ = tidyOutbox();
        setOutput(false);  // again after the SD driver set its pins up: the output starts off whatever the pin

        notes_ = xQueueCreate(8, sizeof(Note));
        queue_ = xQueueCreate(12, sizeof(Photo));
        xTaskCreatePinnedToCore(uploadTask, "upload", 8192, this, 1, nullptr, 0);

        core.onCommand("arm", [this](JsonObjectConst p) {
            if (!p["armed"].is<bool>()) return AckResult::error("armed (bool) is required");
            armed_ = p["armed"];
            AckResult r;
            r.state["armed"] = armed_;
            return r;
        });
        core.onCommand("snapshot", [this](JsonObjectConst p) {
            if (!cameraOk_) return AckResult::error(String("camera not available (init error 0x") + String(static_cast<unsigned>(cameraErr_), HEX) + ")");
            startBurst("snapshot", constrain(p["count"] | 1, 1, 10));
            return AckResult::ok();
        });
        core.onCommand("relay", [this](JsonObjectConst p) { return relayCommand(p); });
    }

    void applyConfig(Core& core, JsonObjectConst config) override {
        JsonObjectConst hw = config["hw"];
        configureOutput(core, hw["relay_pin"] | HW_RELAY_PIN, hw["relay_active_low"] | (HW_RELAY_ACTIVE_LOW != 0));
        JsonObjectConst relay = config["relay"];
        const char* ch = relay["channel"] | "relay";
        if (topics::isValidSegment(ch)) channel_ = ch;
        maxOnS_ = relay["max_on_s"] | 0U;
        publishOutput(core);
        JsonObjectConst guard = config["guard"];
        if (!guard.isNull()) {
            if (guard["armed"].is<bool>()) armed_ = guard["armed"];
            cooldownMs_ = (guard["cooldown_s"] | 20U) * 1000UL;
            recordWhenDisarmed_ = guard["record_when_disarmed"] | false;
        }
        JsonObjectConst camera = config["camera"];
        if (!camera.isNull()) {
            burst_ = constrain(camera["burst"] | 3, 1, 10);
            burstIntervalMs_ = camera["burst_interval_ms"] | 700U;
            if (cameraOk_) {
                sensor_t* s = esp_camera_sensor_get();
                s->set_framesize(s, frameSize(camera["frame"]));
                s->set_quality(s, camera["quality"] | 12);
            }
        }
        if (config["upload"]["primary"].is<const char*>()) uploadUrl_ = config["upload"]["primary"].as<String>();
    }

    void loop(Core& core) override {
        // Said once, as soon as the hive can hear it: a camera that did not start explains every failed snapshot.
        if (!cameraOk_ && !cameraErrLogged_ && core.online()) {
            cameraErrLogged_ = true;
            core.log(LogLevel::Error, String("camera init failed: 0x") + String(static_cast<unsigned>(cameraErr_), HEX) + " (power, ribbon cable or camera settings)");
        }
        if (!sdLogged_ && core.online()) {
            sdLogged_ = true;
            logSdStatus(core);
        }
        Note note;
        for (int i = 0; i < 4 && notes_ && core.online() && xQueueReceive(notes_, &note, 0) == pdTRUE; ++i) core.log(note.level, note.text);
        const bool motion = digitalRead(kPirPin) == HIGH;
        if (motion && !lastPir_ && cooldown_.tryFire(millis(), cooldownMs_)) onMotion(core);
        lastPir_ = motion;
        captureStep(core);
        if (outputOn_ && outputTimer_.expired(millis())) {
            setOutput(false);
            publishOutput(core);
            core.log(LogLevel::Info, "relay off: time limit");
        }
        if (!outputPublished_ && core.online()) publishOutput(core);
    }

    void fillHealth(JsonObject health) override {
        if (queue_) health["outbox"] = (health["outbox"] | 0) + uxQueueMessagesWaiting(queue_) + sdPending_;
    }

private:
    // A new pin takes over the current on/off state; the old one is switched off and released.
    void configureOutput(Core& core, int pin, bool activeLow) {
        if (pin == outputPin_ && activeLow == activeLow_) return;
        if (!outputPinAllowed(pin)) {
            core.log(LogLevel::Warn, "relay_pin " + String(pin) + " is used by the camera, SD or PIR; kept GPIO" + String(outputPin_));
            return;
        }
        if (outputPin_ >= 0 && pin != outputPin_) {
            digitalWrite(outputPin_, activeLow_ ? HIGH : LOW);
            pinMode(outputPin_, INPUT);
        }
        outputPin_ = pin;
        activeLow_ = activeLow;
        pinMode(outputPin_, OUTPUT);
        setOutput(outputOn_);
    }

    void setOutput(bool on) {
        outputOn_ = on;
        if (!on) outputTimer_.stop();
        if (outputPin_ >= 0) digitalWrite(outputPin_, on != activeLow_ ? HIGH : LOW);
    }

    AckResult relayCommand(JsonObjectConst p) {
        const char* ch = p["channel"] | "";
        if (channel_ != ch) return AckResult::error("unknown channel " + String(ch));
        const String set = p["set"] | "";
        if (set != "on" && set != "off") return AckResult::error("set must be on or off");
        setOutput(set == "on");
        if (outputOn_) outputTimer_.start(millis(), p["for_s"] | 0U, maxOnS_);
        publishOutput(*core_);
        AckResult r;
        r.state[channel_] = outputOn_ ? "on" : "off";
        return r;
    }

    void publishOutput(Core& core) {
        JsonDocument state;
        state[channel_] = outputOn_ ? "on" : "off";
        core.publishState(state);
        outputPublished_ = core.online();
    }

    camera_config_t& cameraConfigured() {
        static camera_config_t c = cameraConfig();
        if (!psramFound()) {
            c.frame_size = FRAMESIZE_VGA;
            c.fb_location = CAMERA_FB_IN_DRAM;
            c.fb_count = 1;
        }
        return c;
    }

    void onMotion(Core& core) {
        const bool shoot = cameraOk_ && (armed_ || recordWhenDisarmed_);
        JsonDocument event;
        event["source"] = "pir";
        event["armed"] = armed_;
        event["severity"] = armed_ ? "alarm" : "info";
        // The event id is stamped by the core; photo ids are derived from it, so they are listed up front.
        const String id = core.newUlid();
        JsonArray photos = event["photos"].to<JsonArray>();
        if (shoot)
            for (int i = 0; i < burst_; ++i) photos.add(id + "-" + i);
        event["photo_status"] = shoot ? (uploadUrl().length() ? "uploading" : "buffered") : "none";
        core.publishEvent("motion", event, id);
        if (shoot) beginBurst(id, "motion", burst_);
    }

    void startBurst(const char* type, int count) {
        JsonDocument event;
        const String id = core_->newUlid();
        JsonArray photos = event["photos"].to<JsonArray>();
        for (int i = 0; i < count; ++i) photos.add(id + "-" + i);
        event["severity"] = "info";
        core_->publishEvent(type, event, id);
        beginBurst(id, type, count);
    }

    void beginBurst(const String& eventId, const char* type, int count) {
        burstEvent_ = eventId;
        burstType_ = type;
        burstLeft_ = count;
        burstIndex_ = 0;
        nextFrameMs_ = millis();
    }

    static int64_t frameStartUs(const camera_fb_t* fb) {
        return static_cast<int64_t>(fb->timestamp.tv_sec) * 1000000 + fb->timestamp.tv_usec;
    }

    // With one buffer and CAMERA_GRAB_WHEN_EMPTY the driver fills the buffer as soon as the previous frame is
    // returned and then holds it: the first frame after a quiet spell was taken back then, possibly hours ago.
    // A frame started before the request is handed back and grabbed again, which waits for a new one.
    camera_fb_t* freshFrame(int64_t requestUs) {
        camera_fb_t* fb = nullptr;
        for (int i = 0; i < kFreshFrameTries; ++i) {
            if (fb) esp_camera_fb_return(fb);
            fb = esp_camera_fb_get();
            if (!fb || frameStartUs(fb) >= requestUs) break;
        }
        return fb;
    }

    // One frame per burst interval, without blocking the loop.
    void captureStep(Core& core) {
        if (burstLeft_ <= 0 || millis() < nextFrameMs_) return;
        nextFrameMs_ = millis() + burstIntervalMs_;
        --burstLeft_;
        const int64_t requestUs = esp_timer_get_time();
        camera_fb_t* fb = freshFrame(requestUs);
        if (!fb) {
            core.log(LogLevel::Warn, "camera frame failed");
            return;
        }
        // The photo carries the time the frame was taken, so one that could not be refreshed shows its real age.
        const int64_t ageMs = (esp_timer_get_time() - frameStartUs(fb)) / 1000;
        if (frameStartUs(fb) < requestUs)
            core.log(LogLevel::Warn, "camera gave no fresh frame, sent one taken " + String(static_cast<long>(ageMs / 1000)) + " s ago");
        Photo p = {};
        snprintf(p.photoId, sizeof p.photoId, "%s-%d", burstEvent_.c_str(), burstIndex_++);
        strlcpy(p.eventId, burstEvent_.c_str(), sizeof p.eventId);
        strlcpy(p.eventType, burstType_.c_str(), sizeof p.eventType);
        const uint64_t now = core.nowMs();  // 0 while the clock is not synced: the hive stamps the photo itself then
        p.ts = now != 0 && ageMs > 0 && static_cast<uint64_t>(ageMs) < now ? now - ageMs : now;
        p.len = fb->len;
        p.jpeg = static_cast<uint8_t*>(ps_malloc(fb->len));
        if (p.jpeg) memcpy(p.jpeg, fb->buf, fb->len);
        esp_camera_fb_return(fb);
        if (!p.jpeg || xQueueSend(queue_, &p, 0) != pdTRUE) {
            free(p.jpeg);
            core.log(LogLevel::Warn, "photo dropped: no memory or queue full");
        }
    }

    String uploadUrl() const { return uploadUrl_.length() ? uploadUrl_ : core_->settings().uploadUrl; }

    static void uploadTask(void* arg) {
        auto* self = static_cast<GuardCam*>(arg);
        Photo p;
        unsigned long nextSdFlush = 0;
        for (;;) {
            if (xQueueReceive(self->queue_, &p, pdMS_TO_TICKS(1000)) == pdTRUE) {
                self->deliver(p);
                free(p.jpeg);
            }
            if (self->sdOk_ && millis() >= nextSdFlush && self->core_->online()) {
                nextSdFlush = millis() + kSdRetryMs;
                self->flushSd();
            }
        }
    }

    // Card first, so a reboot cannot lose the photo; then the upload. Without a copy on the card the photo
    // gets a few attempts from PSRAM; with one, the 30 s SD retry takes over after the first.
    void deliver(const Photo& p) {
        const bool onSd = sdOk_ && saveToSd(p);
        const int attempts = onSd ? 1 : kUploadAttemptsWithoutSd;
        int code = 0;
        for (int i = 0; i < attempts; ++i) {
            if (i > 0) vTaskDelay(pdMS_TO_TICKS(uploadRetryPauseMs(i)));
            code = upload(p.photoId, p.eventId, p.eventType, p.ts, p.jpeg, p.len);
            if (uploadSettled(code)) break;
        }
        if (uploadSettled(code)) {
            if (code >= 400) note(LogLevel::Warn, "photo %s rejected by the hive (HTTP %d), not retried", p.photoId, code);
            if (onSd) archive(p.photoId);
            return;
        }
        if (onSd) {
            sdPending_ = sdPending_ + 1;
            note(LogLevel::Warn, "photo %s upload failed (%s), kept on SD, retried every %u s", p.photoId,
                 failure(code).c_str(), static_cast<unsigned>(kSdRetryMs / 1000));
        } else {
            note(LogLevel::Error, "photo %s lost: %d upload attempts failed (%s), %s", p.photoId, attempts,
                 failure(code).c_str(), sdOk_ ? "SD write failed" : "no SD card");
        }
    }

    // POST with the headers from contracts/http-ingest.md. Returns the HTTP status, a negative HTTPClient
    // error, or one of kNoUploadUrl / kNoWifi / kBadUploadUrl.
    int upload(const char* photoId, const char* eventId, const char* eventType, uint64_t ts, const uint8_t* jpeg, size_t len) {
        const String url = uploadUrl();
        if (!url.length()) return kNoUploadUrl;
        if (WiFi.status() != WL_CONNECTED) return kNoWifi;
        HTTPClient http;
        http.setTimeout(kUploadTimeoutMs);
        if (!http.begin(url + "/api/ingest/photo")) return kBadUploadUrl;
        http.addHeader("Content-Type", "image/jpeg");
        const String& token = core_->settings().uploadToken;
        if (token.length()) http.addHeader("Authorization", "Bearer " + token);
        http.addHeader("X-Device-Id", core_->deviceId());
        http.addHeader("X-Photo-Id", photoId);
        http.addHeader("X-Event-Id", eventId);
        http.addHeader("X-Event-Type", eventType);
        http.addHeader("X-Ts", String(ts));
        const int code = http.POST(const_cast<uint8_t*>(jpeg), len);
        http.end();
        if (!uploadSettled(code)) Serial.printf("[guard-cam] photo %s upload failed (%d)\n", photoId, code);
        return code;
    }

    static String failure(int code) {
        if (code == kNoUploadUrl) return "no upload url";
        if (code == kNoWifi) return "no wifi";
        if (code == kBadUploadUrl) return "bad upload url";
        if (code < 0) return HTTPClient::errorToString(code);
        return "HTTP " + String(code);
    }

    // Queues a journal line for loop(); Serial only when the queue is full.
    __attribute__((format(printf, 3, 4))) void note(LogLevel level, const char* fmt, ...) {
        Note n{level, {}};
        va_list args;
        va_start(args, fmt);
        vsnprintf(n.text, sizeof n.text, fmt, args);
        va_end(args);
        if (!notes_ || xQueueSend(notes_, &n, 0) != pdTRUE) Serial.printf("[guard-cam] %s\n", n.text);
    }

    void logSdStatus(Core& core) {
        if (!sdOk_) {
            core.log(LogLevel::Warn, "SD card not found or not mounted: photos are not archived, each gets " + String(kUploadAttemptsWithoutSd) + " upload attempts from memory");
            return;
        }
        const uint64_t total = SD_MMC.totalBytes();
        const uint64_t used = SD_MMC.usedBytes();
        char text[120];
        snprintf(text, sizeof text, "SD card: %llu of %llu MB free, %d photos waiting for upload",
                 static_cast<unsigned long long>((total - used) >> 20), static_cast<unsigned long long>(total >> 20),
                 static_cast<int>(sdPending_));
        core.log(LogLevel::Warn, text);
    }

    // At boot: removes writes cut short by a reset (.tmp, .meta without its photo) and counts what is pending.
    int tidyOutbox() {
        std::vector<String> junk;
        int pending = 0;
        File dir = SD_MMC.open(kOutboxDir);
        if (!dir) return 0;
        for (File f = dir.openNextFile(); f; f = dir.openNextFile()) {
            const String name = f.name();
            if (name.endsWith(".jpg")) ++pending;
            else if (name.endsWith(".tmp")) junk.push_back(name);
            else if (name.endsWith(".meta") && !SD_MMC.exists(String(kOutboxDir) + "/" + name.substring(0, name.length() - 5) + ".jpg"))
                junk.push_back(name);
        }
        dir.close();
        for (const String& name : junk) SD_MMC.remove(String(kOutboxDir) + "/" + name);
        return pending;
    }

    // Writes <id>.tmp + <id>.meta and renames the photo to <id>.jpg only when every byte made it.
    bool saveToSd(const Photo& p) {
        makeRoom(p.len + 1024);
        const String base = String(kOutboxDir) + "/" + p.photoId;
        size_t written = 0;
        File f = SD_MMC.open(base + ".tmp", FILE_WRITE);
        if (f) {
            written = f.write(p.jpeg, p.len);
            f.close();
        }
        bool metaOk = false;
        File meta = SD_MMC.open(base + ".meta", FILE_WRITE);
        if (meta) {
            metaOk = meta.printf("%s %s %llu", p.eventId, p.eventType, static_cast<unsigned long long>(p.ts)) > 0;
            meta.close();
        }
        if (written == p.len && metaOk && SD_MMC.rename(base + ".tmp", base + ".jpg")) return true;
        SD_MMC.remove(base + ".tmp");
        SD_MMC.remove(base + ".meta");
        const uint64_t total = SD_MMC.totalBytes();
        const uint64_t used = SD_MMC.usedBytes();
        note(LogLevel::Error, "SD write failed for photo %s (%u of %u bytes, %llu KB free)", p.photoId,
             static_cast<unsigned>(written), static_cast<unsigned>(p.len),
             static_cast<unsigned long long>(used < total ? (total - used) >> 10 : 0));
        return false;
    }

    // A delivered photo leaves /outbox for its archive folder; the .meta is not needed there.
    void archive(const char* photoId) {
        const String dir = String(kArchiveDir) + "/" + archiveBucket(photoId).c_str();
        if (!SD_MMC.exists(dir)) SD_MMC.mkdir(dir);
        const String from = String(kOutboxDir) + "/" + photoId + ".jpg";
        const String to = dir + "/" + photoId + ".jpg";
        SD_MMC.remove(String(kOutboxDir) + "/" + photoId + ".meta");
        if (SD_MMC.rename(from, to)) return;
        SD_MMC.remove(to);  // a copy left by an earlier attempt
        if (!SD_MMC.rename(from, to)) SD_MMC.remove(from);
    }

    // Keeps the reserve free: whole archive folders go first, oldest first; undelivered photos only after them.
    void makeRoom(size_t incoming) {
        const uint64_t total = SD_MMC.totalBytes();
        int archived = 0;
        int undelivered = 0;
        String firstBucket;
        for (int pass = 0; pass < 64 && sdNeedsRoom(total, SD_MMC.usedBytes(), incoming); ++pass) {
            String bucket;
            int removed = 0;
            if (dropOldestArchiveFolder(bucket, removed)) {
                if (!firstBucket.length()) firstBucket = bucket;
                archived += removed;
                continue;
            }
            const int dropped = dropOldestOutbox();
            if (dropped == 0) break;  // nothing left to delete
            undelivered += dropped;
        }
        if (archived > 0) {
            // Routine once the card is full, so said to the hive once per boot.
            note(archiveRotationLogged_ ? LogLevel::Info : LogLevel::Warn,
                 "SD card full: deleted %d oldest archived photos (from folder %s on)", archived, firstBucket.c_str());
            archiveRotationLogged_ = true;
        }
        if (undelivered > 0) {
            sdPending_ = sdPending_ > undelivered ? sdPending_ - undelivered : 0;
            note(LogLevel::Error, "SD card full and archive empty: dropped %d oldest photos that were never delivered", undelivered);
        }
    }

    // Deletes the oldest archive folder with everything in it; false when there is none.
    bool dropOldestArchiveFolder(String& bucket, int& removed) {
        std::vector<std::string> oldest;
        File root = SD_MMC.open(kArchiveDir);
        if (!root) return false;
        for (File d = root.openNextFile(); d; d = root.openNextFile())
            if (d.isDirectory()) keepOldest(oldest, d.name(), 1);
        root.close();
        if (oldest.empty()) return false;
        bucket = oldest[0].c_str();
        const String path = String(kArchiveDir) + "/" + bucket;
        std::vector<String> names;
        File dir = SD_MMC.open(path);
        if (dir) {
            for (File f = dir.openNextFile(); f; f = dir.openNextFile()) names.push_back(f.name());
            dir.close();
        }
        for (const String& name : names) SD_MMC.remove(path + "/" + name);
        removed = static_cast<int>(names.size());
        return SD_MMC.rmdir(path) || removed > 0;
    }

    int dropOldestOutbox() {
        int dropped = 0;
        for (const std::string& file : oldestPending()) {
            const String name = file.c_str();
            SD_MMC.remove(String(kOutboxDir) + "/" + name);
            SD_MMC.remove(String(kOutboxDir) + "/" + name.substring(0, name.length() - 4) + ".meta");
            ++dropped;
        }
        return dropped;
    }

    // The oldest kOutboxBatch undelivered photos (file names), oldest first; counts all of them on the way.
    std::vector<std::string> oldestPending(int* pending = nullptr) {
        std::vector<std::string> oldest;
        int count = 0;
        File dir = SD_MMC.open(kOutboxDir);
        if (!dir) return oldest;
        for (File f = dir.openNextFile(); f; f = dir.openNextFile()) {
            const String name = f.name();
            if (!name.endsWith(".jpg")) continue;
            ++count;
            keepOldest(oldest, name.c_str(), kOutboxBatch);
        }
        dir.close();
        if (pending) *pending = count;
        return oldest;
    }

    // Retries undelivered photos on the SD card, the oldest few per pass.
    void flushSd() {
        int pending = 0;
        const std::vector<std::string> batch = oldestPending(&pending);
        for (const std::string& file : batch) {
            const String name = file.c_str();
            const String photoId = name.substring(0, name.length() - 4);
            File f = SD_MMC.open(String(kOutboxDir) + "/" + name);
            if (!f) continue;
            const size_t len = f.size();
            uint8_t* jpeg = static_cast<uint8_t*>(ps_malloc(len));
            if (!jpeg) break;
            const size_t got = f.read(jpeg, len);
            f.close();
            char eventId[28] = "", eventType[16] = "motion";
            unsigned long long ts = 0;
            File meta = SD_MMC.open(String(kOutboxDir) + "/" + photoId + ".meta");
            if (meta) {
                sscanf(meta.readString().c_str(), "%27s %15s %llu", eventId, eventType, &ts);
                meta.close();
            }
            // A photo the card cannot read back is treated like one the hive rejected: it would never get through.
            const int code = got == len ? upload(photoId.c_str(), eventId, eventType, ts, jpeg, len) : 400;
            free(jpeg);
            if (!uploadSettled(code)) break;  // still offline: stop for now
            if (code >= 400) note(LogLevel::Warn, "photo %s from SD not delivered (%s), moved to the archive", photoId.c_str(), got == len ? failure(code).c_str() : "unreadable");
            archive(photoId.c_str());
            --pending;
        }
        sdPending_ = pending;
    }

    Core* core_ = nullptr;
    QueueHandle_t queue_ = nullptr;
    QueueHandle_t notes_ = nullptr;
    bool cameraOk_ = false;
    esp_err_t cameraErr_ = ESP_OK;
    bool cameraErrLogged_ = false;
    bool sdOk_ = false;
    bool sdLogged_ = false;
    bool archiveRotationLogged_ = false;
    bool armed_ = true;
    bool recordWhenDisarmed_ = false;
    bool lastPir_ = false;
    Cooldown cooldown_;
    unsigned long cooldownMs_ = 20000;
    int burst_ = 3;
    unsigned long burstIntervalMs_ = 700;
    String uploadUrl_;
    String burstEvent_;
    String burstType_;
    int burstLeft_ = 0;
    int burstIndex_ = 0;
    unsigned long nextFrameMs_ = 0;
    volatile int sdPending_ = 0;
    String channel_ = "relay";
    int outputPin_ = -1;
    bool activeLow_ = false;
    bool outputOn_ = false;
    bool outputPublished_ = false;
    uint32_t maxOnS_ = 0;
    RelayTimer outputTimer_;
};

GuardCam role;

}  // namespace

hornet::Role& hornetRole() { return role; }
