#pragma once

// When a relay switched on must switch off by itself (contract cmd/relay "for_s", config relay.max_on_s):
// the shorter of the two, so the hornet never leaves a load on forever if the hive goes away. Arduino-free.

#include <cstdint>

namespace hornet {

class RelayTimer {
public:
    // forS: 0 = not given. maxOnS: 0 = no limit. Returns false when the relay should stay on indefinitely.
    void start(uint32_t nowMs, uint32_t forS, uint32_t maxOnS) {
        uint32_t limitS = forS;
        if (maxOnS > 0 && (limitS == 0 || maxOnS < limitS)) limitS = maxOnS;
        active_ = limitS > 0;
        startMs_ = nowMs;
        durationMs_ = limitS * 1000UL;
    }

    void stop() { active_ = false; }

    // True once the time is up (works across millis() wrap-around).
    bool expired(uint32_t nowMs) const { return active_ && nowMs - startMs_ >= durationMs_; }

    bool active() const { return active_; }

    uint32_t remainingS(uint32_t nowMs) const {
        if (!active_) return 0;
        const uint32_t elapsed = nowMs - startMs_;
        return elapsed >= durationMs_ ? 0 : (durationMs_ - elapsed) / 1000UL;
    }

private:
    bool active_ = false;
    uint32_t startMs_ = 0;
    uint32_t durationMs_ = 0;
};

}  // namespace hornet
