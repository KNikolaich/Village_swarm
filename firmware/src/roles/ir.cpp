// Roles irrx and irtx: an infrared receiver or an infrared emitter on one pin (GPIO2 on the ESP-01 with the WS2812 connector).
// One source file, two builds (HORNET_IR_MODE 1 = receiver, 2 = emitter), because one pin serves one of them.
//   irrx: the last code seen is kept in the retained state ir_last = "NEC 32 0x02FD807F" (repeats and codes it cannot name are ignored),
//         so the hive can learn a remote's buttons from it.
//   irtx: cmd/ir {protocol, bits, data, repeat?} sends one code. Infrared is one way: ok means "sent", not "obeyed".
// The remote's buttons live in the hive (config ir.buttons); the hornet does not read them.

#include <HornetCore.h>
#include <IRrecv.h>
#include <IRremoteESP8266.h>
#include <IRsend.h>
#include <IRutils.h>
#include <IrCode.h>

#include <memory>

#ifndef HORNET_IR_MODE
#define HORNET_IR_MODE 1
#endif
#ifndef HW_IR_PIN
#define HW_IR_PIN 2
#endif

namespace {

using namespace hornet;

constexpr bool kReceiver = HORNET_IR_MODE == 1;
// A remote held down repeats its code many times a second; the hive needs it once.
constexpr uint32_t kSameCodeQuietMs = 400;
// The longest code of the protocols this firmware knows is well within this many marks and spaces.
constexpr uint16_t kCaptureBuffer = 256;
constexpr uint8_t kMaxRepeat = 10;

class Ir : public Role {
public:
    const char* type() const override { return kReceiver ? "irrx" : "irtx"; }
    const char* hw() const override { return HORNET_BOARD; }
    std::vector<const char*> caps() const override { return {kReceiver ? "ir.rx" : "ir.tx"}; }

    void setup(Core& core) override {
        core_ = &core;
        configurePin(HW_IR_PIN);
        if (!kReceiver) core.onCommand("ir", [this](JsonObjectConst p) { return send(p); });
    }

    void applyConfig(Core&, JsonObjectConst config) override { configurePin(config["hw"]["ir_pin"] | HW_IR_PIN); }

    void loop(Core& core) override {
        if (!kReceiver || !rx_) return;
        decode_results results;
        if (!rx_->decode(&results)) return;
        // A repeat of a held button has no bits of its own; a code that is not one of ours is of no use to the hive.
        if (results.decode_type == decode_type_t::UNKNOWN || results.bits == 0 || results.bits > 64) {
            rx_->resume();
            return;
        }
        const String name = typeToString(results.decode_type);
        const std::string text = ir::describe(name.c_str(), results.bits, results.value);
        rx_->resume();
        if (text == lastText_ && millis() - lastAtMs_ < kSameCodeQuietMs) return;
        lastText_ = text;
        lastAtMs_ = millis();
        JsonDocument state;
        state["ir_last"] = text.c_str();
        core.publishState(state);
        core.log(LogLevel::Info, String("ir: ") + text.c_str());
    }

private:
    void configurePin(int pin) {
        if (pin == pin_ && (rx_ || tx_)) return;
        pin_ = pin;
        rx_.reset();
        tx_.reset();
        if (pin_ < 0) return;
        if (kReceiver) {
            rx_.reset(new IRrecv(pin_, kCaptureBuffer, 15, true));
            rx_->enableIRIn();
        } else {
            tx_.reset(new IRsend(pin_));
            tx_->begin();
        }
    }

    AckResult send(JsonObjectConst p) {
        if (!tx_) return AckResult::error("no pin for the emitter (hw.ir_pin)");
        const char* protocol = p["protocol"] | "";
        const int bits = p["bits"] | 0;
        const char* data = p["data"] | "";
        uint64_t value = 0;
        if (!ir::validProtocolName(protocol) || !ir::validBits(bits) || !ir::parseHex(data, value)) return AckResult::error("bad infrared code");
        const decode_type_t type = strToDecodeType(protocol);
        if (type == decode_type_t::UNKNOWN) return AckResult::error(String("unknown protocol ") + protocol);
        uint32_t repeat = p["repeat"] | 0U;
        if (repeat > kMaxRepeat) repeat = kMaxRepeat;
        if (!tx_->send(type, value, static_cast<uint16_t>(bits), static_cast<uint16_t>(repeat)))
            return AckResult::error(String("protocol ") + protocol + " is not in this firmware or has other bits");
        core_->log(LogLevel::Info, String("ir sent: ") + protocol + " " + bits + " " + data);
        return AckResult::ok();
    }

    Core* core_ = nullptr;
    int pin_ = -2;
    std::unique_ptr<IRrecv> rx_;
    std::unique_ptr<IRsend> tx_;
    std::string lastText_;
    uint32_t lastAtMs_ = 0;
};

Ir role;

}  // namespace

hornet::Role& hornetRole() { return role; }
