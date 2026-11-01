// Role monitor: a screen that shows what the hive sends it (docs/display.md). It measures nothing of its own: the hive
// gathers the readings chosen in config display.items (any device of the hive) and publishes the ready texts to
// vs/v1/hive/display/{id}; this role only draws them. With hw.vbat_pin set, a battery behind a voltage divider is
// measured as battery_voltage (V). With hw.ntc_pin set, an NTC thermistor behind a divider (GPIO0 = A0 on the 0.42"
// board) is measured as temperature_ntc (C), smoothed (Ntc.h, docs/ntc-thermistor.md); the hive shows it like any
// reading, on this screen too when display.items asks for it.
//
// Layout: without "size" on any item the role lays out up to four lines itself; with it every item is drawn where x, y
// say, in the largest font not taller than size/10 of the screen height (DisplayLayout.h), its label in the smallest font
// above it.
//
// A reading outside the normal range set for it in the hive (display items[].range; pink or light blue on the site) is
// drawn black on a white plate with an arrow: up when above the range, down when below it.
//
// Board: ESP32-C3 with the 0.42" SSD1306 OLED (72x40) on I2C (monitor-c3oled, GPIO5/GPIO6). A dot in the top right
// corner shows that the monitor is online.

#include <Battery.h>
#include <DisplayLayout.h>
#include <HornetCore.h>
#include <Ntc.h>
#include <U8g2lib.h>
#include <Wire.h>

namespace {

using namespace hornet;

constexpr int kWidth = 72;
constexpr int kHeight = 40;

// Fonts for placed items, smallest first: Cyrillic for small texts and labels, Logisoso digits from 16 px up.
const uint8_t* const kFonts[] = {
    u8g2_font_5x8_t_cyrillic,  u8g2_font_6x12_t_cyrillic, u8g2_font_9x15_t_cyrillic, u8g2_font_10x20_t_cyrillic,
    u8g2_font_logisoso16_tf,   u8g2_font_logisoso20_tf,   u8g2_font_logisoso24_tf,   u8g2_font_logisoso28_tf,
    u8g2_font_logisoso32_tf,   u8g2_font_logisoso38_tf,
};
constexpr int kFontCount = sizeof(kFonts) / sizeof(kFonts[0]);

class Monitor : public Role {
public:
    const char* type() const override { return "monitor"; }
    const char* hw() const override { return HORNET_BOARD; }

    std::vector<const char*> caps() const override { return {"display.oled"}; }

    std::vector<const char*> metrics() const override {
        std::vector<const char*> m;
        if (battery_.enabled()) m.push_back("battery_voltage");
        if (ntc_.enabled()) m.push_back("temperature_ntc");
        return m;
    }

    void setup(Core&) override { initScreen(); }

    void applyConfig(Core& core, JsonObjectConst config) override {
        teleIntervalMs_ = (config["tele_interval_s"] | 60U) * 1000UL;
        JsonObjectConst hw = config["hw"];
        const bool hadBattery = battery_.enabled();
        const bool hadNtc = ntc_.enabled();
        battery_.configure(hw);
        if (ntc_.configure(hw)) ntcSent_ = false;  // new settings: the next good reading goes out at once
        const int sda = hw["sda_pin"] | HW_SDA_PIN;
        const int scl = hw["scl_pin"] | HW_SCL_PIN;
        if (sda != sdaPin_ || scl != sclPin_) {
            sdaPin_ = sda;
            sclPin_ = scl;
            initScreen();
        }
        if (battery_.enabled() != hadBattery || ntc_.enabled() != hadNtc) core.refreshInfo();  // metrics follow the battery and the NTC
    }

