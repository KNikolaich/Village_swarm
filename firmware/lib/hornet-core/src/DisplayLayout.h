#pragma once

// Placed screen layout (contracts/mqtt-topics.md, vs/v1/hive/display/{id}): an item with "size" 1-10 is drawn at x, y with
// a font about size/10 of the screen height, so "5" looks the same on a 0.42" OLED and on a 4" panel. Arduino-free.

#include <cstdint>

namespace hornet {
namespace display {

enum class Align : uint8_t { Left, Center, Right };

// Text height in pixels for a relative size: size/10 of the screen height (rounded), at least 6 px. Same as the hive's
// DisplayText.PixelHeight, which the editor previews with.
inline int pixelHeight(int size, int screenHeight) {
    if (size < 1) size = 1;
    if (size > 10) size = 10;
    const int px = (screenHeight * size * 2 + 10) / 20;  // round half up
    return px < 6 ? 6 : px;
}

// The font to use: the largest of heights[] (ascending) not taller than target; the smallest when none fits.
inline int pickFont(const int* heights, int count, int target) {
    int best = 0;
    for (int i = 0; i < count; ++i)
        if (heights[i] <= target) best = i;
    return best;
}

inline Align parseAlign(const char* s) {
    if (s == nullptr) return Align::Left;
    if (s[0] == 'c') return Align::Center;
    if (s[0] == 'r') return Align::Right;
    return Align::Left;
}

// Left edge of a text of the given width whose anchor is x.
inline int leftEdge(int x, int width, Align align) {
    switch (align) {
        case Align::Center: return x - width / 2;
        case Align::Right: return x - width;
        default: return x;
    }
}

}  // namespace display
}  // namespace hornet
