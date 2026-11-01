#pragma once

// NTC thermistor behind a voltage divider on an ADC pin (config hw.ntc_*, docs/ntc-thermistor.md). Arduino-free: the
// divider and Beta formulas, the median of a burst of ADC reads and the moving average that keeps the reading still.
//
// Divider, side "high" (the default): 3V3 - NTC - pin - R series - GND. The pin voltage rises with the temperature, so
// the cold end (down to -40 C with 10k/10k) is far from the top of the ESP32-C3 ADC, which is accurate only to about 2.5 V.
// Side "low": 3V3 - R series - pin - NTC - GND, the classic Arduino wiring: good from about 0 C to well over 100 C.
//
//   high: R_ntc = R_series * (Vcc - V) / V        low: R_ntc = R_series * V / (Vcc - V)
//   1/T = 1/T0 + ln(R_ntc / R0) / B               (T in kelvin, R0 at T0 = 25 C)
//   reading = T - 273.15 + offset

#include <cmath>
#include <cstdint>
#include <cstring>

namespace hornet {
namespace ntc {

enum class Side : uint8_t { High, Low };  // where the NTC is: between 3V3 and the pin, or between the pin and GND

// "high" or "low"; anything else keeps the fallback.
inline Side parseSide(const char* name, Side fallback = Side::High) {
    if (!name) return fallback;
    if (std::strcmp(name, "high") == 0) return Side::High;
    if (std::strcmp(name, "low") == 0) return Side::Low;
    return fallback;
}

struct Params {
    Side side = Side::High;
    float rSeries = 10000.0F;   // ohm: the fixed resistor of the divider (measure it: 1% resistors are off by up to 100 ohm)
    float rNominal = 10000.0F;  // ohm: the NTC at 25 C (the "10k")
    float beta = 3950.0F;       // K: B25/50 or B25/85 from the datasheet (3950 for most cheap 10k probes, 3435 for many others)
    float vcc = 3.3F;           // V: what feeds the divider (the 3V3 pin)
    float offset = 0.0F;        // C: added to the result, calibration against a reference thermometer

    bool operator==(const Params& o) const {
        return side == o.side && rSeries == o.rSeries && rNominal == o.rNominal && beta == o.beta && vcc == o.vcc &&
               offset == o.offset;
    }
    bool operator!=(const Params& o) const { return !(*this == o); }
};

constexpr float kKelvin = 273.15F;
constexpr float kNominalK = 25.0F + kKelvin;
// Closer than this to a rail the pin is open or shorted: no thermistor would get it there in -40..150 C.
constexpr float kRailMv = 25.0F;

// The NTC's resistance from the pin voltage, NaN when the pin sits at a rail (a broken wire, a short).
inline float resistance(const Params& p, float mv) {
    const float vccMv = p.vcc * 1000.0F;
    if (!(mv > kRailMv) || !(mv < vccMv - kRailMv)) return NAN;
    return p.side == Side::High ? p.rSeries * (vccMv - mv) / mv : p.rSeries * mv / (vccMv - mv);
}

// Celsius from the NTC's resistance by the Beta equation, with the offset; NaN for NaN or nonsense.
inline float celsius(const Params& p, float ohms) {
    if (!(ohms > 0) || !(p.rNominal > 0) || !(p.beta > 0)) return NAN;
    const float inv = 1.0F / kNominalK + std::log(ohms / p.rNominal) / p.beta;
    if (!(inv > 0)) return NAN;
    return 1.0F / inv - kKelvin + p.offset;
}

inline float celsiusFromMv(const Params& p, float mv) { return celsius(p, resistance(p, mv)); }

// Median of n values (n <= 32), sorted in place: one ADC read spoiled by Wi-Fi noise does not move it.
inline float median(float* v, int n) {
    if (n <= 0) return NAN;
    for (int i = 1; i < n; ++i) {
        const float x = v[i];
        int j = i - 1;
        for (; j >= 0 && v[j] > x; --j) v[j + 1] = v[j];
        v[j + 1] = x;
    }
    return n % 2 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0F;
}

// Exponential moving average of the medians: value += alpha * (sample - value). With a sample every 2 s and alpha 0.2
// the reading follows a step in about 20 s and a lone jump of a degree moves it by a fifth. NaN samples are skipped. Two
// samples in a row further than kResetC away (the probe moved into hot water) restart it, so the reading does not crawl
// for minutes; a single one that far is taken for noise and dropped.
class Smoother {
public:
    static constexpr float kResetC = 5.0F;

    explicit Smoother(float alpha = 0.2F) : alpha_(alpha) {}

    void add(float sample) {
        if (std::isnan(sample)) return;
        if (has_ && std::fabs(sample - value_) > kResetC && ++far_ < 2) return;
        far_ = 0;
        if (!has_ || std::fabs(sample - value_) > kResetC) {
            value_ = sample;
            has_ = true;
            return;
        }
        value_ += alpha_ * (sample - value_);
    }

    void reset() {
        has_ = false;
        far_ = 0;
    }
    bool has() const { return has_; }
    float value() const { return has_ ? value_ : NAN; }

private:
    float alpha_;
    float value_ = 0;
    bool has_ = false;
    uint8_t far_ = 0;  // samples in a row far from the value
};

}  // namespace ntc
}  // namespace hornet
