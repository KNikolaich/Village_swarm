// Role car: an ESP32-CAM on two motors behind an L298N (contracts/mqtt-topics.md "Car").
//   vs/v1/hive/drive/{id} {left, right}  -> wheel speeds, -100..100; no ack, repeated by the hive while a control is held
//   cmd/relay {channel: light, set, for_s} -> the white LED (hw.relay_pin, GPIO4), off at boot
//   MJPEG on port 81: /stream and /jpg, Basic login hive / car.stream_key, opened by the hive's go2rtc only
// The motors stop by themselves (CarDrive.h): 500 ms after the last drive, at once when Wi-Fi or MQTT drops, at boot, and
// after car.max_run_s of moving without a pause. Pins: hw.motor_pins [IN1, IN2, IN3, IN4] (14, 15, 13, 12), only the free
// pins of the ESP32-CAM (2, 4, 12, 13, 14, 15); GPIO12 must be low at power-up, the L298N inputs keep it so.
// No SD card on a car: its pins drive the motors.

#include <CarDrive.h>
#include <HornetCore.h>
#include <RelayTimer.h>
#include <WiFi.h>
#include <driver/gpio.h>
#include <esp_camera.h>
#include <esp_http_server.h>
#include <mbedtls/base64.h>

namespace {

using namespace hornet;

constexpr int kDefaultMotorPins[4] = {14, 15, 13, 12};
constexpr int kFreePins[6] = {2, 4, 12, 13, 14, 15};
// LEDC channels 2..5 (timers 1 and 2): the camera's clock has channel 0 on timer 0.
constexpr int kPwmChannel0 = 2;
constexpr uint32_t kPwmHz = 2000;
constexpr uint8_t kPwmBits = 8;
constexpr uint16_t kStreamPort = 81;

// Every free pin low before anything else runs: the motors must not move while the hornet starts.
void allFreePinsLow() {
    for (int pin : kFreePins) {
        gpio_reset_pin(static_cast<gpio_num_t>(pin));
        gpio_set_direction(static_cast<gpio_num_t>(pin), GPIO_MODE_OUTPUT);
        gpio_set_level(static_cast<gpio_num_t>(pin), 0);
    }
}

// AI-Thinker camera pins (as guard-cam), streaming: two buffers, the latest frame.
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
    c.xclk_freq_hz = 10000000;
    c.ledc_timer = LEDC_TIMER_0;
    c.ledc_channel = LEDC_CHANNEL_0;
    c.pixel_format = PIXFORMAT_JPEG;
    c.frame_size = FRAMESIZE_VGA;
    c.jpeg_quality = 14;
    c.fb_count = psramFound() ? 2 : 1;
    c.fb_location = psramFound() ? CAMERA_FB_IN_PSRAM : CAMERA_FB_IN_DRAM;
    c.grab_mode = CAMERA_GRAB_LATEST;
    return c;
}

framesize_t frameSize(const char* name) {
    const String n = name ? name : "";
    if (n == "QVGA") return FRAMESIZE_QVGA;
    if (n == "SVGA") return FRAMESIZE_SVGA;
    if (n == "XGA") return FRAMESIZE_XGA;
    if (n == "HD") return FRAMESIZE_HD;
    return FRAMESIZE_VGA;  // a car streams: small and quick
}

class Car : public Role {
public:
    Car() { allFreePinsLow(); }

    const char* type() const override { return "car"; }
    const char* hw() const override { return HORNET_BOARD; }
    std::vector<const char*> caps() const override { return {"drive", "camera", "relay"}; }
    bool wantsDrive() const override { return true; }

    void setup(Core& core) override {
        core_ = &core;
        configurePins(kDefaultMotorPins, HW_RELAY_PIN, true);
        camera_config_t camera = cameraConfig();  // here, not at start-up: PSRAM is known only once the core runs
        cameraOk_ = esp_camera_init(&camera) == ESP_OK;
        if (!cameraOk_) Serial.println("[car] camera init failed");
        startStream();
        core.onCommand("relay", [this](JsonObjectConst p) { return lightCommand(p); });
    }

