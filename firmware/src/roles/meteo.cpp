// Role meteo: temperature from DS18B20 (one or more on a 1-Wire bus) and/or temperature, humidity and
// pressure from a BME280 (I2C 0x76/0x77). What is connected is detected at start; pins come from the board
// defaults (platformio.ini HW_* flags) or the config "hw" section.
//
// Metric names: humidity and pressure (hPa) from the BME280; temperatures are temperature, temperature_2,
// temperature_3... with the BME280 first and the DS18B20s after it in bus order. With hw.vbat_pin set, a
// battery behind a voltage divider is measured as battery_voltage (V).
//
// Config "features" (thermometer, bme280) limits which sensors are looked for: on the ESP-01 1-Wire and I2C share
// GPIO2, so a hornet with only a DS18B20 leaves I2C alone. Absent: both.
//
// The thermometer is DS18B20s on 1-Wire or one DHT11/DHT22 on hw.dht_pin, as config hw.thermometer says; a DHT also
// gives humidity (humidity_2 next to a BME280). Builds with HW_DHT_PIN/HW_DHT_TYPE (meteo-esp01dht: the "ESP-01S
// DHT11" module) take that DHT as the default; elsewhere the default is DS18B20s.
//
// Builds with HORNET_DISPLAY (meteo-c3oled) also drive the 0.42" SSD1306 OLED (72x40): two metrics in large
// digits, chosen by config display.lines, and a dot in the corner while the hornet is online.

#include <Adafruit_BME280.h>
#include <Battery.h>
#include <DHTesp.h>
#include <DallasTemperature.h>
#include <Features.h>
#include <HornetCore.h>
#include <OneWire.h>
#include <Wire.h>

#include <map>

#ifndef HORNET_DISPLAY
#define HORNET_DISPLAY 0
#endif

#if HORNET_DISPLAY
#include <U8g2lib.h>
#endif

#ifndef HW_DHT_PIN
#define HW_DHT_PIN -1
#endif
#ifndef HW_DHT_TYPE
#define HW_DHT_TYPE 0  // no DHT of its own: the thermometer is DS18B20s unless the config says otherwise
#endif

namespace {

using namespace hornet;

constexpr uint32_t kConversionMs = 800;  // DS18B20 at 12 bits
constexpr TempSensor kDefaultSensor = HW_DHT_TYPE == 22 ? TempSensor::Dht22 : HW_DHT_TYPE == 11 ? TempSensor::Dht11 : TempSensor::Ds18b20;

class Meteo : public Role {
public:
    const char* type() const override { return "meteo"; }
    const char* hw() const override { return HORNET_BOARD; }

    std::vector<const char*> caps() const override {
        std::vector<const char*> c;
        if (dsCount_ > 0) c.push_back("sensor.ds18b20");
        if (bmeOk_) c.push_back("sensor.bme280");
        if (dhtOk_) c.push_back(sensor_ == TempSensor::Dht22 ? "sensor.dht22" : "sensor.dht11");
        return c;
    }

    std::vector<const char*> metrics() const override {
        std::vector<const char*> m;
        for (const auto& n : names_) m.push_back(n.c_str());
        if (bmeOk_) {
            m.push_back("humidity");
            m.push_back("pressure");
        }
        if (dhtOk_) m.push_back(dhtHumidity_.c_str());
        if (battery_.enabled()) m.push_back("battery_voltage");
        return m;
    }

    void setup(Core&) override { initSensors(); }

    void applyConfig(Core& core, JsonObjectConst config) override {
        teleIntervalMs_ = (config["tele_interval_s"] | 60U) * 1000UL;
        JsonObjectConst hw = config["hw"];
        const int onewire = hw["onewire_pin"] | HW_ONEWIRE_PIN;
        const int sda = hw["sda_pin"] | HW_SDA_PIN;
        const int scl = hw["scl_pin"] | HW_SCL_PIN;
        const int dhtPin = hw["dht_pin"] | HW_DHT_PIN;
        const TempSensor sensor = parseTempSensor(hw["thermometer"].as<const char*>(), kDefaultSensor);
        battery_.configure(hw);
        lines_.clear();
        for (JsonVariantConst line : config["display"]["lines"].as<JsonArrayConst>()) lines_.push_back(line.as<String>());
        Features features{kThermometer | kBme280};
        if (config["features"].is<JsonArrayConst>()) {
            features.bits = 0;
            for (JsonVariantConst f : config["features"].as<JsonArrayConst>()) features.bits |= Features::parseOne(f.as<const char*>());
        }
        if (onewire != onewirePin_ || sda != sdaPin_ || scl != sclPin_ || dhtPin != dhtPin_ || sensor != sensor_ ||
            features.bits != features_.bits) {
            features_ = features;
            onewirePin_ = onewire;
            sdaPin_ = sda;
            sclPin_ = scl;
            dhtPin_ = dhtPin;
            sensor_ = sensor;
            initSensors();
            core.refreshInfo();  // caps and metrics follow the sensors
        }
    }

