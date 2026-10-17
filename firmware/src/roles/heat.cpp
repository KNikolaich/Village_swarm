// Role heat: the universal ESP8266 hornet. Config "features" says what this one is used for, and only those pins are
// touched (on the ESP-01 the relay, 1-Wire and I2C share GPIO0/GPIO2):
//   thermometer  DS18B20s on hw.onewire_pin          -> temperature, temperature_2...
//   bme280       BME280 on hw.sda_pin/hw.scl_pin      -> temperature, humidity, pressure (BME280 first in the names)
//   relay        relay on hw.relay_pin (+ hw.button_pin): cmd/relay {channel, set, for_s}, retained state/{channel}
//   heater       the relay heats: the hornet keeps failsafe.heater itself (spec 5.6, HeaterFailsafe.h) using the
//                first temperature it reads; commands that break it are acked "rejected", and every switch the
//                failsafe makes goes out as event/failsafe {rule, heater, temperature}, as the simulator does
// Without "features" in the config: thermometer + relay + heater. The relay is off at every boot and switches off by
// itself after for_s or max_on_s (failsafe.heater.max_on_s for a heater, else relay.max_on_s), whichever is shorter.
// With hw.vbat_pin set (or HW_VBAT_VCC on the ESP-01) the supply voltage is published as battery_voltage (V).

#include <Adafruit_BME280.h>
#include <Battery.h>
#include <DallasTemperature.h>
#include <Features.h>
#include <HeaterFailsafe.h>
#include <HornetCore.h>
#include <OneWire.h>
#include <RelayTimer.h>
#include <Wire.h>

namespace {

using namespace hornet;

constexpr uint32_t kConversionMs = 800;      // DS18B20 at 12 bits
constexpr uint32_t kHeaterReadMs = 30000;    // a heater reads at least this often, whatever tele_interval_s says
constexpr uint32_t kMinLostAfterMs = 180000; // no temperature for this long (or 3 readings) = sensor lost

class Heat : public Role {
public:
    const char* type() const override { return "heat"; }
    const char* hw() const override { return HORNET_BOARD; }

    std::vector<const char*> caps() const override {
        std::vector<const char*> c;
        if (dsCount_ > 0) c.push_back("sensor.ds18b20");
        if (bmeOk_) c.push_back("sensor.bme280");
        if (features_.has(kRelay)) c.push_back("relay");
        if (features_.has(kRelay) && buttonPin_ >= 0) c.push_back("button");
        if (features_.has(kHeater)) c.push_back("heater.failsafe");
        return c;
    }

    std::vector<const char*> metrics() const override {
        std::vector<const char*> m;
        for (const auto& n : names_) m.push_back(n.c_str());
        if (bmeOk_) {
            m.push_back("humidity");
            m.push_back("pressure");
        }
        if (battery_.enabled()) m.push_back("battery_voltage");
        return m;
    }

    void setup(Core& core) override {
        core_ = &core;
        core.onCommand("relay", [this](JsonObjectConst p) { return command(p); });
        configure(Features{kThermometer | kRelay | kHeater}.normalized(), HW_ONEWIRE_PIN, HW_SDA_PIN, HW_SCL_PIN,
                  HW_RELAY_PIN, HW_RELAY_ACTIVE_LOW != 0, HW_BUTTON_PIN);
    }

