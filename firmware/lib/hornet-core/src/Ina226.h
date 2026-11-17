#pragma once

// INA226 on the I2C bus (config hw.ina_*): the voltage and the current of a battery behind a shunt, published as the
// metrics battery_voltage (V), battery_current (A), battery_power (W) and battery_ah (Ah taken out since it was full).
// Shared by the roles that can measure it (monitor). Wiring: docs/ina226-battery.md; the arithmetic is in PowerMeter.h.
//
// The chip is found by itself: at every boot (and every 30 s while there is none) the monitor asks the address for the
// manufacturer ID, and the sensor turns on when an INA226 answers. On the 0.42" board it shares the I2C bus of the
// screen (GPIO5 SDA, GPIO6 SCL): the screen is at 0x3C, the INA226 at 0x40 by default.
//
// Current is positive when it flows from the IN+ pin to IN-: wire IN+ to the battery and IN- to the load and the
// charger, and a discharging battery reads plus, a charging one minus (hw.ina_invert swaps that without a soldering iron).

#include <Arduino.h>
#include <PowerMeter.h>
#include <Preferences.h>
#include <Wire.h>

#ifndef HW_INA_ADDR
#define HW_INA_ADDR 0x40
#endif
// The shunt of the common blue INA226 module is marked R010 (10 mOhm, up to +-8 A); with R002 (2 mOhm) set
// hw.ina_shunt_mohm = 2. R100 (0.1 Ohm) is good only up to 0.8 A.
#ifndef HW_INA_SHUNT_MOHM
#define HW_INA_SHUNT_MOHM 10.0
#endif

namespace hornet {

class Ina226 {
public:
    static constexpr uint32_t kSampleMs = 1000;
    static constexpr uint32_t kProbeMs = 30000;
    static constexpr uint32_t kSaveMs = 300000;  // the Ah counter is kept in flash every 5 minutes at most

    struct Tele {
        float volts;
        float amps;
        float watts;
        float ah;
    };

    // From the config "hw" section. True when its settings changed.
    template <typename Hw>
    bool configure(const Hw& hw) {
        const uint8_t addr = static_cast<uint8_t>(hw["ina_addr"] | HW_INA_ADDR);
        float shunt = hw["ina_shunt_mohm"] | static_cast<float>(HW_INA_SHUNT_MOHM);
        if (!(shunt > 0)) shunt = HW_INA_SHUNT_MOHM;
        const bool invert = hw["ina_invert"] | false;
        const float fullV = hw["ina_full_v"] | 14.0F;
        const float fullA = hw["ina_full_a"] | 0.5F;
        const bool changed = addr != addr_ || shunt != shuntMohm_ || invert != invert_;
        addr_ = addr;
        shuntMohm_ = shunt;
        invert_ = invert;
        counter_.configure(fullV, fullA, 600.0F);
        if (changed) present_ = false;  // look for it again at the new address
        if (changed) lastProbeMs_ = 0;
        return changed;
    }

    // After Wire.begin(): the Ah counter comes back from flash, the chip is looked for at once.
    void begin() {
        if (!loaded_) {
            Preferences prefs;
            if (prefs.begin("ina", true)) {
                counter_.set(prefs.getFloat("ah", 0.0F));
                prefs.end();
            }
            savedAh_ = counter_.ah();
            loaded_ = true;
        }
        present_ = false;
        probe();
    }

    bool enabled() const { return present_; }

