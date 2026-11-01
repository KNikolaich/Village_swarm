#pragma once

// Placed screen layout (contracts/mqtt-topics.md, vs/v1/hive/display/{id}): an item with "size" 1-10 is drawn at x, y with
// a font about size/10 of the screen height, so "5" looks the same on a 0.42" OLED and on a 4" panel. Arduino-free.

#include <cstdint>
#include <cstring>

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

// Where a reading stands against the normal range set for it in the hive (display items[].range). The site paints the
// number pink above and light blue below; the monochrome OLED draws it black on a white plate with an arrow up or down.
enum class Mark : uint8_t { None, High, Low };

inline Mark parseMark(const char* s) {
    if (s == nullptr) return Mark::None;
    if (std::strcmp(s, "high") == 0) return Mark::High;
    if (std::strcmp(s, "low") == 0) return Mark::Low;
    return Mark::None;
}

struct Box {
    int x;
    int y;
    int w;
    int h;
};

// The plate or frame around a text whose top-left corner is (left, top): 1 px of room on each side, kept on the screen.
inline Box markBox(int left, int top, int width, int height, int screenWidth, int screenHeight) {
    int x0 = left - 1, y0 = top - 1, x1 = left + width + 1, y1 = top + height + 1;
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 > screenWidth) x1 = screenWidth;
    if (y1 > screenHeight) y1 = screenHeight;
    return {x0, y0, x1 > x0 ? x1 - x0 : 0, y1 > y0 ? y1 - y0 : 0};
}

// Width of the arrow drawn after a marked value, the gap before it included; it grows with the font (ascent: the
// height of the digits). Nothing for an unmarked value.
inline int arrowWidth(Mark mark, int ascent) {
    if (mark == Mark::None) return 0;
    return (ascent >= 16 ? 7 : 5) + 1;
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
