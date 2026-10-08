#include "HornetCore.h"

namespace hornet {

void Core::begin() {
    Serial.begin(115200);
    Serial.printf("[hornet] boot, role=%s fw=%s\n", role_.type(), HORNET_FW_VERSION);
    role_.setup();
}

void Core::loop() {
    role_.loop();
}

}  // namespace hornet