    void applyConfig(Core& core, JsonObjectConst config) override {
        JsonObjectConst hw = config["hw"];
        int pins[4] = {kDefaultMotorPins[0], kDefaultMotorPins[1], kDefaultMotorPins[2], kDefaultMotorPins[3]};
        JsonArrayConst given = hw["motor_pins"];
        if (given.size() == 4)
            for (int i = 0; i < 4; ++i) pins[i] = given[i] | -1;
        const int light = hw["relay_pin"] | HW_RELAY_PIN;
        if (carPinsValid(pins, light)) {
            configurePins(pins, light, hw["motor_pwm"] | true);
        } else {
            core.log(LogLevel::Warn, "motor_pins or relay_pin not allowed (free pins: 2, 4, 12, 13, 14, 15, all different); kept the old ones");
            mount_.pwm = hw["motor_pwm"] | true;
        }
        JsonObjectConst car = config["car"];
        mount_.invertLeft = car["invert_left"] | false;
        mount_.invertRight = car["invert_right"] | false;
        mount_.swapSides = car["swap_sides"] | false;
        mount_.maxSpeed = car["max_speed"] | 100;
        guard_.setMaxRunMs((car["max_run_s"] | 60U) * 1000UL);
        streamKey_ = car["stream_key"] | "";
        updateAuth();
        maxLightS_ = config["relay"]["max_on_s"] | 600U;
        JsonObjectConst camera = config["camera"];
        if (cameraOk_ && !camera.isNull()) {
            sensor_t* s = esp_camera_sensor_get();
            if (s) {
                s->set_framesize(s, frameSize(camera["frame"]));
                s->set_quality(s, camera["quality"] | 14);
            }
        }
        writeMotors();  // the new mounting, and the car stays as it is (stopped unless driven)
        publishState(core);
    }

    void applyDrive(Core& core, JsonObjectConst message) override {
        const bool wasMoving = guard_.moving();
        if (!guard_.drive(message["left"] | 0, message["right"] | 0, millis(), message["ts"] | 0ULL, core.nowMs())) return;
        writeMotors();
        if (guard_.moving() != wasMoving) publishState(core);
    }

    void loop(Core& core) override {
        const CarStop stopped = guard_.tick(millis(), core.online() && WiFi.status() == WL_CONNECTED);
        if (stopped != CarStop::None) {
            writeMotors();
            if (stopped == CarStop::Link || stopped == CarStop::MaxRun)
                core.log(LogLevel::Info, String("car stopped: ") + carStopName(stopped));
            publishState(core);
        }
        if (light_ && lightTimer_.expired(millis())) {
            setLight(false);
            publishState(core);
        }
        if (!statePublished_ && core.online()) publishState(core);
    }

    void fillHealth(JsonObject health) override { health["stopped_by"] = carStopName(guard_.lastStop()); }

private:
    // Motors stopped on the old pins, then the new ones set up (PWM on LEDC channels 2..5, or plain outputs).
    void configurePins(const int pins[4], int light, bool pwm) {
        bool same = pwm == mount_.pwm && light == lightPin_;
        for (int i = 0; i < 4; ++i) same = same && pins[i] == pins_[i];
        if (same && pinsReady_) return;
        for (int i = 0; i < 4 && pinsReady_; ++i) {
            if (mount_.pwm) ledcDetach(pins_[i]);
            pinMode(pins_[i], OUTPUT);
            digitalWrite(pins_[i], LOW);
        }
        mount_.pwm = pwm;
        for (int i = 0; i < 4; ++i) {
            pins_[i] = pins[i];
            pinMode(pins_[i], OUTPUT);
            digitalWrite(pins_[i], LOW);
            if (pwm && !ledcAttachChannel(pins_[i], kPwmHz, kPwmBits, kPwmChannel0 + i)) {
                core_->log(LogLevel::Warn, "PWM not available on GPIO" + String(pins_[i]) + ": full speed only");
                mount_.pwm = false;
            }
        }
        if (light != lightPin_) {
            if (lightPin_ >= 0) digitalWrite(lightPin_, LOW);
            lightPin_ = light;
            pinMode(lightPin_, OUTPUT);
        }
        pinsReady_ = true;
        writeMotors();
        setLight(light_);
    }

    void writeMotors() {
        if (!pinsReady_) return;
        const MotorDuty d = carDuty(guard_.left(), guard_.right(), mount_);
        for (int i = 0; i < 4; ++i) {
            if (mount_.pwm) ledcWrite(pins_[i], d.in[i]);
            else digitalWrite(pins_[i], d.in[i] > 0 ? HIGH : LOW);
        }
    }

    AckResult lightCommand(JsonObjectConst p) {
        const char* ch = p["channel"] | "";
        if (String(ch) != "light") return AckResult::error("unknown channel " + String(ch));
        const String set = p["set"] | "";
        if (set != "on" && set != "off") return AckResult::error("set must be on or off");
        setLight(set == "on");
        if (light_) lightTimer_.start(millis(), p["for_s"] | 0U, maxLightS_);
        publishState(*core_);
        AckResult r;
        r.state["light"] = light_ ? "on" : "off";
        return r;
    }

