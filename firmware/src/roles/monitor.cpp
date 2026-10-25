// Role monitor: a screen that shows what the hive sends it (docs/display.md). It measures nothing of its own: the hive
// gathers the readings chosen in config display.items (any device of the hive) and publishes the ready texts to
// vs/v1/hive/display/{id}; this role only draws them. With hw.vbat_pin set, a battery behind a voltage divider is
// measured as battery_voltage (V).
//
// Board: ESP32-C3 with the 0.42" SSD1306 OLED (72x40) on I2C (monitor-c3oled, GPIO5/GPIO6). A dot in the top right
// corner shows that the monitor is online.

#include <Battery.h>
#include <HornetCore.h>
#include <U8g2lib.h>
#include <Wire.h>

namespace {

using namespace hornet;

class Monitor : public Role {
public:
    const char* type() const override { return "monitor"; }
    const char* hw() const override { return HORNET_BOARD; }

    std::vector<const char*> caps() const override { return {"display.oled"}; }

    std::vector<const char*> metrics() const override {
        if (battery_.enabled()) return {"battery_voltage"};
        return {};
    }

    void setup(Core&) override { initScreen(); }

    void applyConfig(Core& core, JsonObjectConst config) override {
        teleIntervalMs_ = (config["tele_interval_s"] | 60U) * 1000UL;
        JsonObjectConst hw = config["hw"];
        const bool hadBattery = battery_.enabled();
        battery_.configure(hw);
        const int sda = hw["sda_pin"] | HW_SDA_PIN;
        const int scl = hw["scl_pin"] | HW_SCL_PIN;
        if (sda != sdaPin_ || scl != sclPin_) {
            sdaPin_ = sda;
            sclPin_ = scl;
            initScreen();
        }
        if (battery_.enabled() != hadBattery) core.refreshInfo();  // metrics follow the battery
    }

    void loop(Core& core) override {
        const uint32_t now = millis();
        if (battery_.enabled() && core.online() && (!sent_ || now - lastBatteryMs_ >= teleIntervalMs_)) {
            sent_ = true;
            lastBatteryMs_ = now;
            core.publishTele("battery_voltage", battery_.read(), "V");
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
        for (JsonObjectConst item : message["items"].as<JsonArrayConst>()) {
            if (rows_.size() >= 4) break;
            rows_.push_back({String(item["label"] | ""), String(item["text"] | "--")});
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
        if (!screenOk_) Serial.printf("[monitor] no screen on i2c %d/%d\n", sdaPin_, sclPin_);
        dirty_ = true;
    }

    // 72x40: one line is a small label above a large text; two are two large texts; three or four are small rows, label on
    // the left and text on the right. Before the hive has said anything: the name of the system; with nothing chosen: a hint.
    void draw(bool online) {
        if (!screenOk_) return;
        oled_.clearBuffer();
        const size_t n = rows_.size();
        if (n == 0) {
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
                oled_.drawUTF8(0, y, row.text.c_str());
                y += 20;
            }
        } else {
            oled_.setFont(u8g2_font_5x8_t_cyrillic);
            int y = 9;
            for (const auto& row : rows_) {
                String label = row.label;
                // 14 characters of 5 px across: the text keeps its place, the label gives way
                const int room = 14 - static_cast<int>(row.text.length()) - 1;
                if (room <= 0) label = "";
                else if (static_cast<int>(label.length()) > room) label = label.substring(0, room);
                oled_.drawUTF8(0, y, label.c_str());
                oled_.drawUTF8(72 - oled_.getUTF8Width(row.text.c_str()), y, row.text.c_str());
                y += 10;
            }
        }
        if (online) oled_.drawDisc(69, 2, 2);
        oled_.sendBuffer();
    }

    struct Row {
        String label;
        String text;
    };
    std::vector<Row> rows_;  // the hive's display message
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
    // SSD1306 72x40 on the ESP32-C3 0.42" board, hardware I2C.
    U8G2_SSD1306_72X40_ER_F_HW_I2C oled_{U8G2_R0, U8X8_PIN_NONE};
};

Monitor role;

}  // namespace

hornet::Role& hornetRole() { return role; }
