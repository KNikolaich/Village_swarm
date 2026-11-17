#pragma once

// What an INA226 (battery voltage and current on I2C) says, as numbers: raw register values to volts and amps, the
// mean over a telemetry interval, and the charge counter (Ah taken out of the battery since it was last full).
// Arduino-free, so the native tests cover it. The bus part is in Ina226.h.

#include <cmath>
#include <cstdint>

namespace hornet::power {

constexpr float kBusLsbV = 0.00125F;     // INA226 bus voltage register: 1.25 mV per step
constexpr float kShuntLsbV = 0.0000025F;  // INA226 shunt voltage register: 2.5 uV per step (signed)

inline float busVolts(uint16_t raw) { return raw * kBusLsbV; }

// Amps through a shunt of shuntMohm milliohms; sign as the register has it (+ = from IN+ to IN-), flipped by invert.
inline float amps(int16_t raw, float shuntMohm, bool invert = false) {
    if (!(shuntMohm > 0)) return NAN;
    const float a = raw * kShuntLsbV / (shuntMohm / 1000.0F);
    return invert ? -a : a;
}

// A mean of the samples of one interval; take() starts the next interval.
class Window {
public:
    void add(float v) {
        sum_ += v;
        ++n_;
    }
    bool empty() const { return n_ == 0; }
    float mean() const { return n_ ? sum_ / n_ : NAN; }
    void take() {
        sum_ = 0;
        n_ = 0;
    }

private:
    double sum_ = 0;
    uint32_t n_ = 0;
};

// Ah taken out of a battery since it was full. Discharge (amps > 0) adds, charge subtracts at kChargeEfficiency, so a
// battery that is charged back reads about zero again; the count never goes below 0. "Full" is read from the charger:
// the voltage at or above fullV with only a trickle going in or out (|amps| < fullA) for fullHoldS seconds in a row
// (a lead-acid battery taking 14.4 V still draws amps while it is not full, so the voltage alone proves nothing).
class ChargeCounter {
public:
    static constexpr float kChargeEfficiency = 0.9F;

    void configure(float fullV, float fullA, float fullHoldS) {
        fullV_ = fullV;
        fullA_ = fullA;
        fullHoldS_ = fullHoldS;
    }

    // dtS: seconds since the last call. Returns true when the counter was just reset because the battery is full.
    bool update(float volts, float amps, float dtS) {
        if (!(dtS > 0) || dtS > 60.0F || std::isnan(amps) || std::isnan(volts)) return false;
        ah_ += (amps > 0 ? amps : amps * kChargeEfficiency) * dtS / 3600.0F;
        if (ah_ < 0) ah_ = 0;
        const bool trickle = fullV_ > 0 && volts >= fullV_ && std::fabs(amps) < fullA_;
        if (!trickle) {
            heldS_ = 0;
            return false;
        }
        heldS_ += dtS;
        if (heldS_ >= fullHoldS_ && ah_ > 0) {
            ah_ = 0;
            return true;
        }
        return false;
    }

    float ah() const { return ah_; }
    void set(float ah) { ah_ = ah > 0 ? ah : 0; }

private:
    float ah_ = 0;
    float fullV_ = 14.0F;
    float fullA_ = 0.5F;
    float fullHoldS_ = 600.0F;
    float heldS_ = 0;
};

}  // namespace hornet::power