    void applyConfig(Core& core, JsonObjectConst config) override {
        teleIntervalMs_ = (config["tele_interval_s"] | 60U) * 1000UL;
        JsonObjectConst hw = config["hw"];
        battery_.configure(hw);

        Features features{kThermometer | kRelay | kHeater};
        if (config["features"].is<JsonArrayConst>()) {
            features.bits = 0;
            for (JsonVariantConst f : config["features"].as<JsonArrayConst>()) features.bits |= Features::parseOne(f.as<const char*>());
        }
        features = features.normalized();

        JsonObjectConst heater = config["failsafe"]["heater"];
        failsafe_.configure(heater["min_c"] | 5.0F, heater["max_c"] | 25.0F, heater["max_on_s"] | 21600U, lostAfterMs());

        JsonObjectConst relay = config["relay"];
        const char* ch = relay["channel"] | (features.has(kHeater) ? "heater" : "relay");
        if (topics::isValidSegment(ch)) channel_ = ch;
        maxOnS_ = relay["max_on_s"] | 0U;

        const bool changed = configure(features, hw["onewire_pin"] | HW_ONEWIRE_PIN, hw["sda_pin"] | HW_SDA_PIN, hw["scl_pin"] | HW_SCL_PIN,
                                       hw["relay_pin"] | HW_RELAY_PIN, hw["relay_active_low"] | (HW_RELAY_ACTIVE_LOW != 0),
                                       hw["button_pin"] | HW_BUTTON_PIN);
        if (changed) core.refreshInfo();
        if (features_.has(kRelay)) publishState(core);
    }

    void loop(Core& core) override {
        const uint32_t now = millis();
        // A thermometer-only hornet on a battery: one reading per wake-up, as the meteo role does.
        const bool sleepy = core.sleepy() && !features_.has(kRelay);
        if (!sleepy || (core.online() && !sent_)) readSensors(core, now, sleepy);

        if (features_.has(kRelay)) {
            readButton();
            if (features_.has(kHeater)) {
                if (failsafe_.evaluate(now)) reportFailsafe(core);
            } else if (commanded_ && timer_.expired(now)) {
                commanded_ = false;
                core.log(LogLevel::Info, channel_ + " off: time limit");
            }
            const bool want = features_.has(kHeater) ? failsafe_.isOn() : commanded_;
            if (want != on_) {
                write(want);
                publishState(core);
            }
            if (!statePublished_ && core.online()) publishState(core);
        }
        if (battery_.enabled() && features_.has(kRelay) && core.online() && (!batterySent_ || now - lastBatteryMs_ >= teleIntervalMs_)) {
            batterySent_ = true;
            lastBatteryMs_ = now;
            core.publishTele("battery_voltage", battery_.read(), "V");
        }
    }

private:
    uint32_t readIntervalMs(Core& core) const {
        const uint32_t tele = core.readingIntervalMs(teleIntervalMs_);
        return features_.has(kHeater) && tele > kHeaterReadMs ? kHeaterReadMs : tele;
    }

    uint32_t lostAfterMs() const {
        const uint32_t every = teleIntervalMs_ < kHeaterReadMs ? teleIntervalMs_ : kHeaterReadMs;
        return 3 * every > kMinLostAfterMs ? 3 * every : kMinLostAfterMs;
    }

    // Returns true when what the hornet does changed (info is sent again).
    bool configure(Features features, int onewire, int sda, int scl, int relayPin, bool activeLow, int buttonPin) {
        const bool changed = features.bits != features_.bits;
        const bool sensorsChanged = changed || onewire != onewirePin_ || sda != sdaPin_ || scl != sclPin_;
        if (changed && features_.has(kRelay) && !features.has(kRelay)) {
            commanded_ = false;
            write(false);  // the relay is no longer used: leave it off
        }
        features_ = features;
        onewirePin_ = onewire;
        sdaPin_ = sda;
        sclPin_ = scl;
        if (features_.has(kRelay) && (relayPin != relayPin_ || activeLow != activeLow_)) {
            relayPin_ = relayPin;
            activeLow_ = activeLow;
            if (relayPin_ >= 0) pinMode(relayPin_, OUTPUT);
            write(on_);
        }
        if (features_.has(kRelay) && buttonPin != buttonPin_) {
            buttonPin_ = buttonPin;
            if (buttonPin_ >= 0) pinMode(buttonPin_, INPUT_PULLUP);
        }
        if (sensorsChanged) initSensors();
        return changed;
    }

