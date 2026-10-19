// Role guard-cam (spec 5.4) on ESP32-CAM AI-Thinker:
//   PIR rising edge -> cooldown -> burst of N frames into PSRAM -> event/motion with photo ids
//   -> each frame POSTed to {upload}/api/ingest/photo; on failure kept on SD /outbox and retried.
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
#include <RelayTimer.h>
#include <SD_MMC.h>
#include <WiFi.h>
#include <esp_camera.h>
#include <esp_timer.h>

namespace {

using namespace hornet;

constexpr int kPirPin = 13;
constexpr const char* kOutboxDir = "/outbox";
constexpr uint32_t kSdRetryMs = 30000;
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
        sdOk_ = SD_MMC.begin("/sdcard", true) && (SD_MMC.exists(kOutboxDir) || SD_MMC.mkdir(kOutboxDir));
        if (!sdOk_) Serial.println("[guard-cam] no SD card: photos that fail to upload are lost");
        setOutput(false);  // again after the SD driver set its pins up: the output starts off whatever the pin

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
    esp_err_t cameraErr_ = ESP_OK;
    bool cameraErrLogged_ = false;
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
