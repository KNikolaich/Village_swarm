// Role guard-cam (spec 5.4) on ESP32-CAM AI-Thinker:
//   PIR rising edge -> cooldown -> burst of N frames into PSRAM -> event/motion with photo ids
//   -> each frame POSTed to {upload}/api/ingest/photo; on failure kept on SD /outbox and retried.
// Uploads run in their own task so the main loop (MQTT, watchdog) never waits on HTTP.
//
// Pins: PIR on GPIO13 (spec). The SD card runs in 1-bit mode so GPIO4/12/13 stay free
// (GPIO4 is also the white flash LED, which 1-bit mode leaves alone).

#include <HTTPClient.h>
#include <HornetCore.h>
#include <Outbox.h>
#include <SD_MMC.h>
#include <WiFi.h>
#include <esp_camera.h>

namespace {

using namespace hornet;

constexpr int kPirPin = 13;
constexpr const char* kOutboxDir = "/outbox";
constexpr uint32_t kSdRetryMs = 30000;

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
    c.xclk_freq_hz = 20000000;
    c.ledc_timer = LEDC_TIMER_0;
    c.ledc_channel = LEDC_CHANNEL_0;
    c.pixel_format = PIXFORMAT_JPEG;
    c.frame_size = FRAMESIZE_SVGA;
    c.jpeg_quality = 12;
    c.fb_count = 2;
    c.fb_location = CAMERA_FB_IN_PSRAM;
    c.grab_mode = CAMERA_GRAB_LATEST;
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

class GuardCam : public Role {
public:
    const char* type() const override { return "guard-cam"; }
    const char* hw() const override { return HORNET_BOARD; }
    std::vector<const char*> caps() const override { return {"camera", "motion.pir", "sd"}; }

    void setup(Core& core) override {
        core_ = &core;
        pinMode(kPirPin, INPUT_PULLDOWN);

        cameraOk_ = esp_camera_init(&cameraConfigured()) == ESP_OK;
        if (!cameraOk_) Serial.println("[guard-cam] camera init failed");
        sdOk_ = SD_MMC.begin("/sdcard", true) && (SD_MMC.exists(kOutboxDir) || SD_MMC.mkdir(kOutboxDir));
        if (!sdOk_) Serial.println("[guard-cam] no SD card: photos that fail to upload are lost");

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
            if (!cameraOk_) return AckResult::error("camera not available");
            startBurst("snapshot", constrain(p["count"] | 1, 1, 10));
            return AckResult::ok();
        });
    }

    void applyConfig(Core&, JsonObjectConst config) override {
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
        const bool motion = digitalRead(kPirPin) == HIGH;
        if (motion && !lastPir_ && cooldown_.tryFire(millis(), cooldownMs_)) onMotion(core);
        lastPir_ = motion;
        captureStep(core);
    }

    void fillHealth(JsonObject health) override {
        if (queue_) health["outbox"] = (health["outbox"] | 0) + uxQueueMessagesWaiting(queue_) + sdPending_;
    }

private:
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

    // One frame per burst interval, without blocking the loop.
    void captureStep(Core& core) {
        if (burstLeft_ <= 0 || millis() < nextFrameMs_) return;
        nextFrameMs_ = millis() + burstIntervalMs_;
        --burstLeft_;
        camera_fb_t* fb = esp_camera_fb_get();
        if (!fb) {
            core.log(LogLevel::Warn, "camera frame failed");
            return;
        }
        Photo p = {};
        snprintf(p.photoId, sizeof p.photoId, "%s-%d", burstEvent_.c_str(), burstIndex_++);
        strlcpy(p.eventId, burstEvent_.c_str(), sizeof p.eventId);
        strlcpy(p.eventType, burstType_.c_str(), sizeof p.eventType);
        p.ts = core.nowMs();
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
                if (!self->upload(p.photoId, p.eventId, p.eventType, p.ts, p.jpeg, p.len)) self->saveToSd(p);
                free(p.jpeg);
            }
            if (self->sdOk_ && millis() >= nextSdFlush && self->core_->online()) {
                nextSdFlush = millis() + kSdRetryMs;
                self->flushSd();
            }
        }
    }

    // POST with the headers from contracts/http-ingest.md; 2xx done, 4xx dropped, else keep for retry.
    bool upload(const char* photoId, const char* eventId, const char* eventType, uint64_t ts, const uint8_t* jpeg, size_t len) {
        const String url = uploadUrl();
        if (!url.length() || WiFi.status() != WL_CONNECTED) return false;
        HTTPClient http;
        http.setTimeout(5000);
        if (!http.begin(url + "/api/ingest/photo")) return false;
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
        if (code >= 200 && code < 300) return true;
        if (code >= 400 && code < 500 && code != 401 && code != 408 && code != 429) {
            Serial.printf("[guard-cam] photo %s rejected (%d), dropped\n", photoId, code);
            return true;  // retrying will not help
        }
        Serial.printf("[guard-cam] photo %s upload failed (%d)\n", photoId, code);
        return false;
    }

    void saveToSd(const Photo& p) {
        if (!sdOk_) return;
        const String base = String(kOutboxDir) + "/" + p.photoId;
        File f = SD_MMC.open(base + ".jpg", FILE_WRITE);
        if (!f) return;
        f.write(p.jpeg, p.len);
        f.close();
        File meta = SD_MMC.open(base + ".meta", FILE_WRITE);
        meta.printf("%s %s %llu", p.eventId, p.eventType, static_cast<unsigned long long>(p.ts));
        meta.close();
        ++sdPending_;
    }

    // Retries photos stored on the SD card, a few per pass.
    void flushSd() {
        File dir = SD_MMC.open(kOutboxDir);
        if (!dir) return;
        int sent = 0;
        int pending = 0;
        for (File f = dir.openNextFile(); f; f = dir.openNextFile()) {
            String name = f.name();
            if (!name.endsWith(".jpg")) continue;
            ++pending;
            if (sent >= 5) continue;
            const String photoId = name.substring(0, name.length() - 4);
            const size_t len = f.size();
            uint8_t* jpeg = static_cast<uint8_t*>(ps_malloc(len));
            if (!jpeg) continue;
            f.read(jpeg, len);
            f.close();
            char eventId[28] = "", eventType[16] = "motion";
            unsigned long long ts = 0;
            File meta = SD_MMC.open(String(kOutboxDir) + "/" + photoId + ".meta");
            if (meta) {
                sscanf(meta.readString().c_str(), "%27s %15s %llu", eventId, eventType, &ts);
                meta.close();
            }
            const bool ok = upload(photoId.c_str(), eventId, eventType, ts, jpeg, len);
            free(jpeg);
            if (!ok) break;  // still offline: stop for now
            SD_MMC.remove(String(kOutboxDir) + "/" + name);
            SD_MMC.remove(String(kOutboxDir) + "/" + photoId + ".meta");
            ++sent;
            --pending;
        }
        sdPending_ = pending;
    }

    Core* core_ = nullptr;
    QueueHandle_t queue_ = nullptr;
    bool cameraOk_ = false;
    bool sdOk_ = false;
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
};

GuardCam role;

}  // namespace

hornet::Role& hornetRole() { return role; }
