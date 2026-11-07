#pragma once
// What the two halves of the CrowPanel 3.7" firmware share: the monitor of the hive (remote_crowpanel37.cpp: the screen, the
// buttons, sleep) and the book reader (reader_crowpanel37.cpp: books from the TF card, no Wi-Fi).
#include <Adafruit_GFX.h>
#include <Arduino.h>
#include <U8g2_for_Adafruit_GFX.h>
#include <driver/gpio.h>

namespace crowpanel37 {

constexpr int kWidth = 240;  // the panel's own size (portrait)
constexpr int kHeight = 416;

constexpr gpio_num_t kPinExit = GPIO_NUM_1;
constexpr gpio_num_t kPinMenu = GPIO_NUM_2;
constexpr gpio_num_t kPinDown = GPIO_NUM_4;
constexpr gpio_num_t kPinConf = GPIO_NUM_5;  // the rocker's press
constexpr gpio_num_t kPinUp = GPIO_NUM_6;
constexpr gpio_num_t kPinSdPower = GPIO_NUM_42;  // HIGH = the TF card powered (Elecrow's TF example)

// ---- given by the monitor (remote_crowpanel37.cpp)

/// The frame being drawn (1 bit a pixel, 1 = black) and the fonts drawing on it.
GFXcanvas1& canvas();
U8G2_FOR_ADAFRUIT_GFX& text();
/// Puts the canvas on the screen: a partial refresh from what is there, a full one now and then. <keepInFlash>: what is shown is
/// kept for the next partial refresh after deep sleep at once (else only when the panel goes to sleep).
void present(bool keepInFlash);
/// The next picture is drawn with a full refresh (a new screen: no ghosts of the old one).
void wipeNext();
/// The battery in millivolts, -1 without the divider; its charge in percent, -1 when not known.
int batteryMv();
int batteryPercent(int mv);
bool pressed(gpio_num_t pin);
/// "set debug 1" is on: the buttons log every accepted press.
bool debugButtons();
/// Held for 2 s from now (false: let go sooner).
bool held(gpio_num_t pin);
void waitRelease();
/// Rocker presses counted by interrupts meanwhile: down minus up, and whether it was pressed in.
void watchButtons();
int takeSteps();
bool takePress();
/// Deep sleep until a button (no timer), what is shown kept for the next partial refresh.
[[noreturn]] void sleepUntilButton();
/// Back to the hive's tabs: the shown tab is read anew (or the panel says it is not set up).
[[noreturn]] void backToMonitor();

// ---- given by the reader (reader_crowpanel37.cpp)

/// The panel was reading when it went to sleep (or lost power): it goes on reading.
bool readerOn();
/// The reader: <entering> by MENU held 2 s, else woken by <buttons> (the ext1 mask) while reading.
[[noreturn]] void runReader(uint64_t buttons, bool entering);

}  // namespace crowpanel37
