#pragma once

// Arduino-free rules of the car (contracts/mqtt-topics.md "Car"): which pins it may drive, how a wheel speed becomes the
// two L298N inputs of a motor, and when the motors must stop by themselves. Covered by native tests: this is the car's
// failsafe.

#include <cstdint>
#include <cstdlib>

namespace hornet {

// The ESP32-CAM pins a car may use for its motors and light: the camera, PSRAM (16, 17) and the serial console (1, 3)
// take the others.
inline bool carPinAllowed(int pin) {
    switch (pin) {
        case 2: case 4: case 12: case 13: case 14: case 15:
            return true;
        default:
            return false;
    }
}

// LEDC of the car: the camera's clock (XCLK) is timer 0 / channel 0, set by the camera driver; the motor PWM is timer 1,
// channels 2..5, set by the car itself, never through Arduino's ledcAttach (which does not see the camera's timer).
constexpr int kCameraLedcTimer = 0;
constexpr int kCameraLedcChannel = 0;
constexpr int kMotorLedcTimer = 1;
constexpr int kMotorLedcChannel0 = 2;

constexpr bool carLedcApart() {
    if (kMotorLedcTimer == kCameraLedcTimer) return false;
    for (int i = 0; i < 4; ++i)
        if (kMotorLedcChannel0 + i == kCameraLedcChannel || kMotorLedcChannel0 + i > 7) return false;
    return true;
}

// Four motor pins and the light: all allowed, all different.
inline bool carPinsValid(const int motor[4], int light) {
    const int all[5] = {motor[0], motor[1], motor[2], motor[3], light};
    for (int i = 0; i < 5; ++i) {
        if (!carPinAllowed(all[i])) return false;
        for (int j = i + 1; j < 5; ++j)
            if (all[i] == all[j]) return false;
    }
    return true;
}

// How the motors are mounted and how fast they may go (config car.*, hw.motor_pwm).
struct CarMount {
    bool invertLeft = false;
    bool invertRight = false;
    bool swapSides = false;
    bool pwm = true;
    int maxSpeed = 100;  // percent
};

// Duty of the four inputs, 0..255: IN1, IN2 (the motor on IN1/IN2), IN3, IN4.
struct MotorDuty {
    uint8_t in[4] = {0, 0, 0, 0};
};

inline int clampSpeed(int v) { return v < -100 ? -100 : v > 100 ? 100 : v; }

// One motor: forward drives the first input, back the second, 0 leaves both low (the L298N lets the motor coast).
inline void motorDuty(int speed, bool invert, const CarMount& m, uint8_t& a, uint8_t& b) {
    speed = clampSpeed(speed);
    if (invert) speed = -speed;
    a = b = 0;
    if (speed == 0) return;
    const int maxSpeed = m.maxSpeed < 10 ? 10 : m.maxSpeed > 100 ? 100 : m.maxSpeed;
    const int duty = m.pwm ? (std::abs(speed) * maxSpeed * 255 + 5000) / 10000 : 255;
    const uint8_t d = static_cast<uint8_t>(duty < 1 ? 1 : duty > 255 ? 255 : duty);
    if (speed > 0) a = d; else b = d;
}

// Wheel speeds (-100..100, left and right) to the four inputs, with the mounting applied.
inline MotorDuty carDuty(int left, int right, const CarMount& m) {
    MotorDuty out;
    // IN1/IN2 is the left motor unless the sides are swapped.
    const int first = m.swapSides ? right : left;
    const int second = m.swapSides ? left : right;
    const bool invertFirst = m.swapSides ? m.invertRight : m.invertLeft;
    const bool invertSecond = m.swapSides ? m.invertLeft : m.invertRight;
    motorDuty(first, invertFirst, m, out.in[0], out.in[1]);
    motorDuty(second, invertSecond, m, out.in[2], out.in[3]);
    return out;
}

// When the camera's stream server may be started: not in setup (the network stack does not exist yet, a socket then
// asserts in lwIP), only once Wi-Fi is up; a failed start is tried again every 10 s.
inline bool carStreamMayStart(bool cameraOk, bool started, bool wifiUp, bool triedBefore, uint32_t nowMs, uint32_t lastTryMs) {
    if (!cameraOk || started || !wifiUp) return false;
    return !triedBefore || nowMs - lastTryMs >= 10000;
}

// Why the motors stopped last.
enum class CarStop { None, Boot, Deadman, Link, MaxRun, Hive };

inline const char* carStopName(CarStop s) {
    switch (s) {
        case CarStop::Boot: return "boot";
        case CarStop::Deadman: return "deadman";
        case CarStop::Link: return "link";
        case CarStop::MaxRun: return "max_run";
        case CarStop::Hive: return "hive";
        default: return "none";
    }
}

// The car's failsafe: a move lasts only while drive messages keep coming (deadman), while the link is up, and at most
// maxRunMs without a pause; after the max run it stays stopped until a {0, 0} comes. Times are millis(), wrap-safe.
class CarGuard {
public:
    static constexpr uint32_t kDeadmanMs = 500;
    static constexpr uint32_t kStaleMs = 1000;

    void setMaxRunMs(uint32_t ms) { maxRunMs_ = ms < 1000 ? 1000 : ms; }

    // A drive message. tsMs is its time, clockMs the hornet's (0 while not synced: not judged). False: ignored (stale,
    // or a move while the max run holds the car).
    bool drive(int left, int right, uint32_t nowMs, uint64_t tsMs = 0, uint64_t clockMs = 0) {
        if (clockMs != 0 && tsMs != 0 && clockMs > tsMs + kStaleMs) return false;
        left = clampSpeed(left);
        right = clampSpeed(right);
        if (left == 0 && right == 0) {
            latched_ = false;
            stop(CarStop::Hive);
            return true;
        }
        if (latched_) return false;
        if (!moving_) movingSince_ = nowMs;
        moving_ = true;
        left_ = left;
        right_ = right;
        lastDrive_ = nowMs;
        return true;
    }

    // Called every loop: stops the car when it must. Returns the reason when it stopped just now, None otherwise.
    CarStop tick(uint32_t nowMs, bool linkUp) {
        if (!moving_) return CarStop::None;
        if (!linkUp) return stop(CarStop::Link);
        if (nowMs - lastDrive_ >= kDeadmanMs) return stop(CarStop::Deadman);
        if (nowMs - movingSince_ >= maxRunMs_) {
            latched_ = true;
            return stop(CarStop::MaxRun);
        }
        return CarStop::None;
    }

    bool moving() const { return moving_; }
    int left() const { return moving_ ? left_ : 0; }
    int right() const { return moving_ ? right_ : 0; }
    CarStop lastStop() const { return lastStop_; }

private:
    CarStop stop(CarStop why) {
        moving_ = false;
        left_ = right_ = 0;
        lastStop_ = why;
        return why;
    }

    uint32_t maxRunMs_ = 60000;
    uint32_t lastDrive_ = 0;
    uint32_t movingSince_ = 0;
    int left_ = 0;
    int right_ = 0;
    bool moving_ = false;
    bool latched_ = false;
    CarStop lastStop_ = CarStop::Boot;
};

}  // namespace hornet
