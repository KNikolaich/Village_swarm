// Role meteo: temperature from DS18B20 (one or more on a 1-Wire bus) and/or temperature, humidity and
// pressure from a BME280 (I2C 0x76/0x77). What is connected is detected at start; pins come from the board
// defaults (platformio.ini HW_* flags) or the config "hw" section.
//
// Metric names: humidity and pressure (hPa) from the BME280; temperatures are temperature, temperature_2,
// temperature_3... with the BME280 first and the DS18B20s after it in bus order.

#include <Adafruit_BME280.h>
#include <DallasTemperature.h>
#include <HornetCore.h>
#include <OneWire.h>
#include <Wire.h>

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
        return m;
    }

    void setup(Core&) override { initSensors(); }

    void applyConfig(Core&, JsonObjectConst config) override {
        teleIntervalMs_ = (config["tele_interval_s"] | 60U) * 1000UL;
        JsonObjectConst hw = config["hw"];
        const int onewire = hw["onewire_pin"] | HW_ONEWIRE_PIN;
        const int sda = hw["sda_pin"] | HW_SDA_PIN;
        const int scl = hw["scl_pin"] | HW_SCL_PIN;
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

        // temperature, temperature_2, temperature_3... in order: BME280 first, then the DS18B20s on the bus.
        names_.clear();
        if (bmeOk_) names_.push_back("temperature");
        for (uint8_t i = 0; i < dsCount_; ++i)
            names_.push_back(names_.empty() ? String("temperature") : "temperature_" + String(names_.size() + 1));
        Serial.printf("[meteo] ds18b20=%u bme280=%s (1-wire %d, i2c %d/%d)\n", dsCount_, bmeOk_ ? "yes" : "no", onewirePin_, sdaPin_, sclPin_);
    }

    void publish(Core& core) {
        if (!bmeOk_ && dsCount_ == 0) {
            if (!warned_) core.log(LogLevel::Warn, "no sensor found (DS18B20 or BME280)");
            warned_ = true;
            return;
        }
        size_t name = 0;
        if (bmeOk_) {
            const float t = bme_.readTemperature();
            const float h = bme_.readHumidity();
            const float p = bme_.readPressure() / 100.0F;
            if (!isnan(t)) core.publishTele("temperature", t, "C");
            if (!isnan(h)) core.publishTele("humidity", h, "%");
            if (!isnan(p) && p > 300) core.publishTele("pressure", p, "hPa");
            name = 1;
        }
        for (uint8_t i = 0; i < dsCount_; ++i, ++name) {
            const float t = dallas_.getTempCByIndex(i);
            if (t == DEVICE_DISCONNECTED_C || t == 85.0F) {  // 85 is the power-on value: conversion did not happen
                core.log(LogLevel::Warn, "DS18B20 #" + String(i) + " read failed");
                continue;
            }
            core.publishTele(names_[name].c_str(), t, "C");
        }
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
    uint32_t teleIntervalMs_ = 60000;
    uint32_t lastReadMs_ = 0;
    uint32_t conversionStartMs_ = 0;
    bool converting_ = false;
    bool started_ = false;
};

Meteo role;

}  // namespace

hornet::Role& hornetRole() { return role; }
