#pragma once
// Highlighting a row of a 1-bit screen (the file list and the settings of the book reader), kept free of Arduino so the host tests cover it.
//
// The row is drawn exactly like the others (same font, same ink) and then every pixel in its rectangle is flipped: black bar, white text.
// Nothing depends on how a font library treats a "white" text colour over a filled bar.

namespace hornet::reader {

/// Flips every pixel of the rectangle: <Canvas> needs getPixel(x, y) -> bool and drawPixel(x, y, colour) (Adafruit_GFX's GFXcanvas1).
/// Pixels outside the canvas are left alone.
template <class Canvas>
void invertRect(Canvas& canvas, int x, int y, int w, int h, int canvasWidth, int canvasHeight) {
    for (int py = y < 0 ? 0 : y; py < y + h && py < canvasHeight; py++)
        for (int px = x < 0 ? 0 : x; px < x + w && px < canvasWidth; px++)
            canvas.drawPixel(px, py, canvas.getPixel(px, py) ? 0 : 1);
}

}  // namespace hornet::reader
