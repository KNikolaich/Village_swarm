#pragma once

// Hornet lifecycle: boot -> wifi -> time -> mqtt -> loop (spec 5.2).
// The core owns the network, the MQTT contract (envelope, LWT, outbox, health, logs, commands, retained
// config); a Role (src/roles/<role>.cpp) adds sensors, actuators, events and commands on top.

#include <Arduino.h>
#include <ArduinoJson.h>

#include <functional>
#include <string>
#include <vector>

#include "Platform.h"
#include "Settings.h"
#include "Topics.h"

// Board wiring defaults come from platformio.ini (-D HW_...=); the config "hw" section overrides them.
#ifndef HORNET_BOARD
#define HORNET_BOARD "unknown"
#endif
#ifndef HW_ONEWIRE_PIN
#define HW_ONEWIRE_PIN -1
#endif
#ifndef HW_SDA_PIN
#define HW_SDA_PIN -1
#endif
#ifndef HW_SCL_PIN
#define HW_SCL_PIN -1
#endif
#ifndef HW_RELAY_PIN
#define HW_RELAY_PIN -1
#endif
#ifndef HW_RELAY_ACTIVE_LOW
#define HW_RELAY_ACTIVE_LOW 0
#endif
#ifndef HW_BUTTON_PIN
#define HW_BUTTON_PIN -1
#endif

namespace hornet {

enum class AckStatus { Ok, Rejected, Expired, Error, Unsupported };

struct AckResult {
    AckStatus status = AckStatus::Ok;
    String msg;
    // Optional actuator state echoed in the ack, e.g. {"armed": true}.
    JsonDocument state;

    AckResult() = default;
    AckResult(AckStatus s, const String& m) : status(s), msg(m) {}

    static AckResult ok() { return {}; }
    static AckResult error(const String& m) { return {AckStatus::Error, m}; }
};

class Core;

// Implemented by each role in src/roles/<role>.cpp.
struct Role {
    virtual ~Role() = default;
    virtual const char* type() const = 0;
    virtual const char* hw() const = 0;
    virtual std::vector<const char*> caps() const { return {}; }
    virtual std::vector<const char*> metrics() const { return {}; }

    // Hardware init; register commands with core.onCommand() here.
    virtual void setup(Core& core) = 0;
    virtual void loop(Core& core) = 0;

    // Retained config from the hive (contracts/schemas/config.schema.json), also replayed from NVS at boot.
    virtual void applyConfig(Core& core, JsonObjectConst config) {}

    // Extra health fields (e.g. psram).
    virtual void fillHealth(JsonObject health) {}
};

using CommandHandler = std::function<AckResult(JsonObjectConst payload)>;

enum class LogLevel { Debug, Info, Warn, Error };

class Core {
public:
    explicit Core(Role& role) : role_(role) {}

    void begin();
    void loop();

    const Settings& settings() const { return settings_; }
    const String& deviceId() const { return settings_.deviceId; }
    bool online() const;
    bool clockSynced() const;

    // Unix ms, or 0 while NTP has not synced (spec 4.3).
    uint64_t nowMs() const;

    String newUlid() const;

    // Adds v, id, ts, seq, boot to a message about to be published; returns the id.
    // A pre-made id lets a camera name photos after the event before publishing it.
    String stamp(JsonObject message, const String& id = String());

    // event/{type}: QoS 1, buffered while offline. Returns the event id.
    String publishEvent(const char* type, JsonDocument& body, const String& id = String());
    void publishTele(const char* metric, float value, const char* unit);
    void publishState(JsonDocument& state);
    void log(LogLevel level, const String& msg);

    void onCommand(const char* name, CommandHandler handler);

    // Last applied config (empty object until the hive sends one).
    JsonObjectConst config() const { return config_.as<JsonObjectConst>(); }

private:
    void connectMqtt();
    void onMqttConnected();
    void onMqttMessage(const char* topic, const uint8_t* payload, size_t len, size_t index, size_t total);
    void handleMessage(const String& topic, const String& payload);
    void handleCommand(const String& name, const String& payload);
    void applyConfig(const String& payload, bool fromHive);
    void publishInfo();
    void publishHealth();
    bool publish(const String& topic, const String& payload, bool retain, bool qos1, bool bufferIfOffline);
    void flushOutbox();
    void serialConsole();
    bool enroll(const String& hiveUrl, const String& code);

    Role& role_;
    Settings settings_;
    JsonDocument config_;
    int configRev_ = -1;
    uint32_t seq_ = 0;
    uint32_t healthIntervalS_ = 60;
    unsigned long lastHealthMs_ = 0;
    unsigned long nextMqttAttemptMs_ = 0;
    unsigned mqttAttempts_ = 0;
    bool mqttWasConnected_ = false;
    String rxTopic_;
    String rxPayload_;
    String consoleLine_;
    std::vector<std::pair<String, CommandHandler>> commands_;
};

}  // namespace hornet