    void loop(Core& core) override {
        const uint32_t now = millis();
        if (battery_.enabled() && core.online() && (!sent_ || now - lastBatteryMs_ >= teleIntervalMs_)) {
            sent_ = true;
            lastBatteryMs_ = now;
            core.publishTele("battery_voltage", battery_.read(), "V");
        }
        ntc_.poll(now);
        if (ntc_.enabled() && core.online() && (!ntcSent_ || now - lastNtcMs_ >= teleIntervalMs_)) {
            const float t = ntc_.value();
            if (!isnan(t)) {
                ntcSent_ = true;
                lastNtcMs_ = now;
                core.publishTele("temperature_ntc", t, "C");
                ntcWarned_ = false;
            } else if (ntc_.failing() && !ntcWarned_) {
                ntcWarned_ = true;
                core.log(LogLevel::Warn, "NTC read failed: open or shorted divider");
            }
        }
        // Redraw on new texts and when the online dot changes; nothing else moves on the screen.
        const bool online = core.online();
        if (dirty_ || online != drawnOnline_) {
            dirty_ = false;
            drawnOnline_ = online;
            draw(online);
        }
    }

    bool wantsDisplay() const override { return true; }

    void applyDisplay(Core&, JsonObjectConst message) override {
        rows_.clear();
        placed_ = false;
        for (JsonObjectConst item : message["items"].as<JsonArrayConst>())
            if (!item["size"].isNull()) placed_ = true;
        for (JsonObjectConst item : message["items"].as<JsonArrayConst>()) {
            if (rows_.size() >= (placed_ ? 8U : 4U)) break;
            rows_.push_back({String(item["label"] | ""), String(item["text"] | "--"), item["x"] | 0, item["y"] | 0,
                             item["size"] | 3, display::parseAlign(item["align"] | "left"),
                             display::parseMark(item["range"].as<const char*>())});
        }
        received_ = true;
        dirty_ = true;
    }

private:
    void initScreen() {
        if (sdaPin_ < 0 || sclPin_ < 0) {
            sdaPin_ = HW_SDA_PIN;
            sclPin_ = HW_SCL_PIN;
        }
        Wire.begin(sdaPin_, sclPin_);
        screenOk_ = oled_.begin();
        oled_.setFontMode(1);  // transparent: a text drawn in black on a white plate leaves the plate around the glyphs
        if (!screenOk_) Serial.printf("[monitor] no screen on i2c %d/%d\n", sdaPin_, sclPin_);
        // The height of each font is what its digits take from the top: measured, not guessed.
        for (int i = 0; i < kFontCount; ++i) {
            oled_.setFont(kFonts[i]);
            fontHeights_[i] = oled_.getAscent();
        }
        dirty_ = true;
    }

    // Each item where the hive placed it: the label small at y, the value right under it.
    void drawPlaced() {
        oled_.setFontPosTop();
        for (const auto& row : rows_) {
            int y = row.y;
            if (row.label.length()) {
                oled_.setFont(kFonts[0]);
                oled_.drawUTF8(display::leftEdge(row.x, oled_.getUTF8Width(row.label.c_str()), row.align), y, row.label.c_str());
                y += fontHeights_[0] + 2;
            }
            const int font = display::pickFont(fontHeights_, kFontCount, display::pixelHeight(row.size, kHeight));
            oled_.setFont(kFonts[font]);
            drawValue(display::leftEdge(row.x, valueWidth(row.text.c_str(), row.mark), row.align), y, true, row.text.c_str(), row.mark);
        }
        oled_.setFontPosBaseline();
    }

