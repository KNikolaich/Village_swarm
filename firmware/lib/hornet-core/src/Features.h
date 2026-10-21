#pragma once

// What a hornet is used for (config "features", contracts/schemas/config.schema.json): one universal ESP8266 image
// can be a thermometer, a relay or a heater, and touches only the pins of what it is used for. Arduino-free.

#include <cstdint>
#include <cstring>

namespace hornet {

enum Feature : uint8_t {
    kThermometer = 1 << 0,  // DS18B20s on 1-Wire or a DHT11/DHT22, as TempSensor says
    kBme280 = 1 << 1,       // BME280 on I2C
    kRelay = 1 << 2,        // relay switched by cmd/relay
    kHeater = 1 << 3,       // that relay heats; the hornet keeps failsafe.heater itself
};

struct Features {
    uint8_t bits = 0;

    bool has(Feature f) const { return (bits & f) != 0; }

    // A heater switches the relay, so it implies one.
    Features normalized() const {
        Features f{bits};
        if (f.has(kHeater)) f.bits |= kRelay;
        return f;
    }

    static uint8_t parseOne(const char* name) {
        if (!name) return 0;
        if (std::strcmp(name, "thermometer") == 0) return kThermometer;
        if (std::strcmp(name, "bme280") == 0) return kBme280;
        if (std::strcmp(name, "relay") == 0) return kRelay;
        if (std::strcmp(name, "heater") == 0) return kHeater;
        return 0;
    }
};

// The sensor the feature thermometer reads (config hw.thermometer): DS18B20s on 1-Wire, or one DHT on hw.dht_pin.
enum class TempSensor : uint8_t { Ds18b20, Dht11, Dht22 };

// "ds18b20", "dht11", "dht22"; anything else (or nothing) keeps the build's default.
inline TempSensor parseTempSensor(const char* name, TempSensor fallback) {
    if (!name) return fallback;
    if (std::strcmp(name, "ds18b20") == 0) return TempSensor::Ds18b20;
    if (std::strcmp(name, "dht11") == 0) return TempSensor::Dht11;
    if (std::strcmp(name, "dht22") == 0) return TempSensor::Dht22;
    return fallback;
}

}  // namespace hornet