    // Call from loop(): one reading a second, averaged for telemetry, with the Ah counter integrating them.
    void poll(uint32_t now) {
        if (!present_) {
            if (now - lastProbeMs_ >= kProbeMs) probe();
            return;
        }
        if (now - lastSampleMs_ < kSampleMs) return;
        const uint32_t dtMs = now - lastSampleMs_;
        lastSampleMs_ = now;
        uint16_t bus = 0;
        uint16_t shunt = 0;
        if (!readReg(0x02, bus) || !readReg(0x01, shunt)) {
            if (++failures_ >= 5) {
                present_ = false;
                lastProbeMs_ = now;
                Serial.println("[ina226] no answer, off");
            }
            return;
        }
        failures_ = 0;
        const float v = power::busVolts(bus);
        const float a = power::amps(static_cast<int16_t>(shunt), shuntMohm_, invert_);
        if (std::isnan(a)) return;
        volts_.add(v);
        amps_.add(a);
        watts_.add(v * a);
        liveV_ = liveV_ < 0 ? v : liveV_ + 0.4F * (v - liveV_);
        liveA_ = liveA_ == kNoLive ? a : liveA_ + 0.4F * (a - liveA_);
        counter_.update(v, a, dtMs / 1000.0F);
        if (now - lastSaveMs_ >= kSaveMs) save(now);
    }

    // The latest smoothed reading for a screen; false until there is one.
    bool live(float& volts, float& amps) const {
        if (!present_ || liveV_ < 0 || liveA_ == kNoLive) return false;
        volts = liveV_;
        amps = liveA_;
        return true;
    }

    // The means since the last call (the interval of the telemetry); false when there is no sample.
    bool take(Tele& out) {
        if (volts_.empty()) return false;
        out = {volts_.mean(), amps_.mean(), watts_.mean(), counter_.ah()};
        volts_.take();
        amps_.take();
        watts_.take();
        return true;
    }

private:
    static constexpr float kNoLive = -1000.0F;

    bool readReg(uint8_t reg, uint16_t& out) {
        Wire.beginTransmission(addr_);
        Wire.write(reg);
        if (Wire.endTransmission(false) != 0) return false;
        if (Wire.requestFrom(addr_, static_cast<uint8_t>(2)) != 2) return false;
        const uint8_t hi = Wire.read();
        const uint8_t lo = Wire.read();
        out = static_cast<uint16_t>(hi << 8 | lo);
        return true;
    }

    bool writeReg(uint8_t reg, uint16_t value) {
        Wire.beginTransmission(addr_);
        Wire.write(reg);
        Wire.write(static_cast<uint8_t>(value >> 8));
        Wire.write(static_cast<uint8_t>(value & 0xFF));
        return Wire.endTransmission() == 0;
    }

    // Manufacturer ID 0x5449 ("TI") in register 0xFE, die ID 0x2260 in 0xFF; then 16 averages of 1.1 ms for the bus
    // and the shunt, continuous: a reading every ~35 ms, taken by the chip itself.
    void probe() {
        lastProbeMs_ = millis();
        uint16_t manufacturer = 0;
        uint16_t die = 0;
        if (!readReg(0xFE, manufacturer) || !readReg(0xFF, die) || manufacturer != 0x5449 || (die & 0xFFF0) != 0x2260) return;
        if (!writeReg(0x00, 0x4527)) return;
        present_ = true;
        failures_ = 0;
        lastSampleMs_ = millis();
        liveV_ = -1;
        liveA_ = kNoLive;
        Serial.printf("[ina226] found at 0x%02X, shunt %.2f mOhm\n", addr_, shuntMohm_);
    }

    void save(uint32_t now) {
        lastSaveMs_ = now;
        const float ah = counter_.ah();
        if (std::fabs(ah - savedAh_) < 0.01F) return;
        Preferences prefs;
        if (!prefs.begin("ina", false)) return;
        prefs.putFloat("ah", ah);
        prefs.end();
        savedAh_ = ah;
    }

    uint8_t addr_ = HW_INA_ADDR;
    float shuntMohm_ = HW_INA_SHUNT_MOHM;
    bool invert_ = false;
    bool present_ = false;
    bool loaded_ = false;
    uint8_t failures_ = 0;
    uint32_t lastProbeMs_ = 0;
    uint32_t lastSampleMs_ = 0;
    uint32_t lastSaveMs_ = 0;
    float savedAh_ = 0;
    float liveV_ = -1;
    float liveA_ = kNoLive;
    power::Window volts_, amps_, watts_;
    power::ChargeCounter counter_;
};

}  // namespace hornet
