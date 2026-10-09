// Role meteo: temperature from DS18B20 (one or more on a 1-Wire bus) and/or temperature, humidity and
// pressure from a BME280 (I2C 0x76/0x77). What is connected is detected at start; pins come from the board
// defaults (platformio.ini HW_* flags) or the config "hw" section.
//
// Metric names: humidity and pressure (hPa) from the BME280; temperatures are temperature, temperature_2,
// temperature_3... with the BME280 first and the DS18B20s after it in bus order. With hw.vbat_pin set, a
// battery behind a voltage divider is measured as battery_voltage (V).
//
// Builds with HORNET_DISPLAY (meteo-c3oled) also drive the 0.42" SSD1306 OLED (72x40): two metrics in large
// digits, chosen by config display.lines, and a dot in the corner while the hornet is online.

#include <Adafruit_BME280.h>
#include <DallasTemperature.h>
#include <HornetCore.h>
#include <OneWire.h>
#include <Wire.h>

#include <map>

#if HORNET_DISPLAY
#include <U8g2lib.h>
#endif

#ifndef HW_VBAT_PIN
#define HW_VBAT_PIN -1
#endif
#ifndef HW_VBAT_RATIO
#define HW_VBAT_RATIO 1.0
#endif
// ESP8266 A0: volts at analogRead() = 1023 (1.0 on a bare ESP-12/ESP-01, 3.2 with the D1 mini / NodeMCU divider).
#ifndef HW_ADC_FULL_SCALE
#define HW_ADC_FULL_SCALE 3.2
#endif

namespace {

using namespace hornet;

constexpr uint32_t kConversionMs = 800;  // DS18B20 at 12 bits

class Meteo : public Role {
public:
    const char* type() const override { return "meteo"; }
    const char* hw() const override { return HORNET_BOARD; }

    std::vector<const char*> caps() const override {
        std::vector<const char*> c;
        if (dsCount_ > 0) c.push_back("sensor.ds18b20");
        if (bmeOk_) c.push_back("sensor.bme280");
        return c;
    }

    std::vector<const char*> metrics() const override {
        std::vector<const char*> m;
        for (const auto& n : names_) m.push_back(n.c_str());
        if (bmeOk_) {
            m.push_back("humidity");
            m.push_back("pressure");
        }
        if (vbatPin_ >= 0) m.push_back("battery_voltage");
        return m;
    }

    void setup(Core&) override { initSensors(); }

    void applyConfig(Core&, JsonObjectConst config) override {
        teleIntervalMs_ = (config["tele_interval_s"] | 60U) * 1000UL;
        JsonObjectConst hw = config["hw"];
        const int onewire = hw["onewire_pin"] | HW_ONEWIRE_PIN;
        const int sda = hw["sda_pin"] | HW_SDA_PIN;
        const int scl = hw["scl_pin"] | HW_SCL_PIN;
        vbatPin_ = hw["vbat_pin"] | HW_VBAT_PIN;
        vbatRatio_ = hw["vbat_ratio"] | HW_VBAT_RATIO;
        lines_.clear();
        for (JsonVariantConst line : config["display"]["lines"].as<JsonArrayConst>()) lines_.push_back(line.as<String>());
        if (onewire != onewirePin_ || sda != sdaPin_ || scl != sclPin_) {
            onewirePin_ = onewire;
            sdaPin_ = sda;
            sclPin_ = scl;
            initSensors();
        }
    }

    void loop(Core& core) override {
        const uint32_t now = millis();
        if (!converting_ && (!started_ || now - lastReadMs_ >= teleIntervalMs_)) {
            started_ = true;  // first reading right after boot
            lastReadMs_ = now;
            if (dsCount_ > 0) dallas_.requestTemperatures();  // non-blocking, read after kConversionMs
            converting_ = true;
            conversionStartMs_ = now;
        }
        if (converting_ && now - conversionStartMs_ >= (dsCount_ > 0 ? kConversionMs : 0)) {
            converting_ = false;
            publish(core);
        }
    }

private:
    void initSensors() {
        if (onewirePin_ < 0) {
            onewirePin_ = HW_ONEWIRE_PIN;
            sdaPin_ = HW_SDA_PIN;
            sclPin_ = HW_SCL_PIN;
        }
        // On boards where 1-Wire and I2C share pins (ESP-01), only one kind can be wired; try both anyway.
        oneWire_.begin(onewirePin_);
        dallas_.setOneWire(&oneWire_);
        dallas_.begin();
        dallas_.setWaitForConversion(false);
        dsCount_ = dallas_.getDeviceCount();

        Wire.begin(sdaPin_, sclPin_);
        bmeOk_ = bme_.begin(0x76, &Wire) || bme_.begin(0x77, &Wire);
#if HORNET_DISPLAY
        displayOk_ = oled_.begin();
        if (displayOk_) {
            oled_.clearBuffer();
            oled_.setFont(u8g2_font_6x10_tf);
            oled_.drawStr(0, 12, "Village");
            oled_.drawStr(0, 26, "Swarm");
            oled_.sendBuffer();
        }
#endif

        // temperature, temperature_2, temperature_3... in order: BME280 first, then the DS18B20s on the bus.
        names_.clear();
        if (bmeOk_) names_.push_back("temperature");
        for (uint8_t i = 0; i < dsCount_; ++i)
            names_.push_back(names_.empty() ? String("temperature") : "temperature_" + String(names_.size() + 1));
        Serial.printf("[meteo] ds18b20=%u bme280=%s (1-wire %d, i2c %d/%d)\n", dsCount_, bmeOk_ ? "yes" : "no", onewirePin_, sdaPin_, sclPin_);
    }