    void loop(Core& core) override {
        // Battery mode: one reading per wake-up, sent once the hornet is online (nothing is buffered for tele).
        if (core.sleepy() && (!core.online() || sent_)) return;
        const uint32_t now = millis();
        if (!converting_ && (!started_ || now - lastReadMs_ >= core.readingIntervalMs(teleIntervalMs_))) {
            started_ = true;  // first reading right after boot
            lastReadMs_ = now;
            if (dsCount_ > 0) dallas_.requestTemperatures();  // non-blocking, read after kConversionMs
            converting_ = true;
            conversionStartMs_ = now;
        }
        if (converting_ && now - conversionStartMs_ >= (dsCount_ > 0 ? kConversionMs : 0)) {
            converting_ = false;
            publish(core);
            if (core.sleepy()) {
                sent_ = true;
                core.readingDone();
            }
        }
    }

private:
    void initSensors() {
        if (onewirePin_ < 0) {
            onewirePin_ = HW_ONEWIRE_PIN;
            sdaPin_ = HW_SDA_PIN;
            sclPin_ = HW_SCL_PIN;
        }
        // On boards where 1-Wire and I2C share pins (ESP-01), only one kind can be wired; config features says which.
        // The thermometer is either DS18B20s or a DHT (hw.thermometer), never both.
        const bool dht = features_.has(kThermometer) && sensor_ != TempSensor::Ds18b20;
        dsCount_ = 0;
        dhtOk_ = false;
        if (dht && dhtPin_ >= 0) {
            dht_.setup(dhtPin_, sensor_ == TempSensor::Dht22 ? DHTesp::DHT22 : DHTesp::DHT11);
            dhtOk_ = true;
            dhtFailures_ = 0;
        }
        if (features_.has(kThermometer) && !dht && onewirePin_ >= 0) {
            oneWire_.begin(onewirePin_);
            dallas_.setOneWire(&oneWire_);
            dallas_.begin();
            dallas_.setWaitForConversion(false);
            dsCount_ = dallas_.getDeviceCount();
        }

        bmeOk_ = false;
        const bool i2c = sdaPin_ >= 0 && sclPin_ >= 0;
        if (i2c && (features_.has(kBme280) || HORNET_DISPLAY)) Wire.begin(sdaPin_, sclPin_);  // the OLED shares the I2C bus
        if (i2c && features_.has(kBme280)) bmeOk_ = bme_.begin(0x76, &Wire) || bme_.begin(0x77, &Wire);
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

        // temperature, temperature_2, temperature_3... in order: BME280 first, then the DHT or the DS18B20s on the bus.
        names_.clear();
        if (bmeOk_) names_.push_back("temperature");
        if (dhtOk_) {
            names_.push_back(names_.empty() ? String("temperature") : String("temperature_2"));
            dhtName_ = names_.back();
            dhtHumidity_ = bmeOk_ ? "humidity_2" : "humidity";
        }
        for (uint8_t i = 0; i < dsCount_; ++i)
            names_.push_back(names_.empty() ? String("temperature") : "temperature_" + String(names_.size() + 1));
        Serial.printf("[meteo] ds18b20=%u dht=%s bme280=%s (1-wire %d, dht %d, i2c %d/%d)\n", dsCount_,
                      dhtOk_ ? (sensor_ == TempSensor::Dht22 ? "dht22" : "dht11") : "no", bmeOk_ ? "yes" : "no", onewirePin_,
                      dhtPin_, sdaPin_, sclPin_);
    }

    void publish(Core& core) {
        if (battery_.enabled()) report(core, "battery_voltage", battery_.read(), "V");
        if (!bmeOk_ && dsCount_ == 0 && !dhtOk_) {
            if (!warned_ && !battery_.enabled() && features_.bits != 0) core.log(LogLevel::Warn, "no sensor found (DS18B20, DHT or BME280)");
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
        if (dhtOk_) readDht(core);
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

    // One read takes about 25 ms (start pulse + 40 bits); the DHT11 must not be read more often than once a second,
    // the DHT22 once in two. A DHT11 read as a DHT22 (or the other way round) fails the same way a missing one does.
    void readDht(Core& core) {
        const TempAndHumidity v = dht_.getTempAndHumidity();
        if (dht_.getStatus() != DHTesp::ERROR_NONE || isnan(v.temperature) || isnan(v.humidity)) {
            if (++dhtFailures_ == 3) core.log(LogLevel::Warn, String("DHT read failed: ") + dht_.getStatusString());
            return;
        }
        dhtFailures_ = 0;
        report(core, dhtName_.c_str(), v.temperature, "C");
        report(core, dhtHumidity_.c_str(), v.humidity, "%");
    }

    void report(Core& core, const char* metric, float value, const char* unit) {
        core.publishTele(metric, value, unit);
        values_[metric] = value;
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

    Features features_{kThermometer | kBme280};
    OneWire oneWire_;
    DallasTemperature dallas_;
    Adafruit_BME280 bme_;
    int onewirePin_ = -1;
    int sdaPin_ = -1;
    int sclPin_ = -1;
    int dhtPin_ = HW_DHT_PIN;
    TempSensor sensor_ = kDefaultSensor;
    uint8_t dsCount_ = 0;
    bool bmeOk_ = false;
    bool dhtOk_ = false;
    DHTesp dht_;
    uint8_t dhtFailures_ = 0;
    String dhtName_ = "temperature";
    String dhtHumidity_ = "humidity";
    bool warned_ = false;
    std::vector<String> names_;
    std::vector<String> lines_;
    std::map<String, float> values_;
    Battery battery_;
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
    bool sent_ = false;  // battery mode: this wake-up's reading has gone out
};

Meteo role;

}  // namespace

hornet::Role& hornetRole() { return role; }