    void setLight(bool on) {
        light_ = on;
        if (!on) lightTimer_.stop();
        if (lightPin_ >= 0) digitalWrite(lightPin_, on ? HIGH : LOW);
    }

    void publishState(Core& core) {
        JsonDocument state;
        state["drive"] = guard_.moving() ? "moving" : "stopped";
        state["light"] = light_ ? "on" : "off";
        core.publishState(state);
        statePublished_ = core.online();
    }

    // The value of the Authorization header the stream accepts: Basic base64(hive:key); empty without a key (closed).
    void updateAuth() {
        auth_ = "";
        if (!streamKey_.length()) return;
        const String plain = "hive:" + streamKey_;
        unsigned char out[128];
        size_t len = 0;
        if (mbedtls_base64_encode(out, sizeof out - 1, &len, reinterpret_cast<const unsigned char*>(plain.c_str()), plain.length()) == 0) {
            out[len] = 0;
            auth_ = "Basic " + String(reinterpret_cast<const char*>(out));
        }
    }

    static bool authorized(httpd_req_t* req) {
        auto* self = static_cast<Car*>(req->user_ctx);
        if (!self->auth_.length()) return false;
        char header[160];
        if (httpd_req_get_hdr_value_str(req, "Authorization", header, sizeof header) != ESP_OK) return false;
        return self->auth_ == header;
    }

    static esp_err_t refuse(httpd_req_t* req) {
        httpd_resp_set_status(req, "401 Unauthorized");
        httpd_resp_set_hdr(req, "WWW-Authenticate", "Basic realm=\"car\"");
        return httpd_resp_send(req, nullptr, 0);
    }

    static esp_err_t jpg(httpd_req_t* req) {
        if (!authorized(req)) return refuse(req);
        camera_fb_t* fb = esp_camera_fb_get();
        if (!fb) return httpd_resp_send_500(req);
        httpd_resp_set_type(req, "image/jpeg");
        const esp_err_t r = httpd_resp_send(req, reinterpret_cast<const char*>(fb->buf), fb->len);
        esp_camera_fb_return(fb);
        return r;
    }

    // multipart/x-mixed-replace until the viewer (go2rtc) goes away.
    static esp_err_t stream(httpd_req_t* req) {
        if (!authorized(req)) return refuse(req);
        httpd_resp_set_type(req, "multipart/x-mixed-replace;boundary=frame");
        char head[96];
        for (;;) {
            camera_fb_t* fb = esp_camera_fb_get();
            if (!fb) {
                vTaskDelay(pdMS_TO_TICKS(20));
                continue;
            }
            const int n = snprintf(head, sizeof head, "\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: %u\r\n\r\n", static_cast<unsigned>(fb->len));
            esp_err_t r = httpd_resp_send_chunk(req, head, n);
            if (r == ESP_OK) r = httpd_resp_send_chunk(req, reinterpret_cast<const char*>(fb->buf), fb->len);
            esp_camera_fb_return(fb);
            if (r != ESP_OK) break;
        }
        return ESP_OK;
    }

    void startStream() {
        if (!cameraOk_) return;
        httpd_config_t c = HTTPD_DEFAULT_CONFIG();
        c.server_port = kStreamPort;
        c.ctrl_port = kStreamPort + 1;
        c.stack_size = 6144;
        if (httpd_start(&server_, &c) != ESP_OK) {
            Serial.println("[car] stream server not started");
            return;
        }
        httpd_uri_t s = {};
        s.uri = "/stream";
        s.method = HTTP_GET;
        s.handler = stream;
        s.user_ctx = this;
        httpd_register_uri_handler(server_, &s);
        httpd_uri_t j = {};
        j.uri = "/jpg";
        j.method = HTTP_GET;
        j.handler = jpg;
        j.user_ctx = this;
        httpd_register_uri_handler(server_, &j);
    }

    Core* core_ = nullptr;
    bool cameraOk_ = false;
    httpd_handle_t server_ = nullptr;
    String streamKey_;
    String auth_;
    CarGuard guard_;
    CarMount mount_;
    int pins_[4] = {-1, -1, -1, -1};
    bool pinsReady_ = false;
    int lightPin_ = -1;
    bool light_ = false;
    uint32_t maxLightS_ = 600;
    RelayTimer lightTimer_;
    bool statePublished_ = false;
};

Car role;

}  // namespace

hornet::Role& hornetRole() { return role; }