    // 72x40: one line is a small label above a large text; two are two large texts; three or four are small rows, label on
    // the left and text on the right. Before the hive has said anything: the name of the system; with nothing chosen: a hint.
    void draw(bool online) {
        if (!screenOk_) return;
        oled_.clearBuffer();
        const size_t n = rows_.size();
        if (placed_ && n > 0) {
            drawPlaced();
        } else if (n == 0) {
            oled_.setFont(u8g2_font_5x8_t_cyrillic);
            if (received_) {
                oled_.drawUTF8(0, 16, "Нет строк:");
                oled_.drawUTF8(0, 28, "настройте");
                oled_.drawUTF8(0, 38, "в Улье");
            } else {
                oled_.drawUTF8(0, 16, "Village");
                oled_.drawUTF8(0, 28, "Swarm");
            }
        } else if (n <= 2) {
            int y = n == 1 ? 38 : 18;
            for (const auto& row : rows_) {
                if (n == 1 && row.label.length()) {
                    oled_.setFont(u8g2_font_5x8_t_cyrillic);
                    oled_.drawUTF8(0, 8, row.label.c_str());
                }
                oled_.setFont(u8g2_font_logisoso16_tf);
                drawValue(row.mark == display::Mark::None ? 0 : 1, y, false, row.text.c_str(), row.mark);  // marked: room for the plate
                y += 20;
            }
        } else {
            oled_.setFont(u8g2_font_5x8_t_cyrillic);
            int y = 9;
            for (const auto& row : rows_) {
                String label = row.label;
                // 14 characters of 5 px across: the text keeps its place, the label gives way
                const int room = 14 - static_cast<int>(row.text.length()) - 1 - (row.mark == display::Mark::None ? 0 : 2);
                if (room <= 0) label = "";
                else if (static_cast<int>(label.length()) > room) label = label.substring(0, room);
                oled_.drawUTF8(0, y, label.c_str());
                drawValue(kWidth - (row.mark == display::Mark::None ? 0 : 1) - valueWidth(row.text.c_str(), row.mark), y, false,
                          row.text.c_str(), row.mark);
                y += 10;
            }
        }
        if (online) oled_.drawDisc(kWidth - 3, 2, 2);
        oled_.sendBuffer();
    }

    // Width of a value in the current font, its arrow included.
    int valueWidth(const char* text, display::Mark mark) {
        return oled_.getUTF8Width(text) + display::arrowWidth(mark, oled_.getAscent());
    }

    // A value in the current font at left, y (y: the top of the text when top is true, else its baseline). Outside its
    // range: black on a white plate, with an arrow after it pointing up (above the range) or down (below it).
    void drawValue(int left, int y, bool top, const char* text, display::Mark mark) {
        if (mark == display::Mark::None) {
            oled_.drawUTF8(left, y, text);
            return;
        }
        const int ascent = oled_.getAscent();
        const int textTop = top ? y : y - ascent;
        const int textWidth = oled_.getUTF8Width(text);
        const int arrow = display::arrowWidth(mark, ascent) - 1;  // without the gap
        const display::Box b = display::markBox(left, textTop, textWidth + 1 + arrow, ascent, kWidth, kHeight);
        oled_.drawBox(b.x, b.y, b.w, b.h);
        oled_.setDrawColor(0);
        oled_.drawUTF8(left, y, text);
        // The arrow: a triangle as wide as `arrow`, half as tall plus one, in the middle of the digits' height.
        const int ax = left + textWidth + 1;
        const int half = arrow / 2;
        const int h = half + 1;
        const int cy = textTop + ascent / 2;
        if (mark == display::Mark::High) oled_.drawTriangle(ax, cy + h / 2, ax + arrow, cy + h / 2, ax + half, cy - h / 2 - 1);
        else oled_.drawTriangle(ax, cy - h / 2, ax + arrow, cy - h / 2, ax + half, cy + h / 2 + 1);
        oled_.setDrawColor(1);
    }

    struct Row {
        String label;
        String text;
        int x;
        int y;
        int size;
        display::Align align;
        display::Mark mark;
    };
    std::vector<Row> rows_;  // the hive's display message
    bool placed_ = false;    // the items carry x, y, size
    int fontHeights_[kFontCount] = {};
    bool received_ = false;  // the hive has sent a message since boot (retained: right after the connection)
    bool dirty_ = true;
    bool drawnOnline_ = false;
    bool screenOk_ = false;
    int sdaPin_ = -1;
    int sclPin_ = -1;
    Battery battery_;
    uint32_t teleIntervalMs_ = 60000;
    uint32_t lastBatteryMs_ = 0;
    bool sent_ = false;
    NtcSensor ntc_;
    uint32_t lastNtcMs_ = 0;
    bool ntcSent_ = false;
    bool ntcWarned_ = false;
    // SSD1306 72x40 on the ESP32-C3 0.42" board, hardware I2C.
    U8G2_SSD1306_72X40_ER_F_HW_I2C oled_{U8G2_R0, U8X8_PIN_NONE};
};

Monitor role;

}  // namespace

hornet::Role& hornetRole() { return role; }
