#pragma once

// Local heater safety that never depends on the hive (spec 5.6). A port of the simulator's reference model
// (backend/tools/Hive.Simulator/Roles/HeaterFailsafe.cs), kept the same so the hive sees the same behaviour:
//   t < min_c                 -> on whatever the commands, until min_c + hysteresis ("min_c")
//   t > max_c                 -> off, the command is dropped; "on" is rejected while t >= max_c ("max_c")
//   on for max_on_s           -> off and a short rest, whatever switched it on ("max_on_s")
//   no reading for lostAfter  -> antifreeze cycle 10 min on / 20 min off, "on" rejected ("sensor_lost")
//   otherwise                 -> what the last command said, for at most min(for_s, max_on_s)
// "off" is rejected below min_c. The hive can narrow the limits through config failsafe.heater, not switch them off.
// Arduino-free, covered by native tests (test/test_heater).

#include <cmath>
#include <cstdint>

namespace hornet {

class HeaterFailsafe {
public:
    static constexpr float kHysteresisC = 1.0F;
    static constexpr uint32_t kRestMs = 60000;
    static constexpr uint32_t kSafeOnMs = 10 * 60000UL;
    static constexpr uint32_t kSafeCycleMs = 30 * 60000UL;

    void configure(float minC, float maxC, uint32_t maxOnS, uint32_t lostAfterMs) {
        minC_ = minC;
        maxC_ = maxC;
        maxOnMs_ = (maxOnS > 0 ? maxOnS : 1) * 1000UL;
        lostAfterMs_ = lostAfterMs;
    }

    float minC() const { return minC_; }
    float maxC() const { return maxC_; }
    uint32_t maxOnS() const { return maxOnMs_ / 1000UL; }

    // A sensor reading; NAN when the read failed (does not count as a reading).
    void reading(uint32_t nowMs, float t) {
        if (std::isnan(t)) return;
        lastT_ = t;
        lastReadingMs_ = nowMs;
        haveReading_ = true;
    }

    // The temperature the rules use; NAN before the first reading and while the sensor is lost.
    float temperature(uint32_t nowMs) const { return sensorLost(nowMs) ? NAN : lastT_; }

    bool sensorLost(uint32_t nowMs) const {
        const uint32_t since = haveReading_ ? lastReadingMs_ : startMs_;
        return started_ && nowMs - since >= lostAfterMs_;
    }

    // cmd/relay. Returns nullptr when accepted, else why it is rejected (ack "rejected", spec 4.4).
    const char* command(bool on, uint32_t forS, uint32_t nowMs) {
        start(nowMs);
        const float t = temperature(nowMs);
        if (on) {
            if (sensorLost(nowMs)) return "failsafe: temperature sensor lost";
            if (!std::isnan(t) && t >= maxC_) return "failsafe: max_c exceeded";
            if (resting(nowMs)) return "failsafe: resting after max_on_s";
            commandedOn_ = true;
            commandStartMs_ = nowMs;
            const uint32_t forMs = forS * 1000UL;
            commandMs_ = forS > 0 && forMs < maxOnMs_ ? forMs : maxOnMs_;
        } else {
            if (!std::isnan(t) && t < minC_) return "failsafe: below min_c";
            commandedOn_ = false;
        }
        reason_ = nullptr;
        set(on, nowMs, nullptr);
        return nullptr;
    }

    // Re-evaluates the rules; call every loop. Returns true when the heater switched.
    bool evaluate(uint32_t nowMs) {
        start(nowMs);
        const bool was = on_;
        if (on_ && nowMs - onSinceMs_ >= maxOnMs_) {
            commandedOn_ = false;
            forced_ = false;
            restUntilMs_ = nowMs + kRestMs;
            restActive_ = true;
            set(false, nowMs, "max_on_s");
            return was != on_;
        }
        if (commandedOn_ && nowMs - commandStartMs_ >= commandMs_) commandedOn_ = false;  // for_s elapsed

        if (sensorLost(nowMs)) {
            if (!lost_) {
                lost_ = true;
                lostSinceMs_ = nowMs;
            }
            const bool inOn = (nowMs - lostSinceMs_) % kSafeCycleMs < kSafeOnMs;
            set(inOn && !resting(nowMs), nowMs, "sensor_lost");
            return was != on_;
        }
        lost_ = false;

        const float t = lastT_;
        if (!haveReading_) {
            set(commandedOn_, nowMs, nullptr);  // first minutes after boot, nothing read yet
        } else if (t > maxC_) {
            commandedOn_ = false;
            forced_ = false;
            set(false, nowMs, "max_c");
        } else if (!resting(nowMs) && (t < minC_ || (forced_ && t < minC_ + kHysteresisC))) {
            forced_ = true;
            set(true, nowMs, "min_c");
        } else {
            forced_ = false;
            set(commandedOn_, nowMs, commandedOn_ ? nullptr : "idle");
        }
        return was != on_;
    }

    bool isOn() const { return on_; }

    // Why the last automatic switch happened: min_c, max_c, max_on_s, sensor_lost, idle; nullptr after a command.
    const char* lastReason() const { return reason_; }

private:
    void start(uint32_t nowMs) {
        if (started_) return;
        started_ = true;
        startMs_ = nowMs;
    }

    bool resting(uint32_t nowMs) {
        if (restActive_ && static_cast<int32_t>(nowMs - restUntilMs_) >= 0) restActive_ = false;
        return restActive_;
    }

    void set(bool on, uint32_t nowMs, const char* reason) {
        if (on == on_) return;
        on_ = on;
        if (on) onSinceMs_ = nowMs;
        if (reason) reason_ = reason;
    }

    float minC_ = 5.0F;
    float maxC_ = 25.0F;
    uint32_t maxOnMs_ = 21600000UL;
    uint32_t lostAfterMs_ = 180000;

    bool started_ = false;
    uint32_t startMs_ = 0;
    bool haveReading_ = false;
    float lastT_ = NAN;
    uint32_t lastReadingMs_ = 0;

    bool on_ = false;
    uint32_t onSinceMs_ = 0;
    bool commandedOn_ = false;
    uint32_t commandStartMs_ = 0;
    uint32_t commandMs_ = 0;
    bool forced_ = false;
    bool restActive_ = false;
    uint32_t restUntilMs_ = 0;
    bool lost_ = false;
    uint32_t lostSinceMs_ = 0;
    const char* reason_ = nullptr;
};

}  // namespace hornet