    void publish(Core& core) {
        if (vbatPin_ >= 0) report(core, "battery_voltage", readBattery(), "V");
        if (!bmeOk_ && dsCount_ == 0) {
            if (!warned_ && vbatPin_ < 0) core.log(LogLevel::Warn, "no sensor found (DS18B20 or BME280)");
            warned_ = true;
            draw(core);
            return;
        }
        size_t name = 0;
        if (bmeOk_) {
            const float t = bme_.readTemperature();
            const float h = bme_.readHumidity();
            const float p = bme_.readPressure() / 100.0F;
            if (!isnan(t)) report(core, "temperature", t, "C");
            if (!isnan(h)) report(core, "humidity", h, "%");
            if (!isnan(p) && p > 300) report(core, "pressure", p, "hPa");
            name = 1;
        }
        for (uint8_t i = 0; i < dsCount_; ++i, ++name) {
            const float t = dallas_.getTempCByIndex(i);
            if (t == DEVICE_DISCONNECTED_C || t == 85.0F) {  // 85 is the power-on value: conversion did not happen
                core.log(LogLevel::Warn, "DS18B20 #" + String(i) + " read failed");
                continue;
            }
            report(core, names_[name].c_str(), t, "C");
        }
        draw(core);
    }

    void report(Core& core, const char* metric, float value, const char* unit) {
        core.publishTele(metric, value, unit);
        values_[metric] = value;
    }

    // Averaged ADC reading times the divider ratio.
    float readBattery() const {
        uint32_t sum = 0;
        constexpr int kSamples = 16;
        for (int i = 0; i < kSamples; ++i) {
#if defined(ESP32)
            sum += analogReadMilliVolts(vbatPin_);
#else
            sum += static_cast<uint32_t>(analogRead(A0) * (HW_ADC_FULL_SCALE * 1000.0) / 1023.0);
#endif
        }
        return sum / static_cast<float>(kSamples) / 1000.0F * vbatRatio_;
    }

    // What goes on the screen when the config does not say: the first two of these that exist.
    std::vector<String> displayLines() const {
        if (!lines_.empty()) return lines_;
        std::vector<String> out;
        for (const char* m : {"temperature", "humidity", "temperature_2", "battery_voltage", "pressure"})
            if (values_.count(m) && out.size() < 2) out.push_back(m);
        return out;
    }

    static String format(const String& metric, float v) {
        if (metric.startsWith("temperature")) return String(v, 1) + "°";
        if (metric == "humidity") return String(v, 0) + "%";
        if (metric == "battery_voltage") return String(v, 1) + "V";
        if (metric == "pressure") return String(v, 0);
        return String(v, 1);
    }

    void draw(Core& core) {
#if HORNET_DISPLAY
        if (!displayOk_) return;
        oled_.clearBuffer();
        oled_.setFont(u8g2_font_logisoso16_tf);
        int y = 18;
        for (const auto& line : displayLines()) {
            const auto it = values_.find(line);
            const String text = it == values_.end() ? String("--") : format(line, it->second);
            oled_.drawUTF8(0, y, text.c_str());
            y += 20;
        }
        if (core.online()) oled_.drawDisc(69, 2, 2);
        oled_.sendBuffer();
#else
        (void)core;
#endif
    }

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
    std::vector<String> lines_;
    std::map<String, float> values_;
    int vbatPin_ = HW_VBAT_PIN;
    float vbatRatio_ = HW_VBAT_RATIO;
#if HORNET_DISPLAY
    // SSD1306 72x40 on the ESP32-C3 0.42" board, hardware I2C on the shared bus.
    U8G2_SSD1306_72X40_ER_F_HW_I2C oled_{U8G2_R0, U8X8_PIN_NONE};
    bool displayOk_ = false;
#endif
    uint32_t teleIntervalMs_ = 60000;
    uint32_t lastReadMs_ = 0;
    uint32_t conversionStartMs_ = 0;
    bool converting_ = false;
    bool started_ = false;
};

Meteo role;

}  // namespace

hornet::Role& hornetRole() { return role; }
