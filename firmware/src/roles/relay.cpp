// Role relay: one relay (light, socket, pump) on hw.relay_pin, optionally a local button on hw.button_pin.
//   cmd/relay {channel, set: on|off, for_s}  ->  ack with state, retained state/{channel}
// Off at every boot. Switches off by itself after for_s or config relay.max_on_s, whichever is shorter,
// so a lost hive never leaves a load on. Heaters with temperature failsafe are a separate role (spec 5.6).

#include <HornetCore.h>
#include <RelayTimer.h>

namespace {

using namespace hornet;

class Relay : public Role {
public:
    const char* type() const override { return "relay"; }
    const char* hw() const override { return HORNET_BOARD; }
    std::vector<const char*> caps() const override {
        if (buttonPin_ >= 0) return {"relay", "button"};
        return {"relay"};
    }

    void setup(Core& core) override {
        core_ = &core;
        configurePins(HW_RELAY_PIN, HW_RELAY_ACTIVE_LOW != 0, HW_BUTTON_PIN);
        core.onCommand("relay", [this](JsonObjectConst p) { return command(p); });
    }

    void applyConfig(Core& core, JsonObjectConst config) override {
        JsonObjectConst hw = config["hw"];
        configurePins(hw["relay_pin"] | HW_RELAY_PIN, hw["relay_active_low"] | (HW_RELAY_ACTIVE_LOW != 0), hw["button_pin"] | HW_BUTTON_PIN);
        JsonObjectConst relay = config["relay"];
        const char* ch = relay["channel"] | "relay";
        if (topics::isValidSegment(ch)) channel_ = ch;
        maxOnS_ = relay["max_on_s"] | 0U;
        publishState(core);
    }

    void loop(Core& core) override {
        if (on_ && timer_.expired(millis())) {
            set(false);
            publishState(core);
            core.log(LogLevel::Info, "relay off: time limit");
        }
        if (buttonPin_ >= 0) {
            const bool pressed = digitalRead(buttonPin_) == LOW;
            if (pressed && !buttonDown_ && millis() - lastButtonMs_ > 300) {
                lastButtonMs_ = millis();
                set(!on_);
                if (on_) timer_.start(millis(), 0, maxOnS_);
                publishState(core);
            }
            buttonDown_ = pressed;
        }
        if (!statePublished_ && core.online()) publishState(core);
    }

private:
    void configurePins(int relayPin, bool activeLow, int buttonPin) {
        if (relayPin != relayPin_ || activeLow != activeLow_) {
            relayPin_ = relayPin;
            activeLow_ = activeLow;
            pinMode(relayPin_, OUTPUT);
            set(on_);
        }
        if (buttonPin != buttonPin_) {
            buttonPin_ = buttonPin;
            if (buttonPin_ >= 0) pinMode(buttonPin_, INPUT_PULLUP);
        }
    }

    AckResult command(JsonObjectConst p) {
        const char* ch = p["channel"] | "";
        if (channel_ != ch) return AckResult::error("unknown channel " + String(ch));
        const String set = p["set"] | "";
        if (set != "on" && set != "off") return AckResult::error("set must be on or off");
        this->set(set == "on");
        if (on_) timer_.start(millis(), p["for_s"] | 0U, maxOnS_);
        else timer_.stop();
        publishState(*core_);
        AckResult r;
        r.state[channel_] = on_ ? "on" : "off";
        return r;
    }

    void set(bool on) {
        on_ = on;
        if (!on) timer_.stop();
        if (relayPin_ >= 0) digitalWrite(relayPin_, on != activeLow_ ? HIGH : LOW);
    }

    void publishState(Core& core) {
        JsonDocument state;
        state[channel_] = on_ ? "on" : "off";
        core.publishState(state);
        statePublished_ = core.online();
    }

    Core* core_ = nullptr;
    String channel_ = "relay";
    int relayPin_ = -1;
    bool activeLow_ = false;
    int buttonPin_ = -1;
    bool on_ = false;
    bool buttonDown_ = false;
    uint32_t lastButtonMs_ = 0;
    uint32_t maxOnS_ = 0;
    bool statePublished_ = false;
    RelayTimer timer_;
};

Relay role;

}  // namespace

hornet::Role& hornetRole() { return role; }
