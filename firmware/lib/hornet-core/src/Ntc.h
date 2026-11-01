#pragma once

// NTC thermistor on an ADC pin (config hw.ntc_pin and hw.ntc_*), published as the metric temperature_ntc (C). Shared by
// the roles that can measure it (meteo, monitor). The formulas are in Thermistor.h, the wiring in docs/ntc-thermistor.md.
//
// Every 2 s the pin is read 9 times, the median of the burst goes into a moving average (Thermistor.h Smoother) and
// the roles publish that average at their telemetry interval. A battery hornet that wakes for one reading takes the
// median of a longer burst instead.
//
// ESP32: only ADC1 pins, ADC2 does not work while Wi-Fi is on (ESP32-C3: GPIO0..GPIO4; GPIO2 is a strapping pin, keep
// it free). ESP8266: the only ADC is A0 (GPIO17), whatever ntc_pin says; it is shared with the battery voltage.

#include <Arduino.h>
#include <Thermistor.h>

#ifndef HW_NTC_PIN
#define HW_NTC_PIN -1
#endif
// Where an NTC thermometer (config hw.thermometer = "ntc") is read when hw.ntc_pin does not say: A0 of the board.
#ifndef HW_NTC_THERMOMETER_PIN
#if defined(CONFIG_IDF_TARGET_ESP32C3)
#define HW_NTC_THERMOMETER_PIN 0  // GPIO0 = A0 (ADC1)
#elif defined(ESP8266)
#define HW_NTC_THERMOMETER_PIN 17  // A0
#else
#define HW_NTC_THERMOMETER_PIN HW_NTC_PIN
#endif
#endif
// ESP8266 A0: volts at analogRead() = 1023 (1.0 on a bare ESP-12/ESP-01, 3.2 with the D1 mini / NodeMCU divider).
#ifndef HW_ADC_FULL_SCALE
#define HW_ADC_FULL_SCALE 3.2
#endif

namespace hornet {

class NtcSensor {
public:
    static constexpr uint32_t kSampleMs = 2000;

    // From the config "hw" section. True when the sensor came or went (the metrics change) or its settings did.
    // thermometer: the NTC is the hornet's thermometer (hw.thermometer = "ntc"), read on A0 when hw.ntc_pin is absent.
    template <typename Hw>
    bool configure(const Hw& hw, bool thermometer = false) {
        int pin = hw["ntc_pin"] | (thermometer ? HW_NTC_THERMOMETER_PIN : HW_NTC_PIN);
        if (pin >= 0 && !adcPin(pin)) {
            Serial.printf("[ntc] GPIO%d has no ADC usable with Wi-Fi, NTC off\n", pin);
            pin = -1;
        }
        ntc::Params p;
        p.side = ntc::parseSide(hw["ntc_side"].template as<const char*>());
        p.rSeries = positive(hw["ntc_r_series"] | p.rSeries, p.rSeries);
        p.rNominal = positive(hw["ntc_r_nominal"] | p.rNominal, p.rNominal);
        p.beta = positive(hw["ntc_beta"] | p.beta, p.beta);
        p.vcc = positive(hw["ntc_vcc"] | p.vcc, p.vcc);
        p.offset = hw["ntc_offset"] | 0.0F;
        const bool changed = pin != pin_ || p != params_;
        if (changed) {
            pin_ = pin;
            params_ = p;
            smoother_.reset();
            failures_ = 0;
            lastSampleMs_ = 0;
            sampled_ = false;  // the ADC stays at the core's default attenuation: up to ~2.5 V on the C3, ~3.1 V on the ESP32
        }
        return changed;
    }

    bool enabled() const { return pin_ >= 0; }

    // Call from loop(): a burst every kSampleMs feeds the average (about 1 ms of ADC work).
    void poll(uint32_t now) {
        if (!enabled() || (sampled_ && now - lastSampleMs_ < kSampleMs)) return;
        sampled_ = true;
        lastSampleMs_ = now;
        const float c = ntc::celsiusFromMv(params_, burstMv(9));
        if (std::isnan(c)) {
            if (failures_ < 255) ++failures_;
            return;
        }
        failures_ = 0;
        smoother_.add(c);
    }

    // The smoothed reading; NaN until the first good burst or while the pin reads a rail.
    float value() const { return failures_ >= 3 ? NAN : smoother_.value(); }

    // One reading now without the average (battery hornet: one per wake-up).
    float readNow() const { return enabled() ? ntc::celsiusFromMv(params_, burstMv(31)) : NAN; }

    // Several failed bursts in a row: open or shorted divider (the roles log it once).
    bool failing() const { return failures_ >= 3; }

private:
    static float positive(float v, float fallback) { return v > 0 ? v : fallback; }

    static bool adcPin(int pin) {
#if defined(CONFIG_IDF_TARGET_ESP32C3)
        return pin >= 0 && pin <= 4;  // ADC1; GPIO5 is ADC2
#elif defined(CONFIG_IDF_TARGET_ESP32S3)
        return pin >= 1 && pin <= 10;  // ADC1
#elif defined(CONFIG_IDF_TARGET_ESP32)
        return pin >= 32 && pin <= 39;  // ADC1
#elif defined(ESP32)
        return digitalPinToAnalogChannel(pin) >= 0;
#else
        (void)pin;
        return true;  // ESP8266: A0 whatever the number
#endif
    }

    float burstMv(int n) const {
        float v[32];
        if (n > 32) n = 32;
        for (int i = 0; i < n; ++i) {
#if defined(ESP32)
            v[i] = static_cast<float>(analogReadMilliVolts(pin_));
#else
            v[i] = analogRead(A0) * (HW_ADC_FULL_SCALE * 1000.0F) / 1023.0F;
#endif
        }
        return ntc::median(v, n);
    }

    int pin_ = -1;
    ntc::Params params_;
    ntc::Smoother smoother_;
    uint32_t lastSampleMs_ = 0;
    bool sampled_ = false;
    uint8_t failures_ = 0;
};

}  // namespace hornet