    void initSensors() {
        dsCount_ = 0;
        bmeOk_ = false;
        if (features_.has(kThermometer) && onewirePin_ >= 0 && onewirePin_ != relayPin_) {
            oneWire_.begin(onewirePin_);
            dallas_.setOneWire(&oneWire_);
            dallas_.begin();
            dallas_.setWaitForConversion(false);
            dsCount_ = dallas_.getDeviceCount();
        }
        if (features_.has(kBme280) && sdaPin_ >= 0 && sclPin_ >= 0) {
            Wire.begin(sdaPin_, sclPin_);
            bmeOk_ = bme_.begin(0x76, &Wire) || bme_.begin(0x77, &Wire);
        }
        names_.clear();
        if (bmeOk_) names_.push_back("temperature");
        for (uint8_t i = 0; i < dsCount_; ++i)
            names_.push_back(names_.empty() ? String("temperature") : "temperature_" + String(names_.size() + 1));
        warned_ = false;
        Serial.printf("[heat] features=%u ds18b20=%u bme280=%s relay=%d (1-wire %d, i2c %d/%d)\n", features_.bits, dsCount_,
                      bmeOk_ ? "yes" : "no", features_.has(kRelay) ? relayPin_ : -1, onewirePin_, sdaPin_, sclPin_);
    }

    void readSensors(Core& core, uint32_t now, bool sleepy) {
        if (!converting_ && (!started_ || now - lastReadMs_ >= readIntervalMs(core))) {
            started_ = true;  // first reading right after boot
            lastReadMs_ = now;
            if (dsCount_ > 0) dallas_.requestTemperatures();  // non-blocking, read after kConversionMs
            converting_ = true;
            conversionStartMs_ = now;
        }
        if (!converting_ || now - conversionStartMs_ < (dsCount_ > 0 ? kConversionMs : 0)) return;
        converting_ = false;
        const bool publish = !published_ || now - lastPublishMs_ >= core.readingIntervalMs(teleIntervalMs_) - 1000;
        if (publish) {
            published_ = true;
            lastPublishMs_ = now;
        }
        read(core, now, publish);
        if (sleepy) {
            sent_ = true;
            core.readingDone();
        }
    }

    void read(Core& core, uint32_t now, bool publish) {
        if (publish && battery_.enabled() && !features_.has(kRelay)) core.publishTele("battery_voltage", battery_.read(), "V");
        if (!bmeOk_ && dsCount_ == 0) {
            if (!warned_ && (features_.has(kThermometer) || features_.has(kBme280)))
                core.log(LogLevel::Warn, "no sensor found (DS18B20 or BME280)");
            warned_ = true;
            return;
        }
        float control = NAN;  // the heater follows the first temperature read
        size_t name = 0;
        if (bmeOk_) {
            const float t = bme_.readTemperature();
            const float h = bme_.readHumidity();
            const float p = bme_.readPressure() / 100.0F;
            if (!isnan(t)) control = t;
            if (publish && !isnan(t)) core.publishTele("temperature", t, "C");
            if (publish && !isnan(h)) core.publishTele("humidity", h, "%");
            if (publish && !isnan(p) && p > 300) core.publishTele("pressure", p, "hPa");
            name = 1;
        }
        for (uint8_t i = 0; i < dsCount_; ++i, ++name) {
            const float t = dallas_.getTempCByIndex(i);
            if (t == DEVICE_DISCONNECTED_C || t == 85.0F) {  // 85 is the power-on value: conversion did not happen
                if (publish) core.log(LogLevel::Warn, "DS18B20 #" + String(i) + " read failed");
                continue;
            }
            if (isnan(control)) control = t;
            if (publish) core.publishTele(names_[name].c_str(), t, "C");
        }
        if (features_.has(kHeater)) failsafe_.reading(now, control);
    }

