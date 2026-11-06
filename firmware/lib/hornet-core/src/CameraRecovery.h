#pragma once

// Arduino-free rules for a guard camera that hands out no frame (esp_camera_fb_get gave NULL): the driver and the
// sensor are restarted at once on the first failure, then at most once per `minGapMs` while the failures go on,
// so a dead sensor is not re-initialised on every frame of every burst.

#include <cstdint>

namespace hornet {

class CameraRecovery {
public:
    explicit CameraRecovery(uint32_t minGapMs = 60000) : minGapMs_(minGapMs) {}

    // A frame failed at `nowMs`; true when the camera should be restarted now.
    bool failed(uint32_t nowMs) {
        ++failures_;
        if (restarted_ && nowMs - lastRestartMs_ < minGapMs_) return false;
        restarted_ = true;
        lastRestartMs_ = nowMs;
        return true;
    }

    // A frame came: the next failure restarts the camera at once again.
    void succeeded() {
        failures_ = 0;
        restarted_ = false;
    }

    // Failed frames in a row.
    int failures() const { return failures_; }

private:
    uint32_t minGapMs_;
    uint32_t lastRestartMs_ = 0;
    bool restarted_ = false;
    int failures_ = 0;
};

}  // namespace hornet
