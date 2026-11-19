#pragma once

// Low-voltage cutoff of a battery (config failsafe.lvd): the load is disconnected before the battery is drained so deep
// that it is ruined, and reconnected only when it has really recovered. It never depends on the hive or Wi-Fi.
//   connected, voltage < cutoffV for delayMs in a row        -> cut ("cut")
//   connected, no reading for lostAfterMs                    -> cut ("lost": a battery that is not measured is not safe)
//   cut, voltage >= restoreV for restoreDelayMs in a row     -> reconnected ("restored")
//   after boot, voltage >= restoreV for bootDelayMs in a row -> connected (the load is off at every boot, so a brown-out
//                                                              of the controller itself also leaves it off)
// restoreV is well above cutoffV: a battery that just lost its load springs back by volts, so only the charger being
// back (the voltage on charge) or a battery that has rested proves it is fine. Arduino-free, covered by native tests
// (test/test_lvd).

#include <cmath>
#include <cstdint>

namespace hornet {

class LowVoltageCutoff {
public:
    struct Config {
        float cutoffV = 10.5F;                 // 1.75 V per cell of a 12 V lead-acid battery, under load
        float restoreV = 12.4F;                // about 60% charged at rest, or the charger is back
        uint32_t delayMs = 30000;              // a dip of an inverter starting does not cut the load
        uint32_t restoreDelayMs = 120000;      // after a cut
        uint32_t bootDelayMs = 5000;           // after boot
        uint32_t lostAfterMs = 120000;         // no reading
    };

    enum class Event : uint8_t { None, Cut, Lost, Restored };

    // Keeps the limits sane: restoreV at least 0.3 V above cutoffV.
    void configure(Config c) {
        if (!(c.cutoffV > 0)) c.cutoffV = Config{}.cutoffV;
        if (!(c.restoreV > c.cutoffV + 0.3F)) c.restoreV = c.cutoffV + 0.3F;
        cfg_ = c;
    }

    const Config& config() const { return cfg_; }
    bool connected() const { return connected_; }

    // Back to the state after boot: load off.
    void restart() {
        connected_ = false;
        afterCut_ = false;
        started_ = false;
        timing_ = false;
    }

    // Call about once a second with the battery voltage (NAN: no reading).
    Event update(uint32_t nowMs, float volts) {
        if (!started_) {
            started_ = true;
            lastGoodMs_ = nowMs;
        }
        if (std::isnan(volts)) {
            timing_ = false;
            if (connected_ && nowMs - lastGoodMs_ >= cfg_.lostAfterMs) return cut(false);
            return Event::None;
        }
        lastGoodMs_ = nowMs;
        if (connected_) {
            if (!(volts < cfg_.cutoffV)) {
                timing_ = false;
                return Event::None;
            }
            if (!timing_) {
                timing_ = true;
                sinceMs_ = nowMs;
            }
            return nowMs - sinceMs_ >= cfg_.delayMs ? cut(true) : Event::None;
        }
        if (!(volts >= cfg_.restoreV)) {
            timing_ = false;
            return Event::None;
        }
        if (!timing_) {
            timing_ = true;
            sinceMs_ = nowMs;
        }
        const uint32_t need = afterCut_ ? cfg_.restoreDelayMs : cfg_.bootDelayMs;
        if (nowMs - sinceMs_ < need) return Event::None;
        connected_ = true;
        timing_ = false;
        return Event::Restored;
    }

private:
    Event cut(bool low) {
        connected_ = false;
        afterCut_ = true;
        timing_ = false;
        return low ? Event::Cut : Event::Lost;
    }

    Config cfg_;
    bool connected_ = false;
    bool afterCut_ = false;
    bool started_ = false;
    bool timing_ = false;
    uint32_t sinceMs_ = 0;
    uint32_t lastGoodMs_ = 0;
};

}  // namespace hornet