    // A switch the failsafe made by itself (not "idle", the end of a for_s): event/failsafe, as in the simulator.
    void reportFailsafe(Core& core) {
        const char* rule = failsafe_.lastReason();
        if (rule == nullptr || strcmp(rule, "idle") == 0) return;
        JsonDocument body;
        const bool lost = strcmp(rule, "sensor_lost") == 0;
        body["severity"] = lost ? "alarm" : "warn";
        body["rule"] = rule;
        body[channel_] = failsafe_.isOn() ? "on" : "off";
        const float t = failsafe_.temperature(millis());
        if (!isnan(t)) body["temperature"] = round(t * 100) / 100;
        core.publishEvent("failsafe", body);
    }

    void readButton() {
        if (buttonPin_ < 0) return;
        const bool pressed = digitalRead(buttonPin_) == LOW;
        if (pressed && !buttonDown_ && millis() - lastButtonMs_ > 300) {
            lastButtonMs_ = millis();
            const bool on = !on_;
            if (features_.has(kHeater)) {
                failsafe_.command(on, 0, millis());  // a refused press does nothing
            } else {
                commanded_ = on;
                if (on) timer_.start(millis(), 0, maxOnS_);
                else timer_.stop();
            }
        }
        buttonDown_ = pressed;
    }

    AckResult command(JsonObjectConst p) {
        if (!features_.has(kRelay)) return AckResult::error("this hornet has no relay in its features");
        const char* ch = p["channel"] | "";
        if (channel_ != ch) return AckResult::error("unknown channel " + String(ch));
        const String set = p["set"] | "";
        if (set != "on" && set != "off") return AckResult::error("set must be on or off");
        const bool on = set == "on";
        if (features_.has(kHeater)) {
            if (const char* why = failsafe_.command(on, p["for_s"] | 0U, millis())) return AckResult(AckStatus::Rejected, why);
            write(failsafe_.isOn());
        } else {
            commanded_ = on;
            if (on) timer_.start(millis(), p["for_s"] | 0U, maxOnS_);
            else timer_.stop();
            write(commanded_);
        }
        publishState(*core_);
        AckResult r;
        r.state[channel_] = on_ ? "on" : "off";
        return r;
    }

    void write(bool on) {
        on_ = on;
        if (relayPin_ >= 0) digitalWrite(relayPin_, on != activeLow_ ? HIGH : LOW);
    }

    void publishState(Core& core) {
        JsonDocument state;
        state[channel_] = on_ ? "on" : "off";
        core.publishState(state);
        statePublished_ = core.online();
    }

    Core* core_ = nullptr;
    Features features_;
    // sensors
    OneWire oneWire_;
    DallasTemperature dallas_;
    Adafruit_BME280 bme_;
    int onewirePin_ = -1;
    int sdaPin_ = -1;
    int sclPin_ = -1;
    uint8_t dsCount_ = 0;
    bool bmeOk_ = false;
    bool warned_ = false;
    std::vector<String> names_;
    uint32_t teleIntervalMs_ = 60000;
    uint32_t lastReadMs_ = 0;
    uint32_t lastPublishMs_ = 0;
    uint32_t conversionStartMs_ = 0;
    bool converting_ = false;
    bool started_ = false;
    bool published_ = false;
    bool sent_ = false;  // battery mode: this wake-up's reading has gone out
    // relay
    String channel_ = "heater";
    int relayPin_ = -1;
    bool activeLow_ = false;
    int buttonPin_ = -1;
    bool on_ = false;         // what the relay pin is now
    bool commanded_ = false;  // plain relay: what the commands (or the button) asked for
    bool buttonDown_ = false;
    uint32_t lastButtonMs_ = 0;
    uint32_t maxOnS_ = 0;
    bool statePublished_ = false;
    RelayTimer timer_;        // plain relay only; a heater's limits live in failsafe_
    HeaterFailsafe failsafe_;
    Battery battery_;
    uint32_t lastBatteryMs_ = 0;
    bool batterySent_ = false;
};

Heat role;

}  // namespace

hornet::Role& hornetRole() { return role; }
