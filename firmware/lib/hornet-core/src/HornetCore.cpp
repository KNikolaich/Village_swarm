#include "HornetCore.h"

#include <HTTPClient.h>
#include <WiFi.h>
#include <esp_mac.h>
#include <esp_random.h>
#include <esp_system.h>
#include <esp_task_wdt.h>
#include <espMqttClient.h>

#include <sys/time.h>

#include "CommandGuard.h"
#include "Outbox.h"
#include "Ulid.h"

namespace hornet {

namespace {

// Single-threaded client: callbacks run inside mqtt.loop() on the Arduino loop task.
espMqttClient mqtt(espMqttClientTypes::UseInternalTask::NO);
Outbox<50> outbox;  // RAM only (spec 4.5); camera roles keep photos on SD themselves
CommandGuard<16> guard;

constexpr uint32_t kWatchdogMs = 30000;  // spec 4.5: reboot if the loop hangs for 30 s
constexpr uint64_t kSaneEpochMs = 1700000000000ULL;

String willTopic;

const char* resetReason() {
    switch (esp_reset_reason()) {
        case ESP_RST_POWERON: return "poweron";
        case ESP_RST_SW: return "software";
        case ESP_RST_PANIC: return "panic";
        case ESP_RST_INT_WDT:
        case ESP_RST_TASK_WDT:
        case ESP_RST_WDT: return "watchdog";
        case ESP_RST_BROWNOUT: return "brownout";
        case ESP_RST_DEEPSLEEP: return "deepsleep";
        case ESP_RST_EXT: return "external";
        default: return "unknown";
    }
}

const char* ackStatus(AckStatus s) {
    switch (s) {
        case AckStatus::Ok: return "ok";
        case AckStatus::Rejected: return "rejected";
        case AckStatus::Expired: return "expired";
        case AckStatus::Error: return "error";
        default: return "unsupported";
    }
}

String macString() {
    uint8_t mac[6];
    esp_read_mac(mac, ESP_MAC_WIFI_STA);
    char buf[18];
    snprintf(buf, sizeof buf, "%02X:%02X:%02X:%02X:%02X:%02X", mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    return buf;
}

}  // namespace

void Core::begin() {
    Serial.begin(115200);
    settings_.load();
    settings_.boot++;
    settings_.saveBoot();
    Serial.printf("\n[hornet] %s fw=%s boot=%u reset=%s\n", role_.type(), HORNET_FW_VERSION, settings_.boot, resetReason());

    esp_task_wdt_config_t wdt = {.timeout_ms = kWatchdogMs, .idle_core_mask = 0, .trigger_panic = true};
    esp_task_wdt_reconfigure(&wdt);
    esp_task_wdt_add(nullptr);

    // Last config from NVS: the hornet behaves the same after a reboot without the broker (spec 5.6).
    const String saved = settings_.loadConfig();
    role_.setup(*this);
    if (saved.length()) applyConfig(saved, false);

    if (!settings_.complete()) {
        Serial.println("[hornet] not configured. Type: set ssid <name> / set pass <pw> / set mqtt <host[:port]> / set id <device-id> / set upload <http://hive:8443> / reboot");
        settings_.print(Serial);
        return;
    }

    WiFi.mode(WIFI_STA);
    WiFi.setAutoReconnect(true);
    WiFi.persistent(false);
    WiFi.setHostname(settings_.deviceId.c_str());
    WiFi.begin(settings_.ssid.c_str(), settings_.pass.c_str());

    configTime(0, 0, settings_.ntp.c_str(), settings_.mqttHost.c_str());

    willTopic = topics::status(settings_.deviceId.c_str()).c_str();
    mqtt.setServer(settings_.mqttHost.c_str(), settings_.mqttPort);
    mqtt.setClientId(settings_.deviceId.c_str());
    if (settings_.mqttUser.length()) mqtt.setCredentials(settings_.mqttUser.c_str(), settings_.mqttPass.c_str());
    mqtt.setWill(willTopic.c_str(), 1, true, "offline");
    mqtt.setKeepAlive(15);
    mqtt.setCleanSession(true);
    mqtt.onConnect([this](bool) { onMqttConnected(); });
    mqtt.onMessage([this](const espMqttClientTypes::MessageProperties&, const char* topic, const uint8_t* payload,
                          size_t len, size_t index, size_t total) { onMqttMessage(topic, payload, len, index, total); });
}

void Core::loop() {
    esp_task_wdt_reset();
    serialConsole();
    if (settings_.complete()) {
        mqtt.loop();
        connectMqtt();
        if (online() && millis() - lastHealthMs_ >= healthIntervalS_ * 1000UL) {
            lastHealthMs_ = millis();
            publishHealth();
        }
    }
    role_.loop(*this);
}

bool Core::online() const { return mqtt.connected(); }

bool Core::clockSynced() const {
    timeval tv;
    gettimeofday(&tv, nullptr);
    return static_cast<uint64_t>(tv.tv_sec) * 1000ULL > kSaneEpochMs;
}

uint64_t Core::nowMs() const {
    timeval tv;
    gettimeofday(&tv, nullptr);
    const uint64_t ms = static_cast<uint64_t>(tv.tv_sec) * 1000ULL + tv.tv_usec / 1000;
    return ms > kSaneEpochMs ? ms : 0;
}

String Core::newUlid() const {
    uint8_t random[10];
    esp_fill_random(random, sizeof random);
    char id[kUlidLength + 1];
    encodeUlid(nowMs(), random, id);
    return id;
}

String Core::stamp(JsonObject message, const String& presetId) {
    const String id = presetId.length() ? presetId : newUlid();
    message["v"] = 1;
    message["id"] = id;
    message["ts"] = nowMs();
    message["seq"] = ++seq_;
    message["boot"] = settings_.boot;
    return id;
}

void Core::connectMqtt() {
    if (mqtt.connected()) {
        mqttWasConnected_ = true;
        return;
    }
    if (mqttWasConnected_) {
        mqttWasConnected_ = false;
        Serial.println("[hornet] mqtt disconnected");
    }
    if (WiFi.status() != WL_CONNECTED || millis() < nextMqttAttemptMs_ || mqtt.disconnected() == false) return;
    // Exponential backoff with jitter (spec 4.5).
    nextMqttAttemptMs_ = millis() + backoffMs(mqttAttempts_++) + (esp_random() % 1000);
    Serial.printf("[hornet] mqtt connect %s:%u\n", settings_.mqttHost.c_str(), settings_.mqttPort);
    mqtt.connect();
}

void Core::onMqttConnected() {
    mqttAttempts_ = 0;
    const std::string dev = settings_.deviceId.c_str();
    Serial.printf("[hornet] online as %s, ip %s\n", dev.c_str(), WiFi.localIP().toString().c_str());
    mqtt.subscribe(topics::cmdWildcard(dev).c_str(), 1);
    mqtt.subscribe(topics::config(dev).c_str(), 1);
    mqtt.publish(willTopic.c_str(), 1, true, "online");
    publishInfo();
    flushOutbox();
    lastHealthMs_ = 0;  // health right away
}

void Core::onMqttMessage(const char* topic, const uint8_t* payload, size_t len, size_t index, size_t total) {
    // Large payloads arrive in chunks; assemble before parsing.
    if (index == 0) {
        rxTopic_ = topic;
        rxPayload_ = "";
        rxPayload_.reserve(total);
    }
    rxPayload_.concat(reinterpret_cast<const char*>(payload), len);
    if (index + len >= total) handleMessage(rxTopic_, rxPayload_);
}

void Core::handleMessage(const String& topic, const String& payload) {
    const String dev = settings_.deviceId;
    const String cfg = topics::config(dev.c_str()).c_str();
    if (topic == cfg) {
        if (payload.length()) applyConfig(payload, true);
        return;
    }
    const String cmdPrefix = String(topics::device(dev.c_str()).c_str()) + "/cmd/";
    if (topic.startsWith(cmdPrefix) && !topic.endsWith("/ack")) handleCommand(topic.substring(cmdPrefix.length()), payload);
}

void Core::handleCommand(const String& name, const String& payload) {
    JsonDocument cmd;
    if (deserializeJson(cmd, payload) || !cmd["cid"].is<const char*>()) {
        log(LogLevel::Warn, "bad command " + name);
        return;
    }
    const std::string cid = cmd["cid"].as<const char*>();
    switch (guard.check(cid, cmd["ts"] | 0ULL, cmd["ttl_s"] | 60U, nowMs())) {
        case CommandCheck::Duplicate: {
            // Same cid again: not executed twice, the original ack is re-sent (spec 4.4).
            const std::string* ackTopic = nullptr;
            const std::string* ack = guard.ackFor(cid, &ackTopic);
            publish(ackTopic->c_str(), ack->c_str(), false, true, true);
            return;
        }
        case CommandCheck::Expired: {
            AckResult expired(AckStatus::Expired, "expired");
            JsonDocument ack;
            stamp(ack.to<JsonObject>());
            ack["cid"] = cid;
            ack["status"] = ackStatus(expired.status);
            ack["msg"] = expired.msg;
            String body;
            serializeJson(ack, body);
            const String t = topics::cmdAck(settings_.deviceId.c_str(), name.c_str()).c_str();
            guard.remember(cid, t.c_str(), body.c_str());
            publish(t, body, false, true, true);
            return;
        }
        case CommandCheck::Execute:
            break;
    }

    AckResult result(AckStatus::Unsupported, "unknown command " + name);
    if (name == "reboot" || name == "config_reload" || name == "factory_reset") {
        result = AckResult::ok();
    } else {
        for (auto& [n, handler] : commands_)
            if (n == name) result = handler(cmd.as<JsonObjectConst>());
    }

    JsonDocument ack;
    stamp(ack.to<JsonObject>());
    ack["cid"] = cid;
    ack["status"] = ackStatus(result.status);
    if (result.msg.length()) ack["msg"] = result.msg;
    if (!result.state.isNull()) ack["state"] = result.state;
    String body;
    serializeJson(ack, body);
    const String t = topics::cmdAck(settings_.deviceId.c_str(), name.c_str()).c_str();
    guard.remember(cid, t.c_str(), body.c_str());
    publish(t, body, false, true, true);

    if ((name == "reboot" || name == "factory_reset") && result.status == AckStatus::Ok) {
        mqtt.loop();  // give the ack a chance to leave
        delay(200);
        if (name == "factory_reset") settings_.clear();  // spec 5.3: back to an unconfigured board
        ESP.restart();
    }
}

void Core::applyConfig(const String& payload, bool fromHive) {
    JsonDocument doc;
    if (deserializeJson(doc, payload)) {
        log(LogLevel::Error, "bad config");
        return;
    }
    const int rev = doc["rev"] | -1;
    if (fromHive && rev == configRev_) return;
    configRev_ = rev;
    config_ = doc;
    healthIntervalS_ = doc["health_interval_s"] | 60U;
    role_.applyConfig(*this, config_.as<JsonObjectConst>());
    if (!fromHive) return;

    settings_.saveConfig(payload);
    JsonDocument applied;
    applied["rev"] = rev;
    publishEvent("config_applied", applied);
}

void Core::publishInfo() {
    JsonDocument info;
    stamp(info.to<JsonObject>());
    info["type"] = role_.type();
    info["hw"] = role_.hw();
    info["fw"] = HORNET_FW_VERSION;
    info["mac"] = macString();
    info["ip"] = WiFi.localIP().toString();
    JsonArray caps = info["caps"].to<JsonArray>();
    for (auto c : role_.caps()) caps.add(c);
    JsonArray metrics = info["metrics"].to<JsonArray>();
    for (auto m : role_.metrics()) metrics.add(m);
    JsonArray commands = info["commands"].to<JsonArray>();
    commands.add("reboot");
    commands.add("config_reload");
    commands.add("factory_reset");
    for (auto& [n, _] : commands_) commands.add(n);
    String body;
    serializeJson(info, body);
    publish(topics::info(settings_.deviceId.c_str()).c_str(), body, true, true, false);
}

void Core::publishHealth() {
    JsonDocument health;
    stamp(health.to<JsonObject>());
    health["uptime_s"] = millis() / 1000;
    health["rssi"] = WiFi.RSSI();
    health["heap_free"] = ESP.getFreeHeap();
    if (psramFound()) health["psram_free"] = ESP.getFreePsram();
    health["reset_reason"] = resetReason();
    health["outbox"] = outbox.size();
    health["broker"] = "home";
    role_.fillHealth(health.as<JsonObject>());
    String body;
    serializeJson(health, body);
    publish(topics::health(settings_.deviceId.c_str()).c_str(), body, false, false, false);
}

String Core::publishEvent(const char* type, JsonDocument& body, const String& presetId) {
    const String id = stamp(body.as<JsonObject>(), presetId);
    String payload;
    serializeJson(body, payload);
    publish(topics::event(settings_.deviceId.c_str(), type).c_str(), payload, false, true, true);
    return id;
}

void Core::publishTele(const char* metric, float value, const char* unit) {
    JsonDocument tele;
    stamp(tele.to<JsonObject>());
    tele["value"] = value;
    if (unit) tele["unit"] = unit;
    String body;
    serializeJson(tele, body);
    publish(topics::tele(settings_.deviceId.c_str(), metric).c_str(), body, false, false, false);
}

void Core::publishState(JsonDocument& state) {
    JsonDocument msg;
    stamp(msg.to<JsonObject>());
    msg["state"] = state;
    String body;
    serializeJson(msg, body);
    publish(topics::state(settings_.deviceId.c_str()).c_str(), body, true, true, false);
}

void Core::log(LogLevel level, const String& msg) {
    static const char* names[] = {"debug", "info", "warn", "error"};
    Serial.printf("[%s] %s\n", names[static_cast<int>(level)], msg.c_str());
    if (level < LogLevel::Warn || !settings_.complete()) return;  // WARN and above go to MQTT (spec 4.2)
    JsonDocument doc;
    stamp(doc.to<JsonObject>());
    doc["level"] = names[static_cast<int>(level)];
    doc["msg"] = msg.substring(0, 500);
    String body;
    serializeJson(doc, body);
    publish(topics::log(settings_.deviceId.c_str()).c_str(), body, false, false, false);
}

void Core::onCommand(const char* name, CommandHandler handler) { commands_.emplace_back(name, std::move(handler)); }

bool Core::publish(const String& topic, const String& payload, bool retain, bool qos1, bool bufferIfOffline) {
    if (mqtt.connected() && mqtt.publish(topic.c_str(), qos1 ? 1 : 0, retain, payload.c_str()) != 0) return true;
    if (bufferIfOffline && !outbox.push(topic.c_str(), payload.c_str())) Serial.println("[hornet] outbox full, oldest message dropped");
    return false;
}

void Core::flushOutbox() {
    while (!outbox.empty() && mqtt.connected()) {
        const auto& m = outbox.front();
        if (mqtt.publish(m.topic.c_str(), 1, false, m.payload.c_str()) == 0) break;
        outbox.pop();
    }
}

void Core::serialConsole() {
    while (Serial.available()) {
        const char c = static_cast<char>(Serial.read());
        if (c != '\n' && c != '\r') {
            if (consoleLine_.length() < 200) consoleLine_ += c;
            continue;
        }
        String line = consoleLine_;
        consoleLine_ = "";
        line.trim();
        if (!line.length()) continue;
        if (line == "show") settings_.print(Serial);
        else if (line == "reboot") ESP.restart();
        else if (line == "factory") {
            settings_.clear();
            Serial.println("settings cleared, rebooting");
            delay(100);
            ESP.restart();
        } else if (line.startsWith("enroll ")) {
            const int sp = line.indexOf(' ', 7);
            if (sp < 0) Serial.println("usage: enroll <http://hive:port> <CODE>");
            else if (enroll(line.substring(7, sp), line.substring(sp + 1))) {
                delay(200);
                ESP.restart();
            }
        } else if (!settings_.command(line, Serial)) Serial.println("commands: show | set <ssid|pass|mqtt|mqttuser|mqttpass|id|upload|token|ntp> <value> | enroll <hive-url> <code> | reboot | factory");
    }
}

}  // namespace hornet

namespace hornet {

// Trades a one-time code from the web UI for this hornet's identity and credentials (spec 5.3 step 3-5).
// Wi-Fi must be set first (`set ssid`, `set pass`). Prints "enrolled <id>" for the browser wizard.
bool Core::enroll(const String& hiveUrl, const String& code) {
    if (!settings_.ssid.length()) {
        Serial.println("enroll failed: set ssid/pass first");
        return false;
    }
    if (WiFi.status() != WL_CONNECTED) {
        WiFi.mode(WIFI_STA);
        WiFi.begin(settings_.ssid.c_str(), settings_.pass.c_str());
        Serial.print("[hornet] wifi");
        for (int i = 0; i < 40 && WiFi.status() != WL_CONNECTED; ++i) {
            esp_task_wdt_reset();
            delay(500);
            Serial.print('.');
        }
        Serial.println();
        if (WiFi.status() != WL_CONNECTED) {
            Serial.println("enroll failed: wifi not connected (check ssid/pass)");
            return false;
        }
    }

    JsonDocument request;
    request["code"] = code;
    request["mac"] = macString();
    request["hw"] = role_.hw();
    request["fw"] = HORNET_FW_VERSION;
    String body;
    serializeJson(request, body);

    HTTPClient http;
    http.setTimeout(10000);
    String url = hiveUrl;
    if (url.endsWith("/")) url.remove(url.length() - 1);
    if (!http.begin(url + "/api/provision/enroll")) {
        Serial.println("enroll failed: bad hive url");
        return false;
    }
    http.addHeader("Content-Type", "application/json");
    const int status = http.POST(body);
    const String response = http.getString();
    http.end();
    if (status != 200) {
        Serial.printf("enroll failed: HTTP %d%s\n", status, status == 403 ? " (code wrong, used or expired)" : "");
        return false;
    }

    JsonDocument doc;
    if (deserializeJson(doc, response)) {
        Serial.println("enroll failed: bad response");
        return false;
    }
    settings_.deviceId = doc["deviceId"].as<String>();
    settings_.mqttHost = doc["mqtt"]["host"].as<String>();
    settings_.mqttPort = doc["mqtt"]["port"] | 1883;
    settings_.mqttUser = doc["mqtt"]["user"] | "";
    settings_.mqttPass = doc["mqtt"]["pass"] | "";
    settings_.uploadUrl = doc["uploadUrl"].as<String>();
    settings_.uploadToken = doc["uploadToken"].as<String>();
    settings_.save();
    String config;
    serializeJson(doc["config"], config);
    settings_.saveConfig(config);
    Serial.printf("enrolled %s\n", settings_.deviceId.c_str());
    return true;
}

}  // namespace hornet
