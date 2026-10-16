#pragma once

// Battery (supply) voltage behind a divider on an ADC pin: config hw.vbat_pin and hw.vbat_ratio, published as
// the metric battery_voltage (V). Shared by the roles that can measure it (meteo, relay).
//
// ESP32: any ADC pin, read in millivolts. ESP8266: the only ADC is A0 (GPIO17), whatever vbat_pin says.
//
// ESP-01/01S have no A0 on their header. Their builds set HW_VBAT_VCC=1: the chip then measures its own supply
// (ESP.getVcc(), a battery wired straight to 3V3 as in docs/hornet-esp01-battery.md) and publishes it the same way.
// It is good to about 0.1 V, enough for the discharge curve; hw.vbat_ratio can be used to calibrate it against a meter.

#include <Arduino.h>

#ifndef HW_VBAT_PIN
#define HW_VBAT_PIN -1
#endif
#ifndef HW_VBAT_VCC
#define HW_VBAT_VCC 0
#endif
#ifndef HW_VBAT_RATIO
#define HW_VBAT_RATIO 1.0
#endif
// ESP8266 A0: volts at analogRead() = 1023 (1.0 on a bare ESP-12/ESP-01, 3.2 with the D1 mini / NodeMCU divider).
#ifndef HW_ADC_FULL_SCALE
#define HW_ADC_FULL_SCALE 3.2
#endif

#if HW_VBAT_VCC && defined(ESP8266)
// The ADC reads the supply instead of A0. Defined once: only one role is built into an image.
ADC_MODE(ADC_VCC);
#endif

namespace hornet {

class Battery {
public:
    // From the config "hw" section; the build defaults when a key is missing.
    template <typename Hw>
    void configure(const Hw& hw) {
        pin_ = hw["vbat_pin"] | HW_VBAT_PIN;
        ratio_ = hw["vbat_ratio"] | HW_VBAT_RATIO;
        if (ratio_ <= 0) ratio_ = HW_VBAT_RATIO;
    }

    bool enabled() const { return HW_VBAT_VCC || pin_ >= 0; }

    // Averaged ADC reading times the divider ratio, in volts.
    float read() const {
#if HW_VBAT_VCC && defined(ESP8266)
        uint32_t supply = 0;
        constexpr int kReads = 16;
        for (int i = 0; i < kReads; ++i) supply += ESP.getVcc();
        return supply / static_cast<float>(kReads) / 1000.0F * ratio_;
#endif
        uint32_t sum = 0;
        constexpr int kSamples = 16;
        for (int i = 0; i < kSamples; ++i) {
#if defined(ESP32)
            sum += analogReadMilliVolts(pin_);
#else
            sum += static_cast<uint32_t>(analogRead(A0) * (HW_ADC_FULL_SCALE * 1000.0) / 1023.0);
#endif
        }
        return sum / static_cast<float>(kSamples) / 1000.0F * ratio_;
    }

private:
    int pin_ = HW_VBAT_PIN;
    float ratio_ = HW_VBAT_RATIO;
};

}  // namespace hornet
