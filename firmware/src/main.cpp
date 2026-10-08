#include <HornetCore.h>

// Each env compiles exactly one role file, which defines hornetRole().
hornet::Role& hornetRole();

static hornet::Core core(hornetRole());

void setup() {
    core.begin();
}

void loop() {
    core.loop();
}
