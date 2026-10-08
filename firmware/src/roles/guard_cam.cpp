// Role guard-cam (spec 5.4): PIR -> photo burst -> HTTP upload -> event/motion.
// Skeleton: only reads the PIR and logs. Camera, upload and SD buffer arrive in build step 9.

#include <HornetCore.h>

namespace {

constexpr int kPirPin = 13;

class GuardCam : public hornet::Role {
public:
    const char* type() const override { return "guard-cam"; }

    void setup() override {
        pinMode(kPirPin, INPUT);
    }

    void loop() override {
        bool motion = digitalRead(kPirPin) == HIGH;
        if (motion && !lastMotion_) {
            Serial.println("[guard-cam] motion");
        }
        lastMotion_ = motion;
    }

private:
    bool lastMotion_ = false;
};

GuardCam role;

}  // namespace

hornet::Role& hornetRole() { return role; }
