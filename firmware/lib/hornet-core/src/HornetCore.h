#pragma once

// Hornet lifecycle: boot -> wifi -> time -> mqtt -> loop (spec 5.2).
// Skeleton only: Net, Mqtt, Config, Health, Log and Commands arrive in build step 9.

#include <Arduino.h>

#include "Topics.h"

namespace hornet {

// Implemented by each role in src/roles/<role>.cpp.
struct Role {
    virtual ~Role() = default;
    virtual const char* type() const = 0;
    virtual void setup() = 0;
    virtual void loop() = 0;
};

class Core {
public:
    explicit Core(Role& role) : role_(role) {}

    void begin();
    void loop();

private:
    Role& role_;
};

}  // namespace hornet
