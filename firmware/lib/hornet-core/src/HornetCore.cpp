#include "HornetCore.h"

#include <espMqttClient.h>

#include <sys/time.h>

#include "CommandGuard.h"
#include "Ota.h"
#include "Outbox.h"
#include "Platform.h"
#include "Ulid.h"
#include "WifiChange.h"

namespace hornet {

namespace {

// Single-threaded client: callbacks run inside mqtt.loop() on the Arduino loop task.
#if defined(ESP32)
espMqttClient mqtt(espMqttClientTypes::UseInternalTask::NO);
#else
espMqttClient mqtt;
#endif
Outbox<50> outbox;  // RAM only (spec 4.5); camera roles keep photos on SD themselves
CommandGuard<16> guard;

constexpr uint32_t kWatchdogMs = 30000;  // spec 4.5: reboot if the loop hangs for 30 s
constexpr uint64_t kSaneEpochMs = 1700000000000ULL;
// A sleeping hornet that cannot reach Wi-Fi or the broker gives up after this long and sleeps again.
constexpr unsigned long kAwakeLimitMs = 30000;
// Time for the last QoS 0/1 messages to leave before the clean disconnect.
constexpr unsigned long kFlushBeforeSleepMs = 1500;
// A freshly updated image that has not reached the broker by then is judged broken (ESP32 rolls back).
constexpr unsigned long kOtaConfirmMs = 120000;

String willTopic;

const char* ackStatus(AckStatus s) {
    switch (s) {
        case AckStatus::Ok: return "ok";
        case AckStatus::Rejected: return "rejected";
        case AckStatus::Expired: return "expired";
        case AckStatus::Error: return "error";
        default: return "unsupported";
    }
}

String macString() { return WiFi.macAddress(); }

}  // namespace

void Core::begin() {
    Serial.begin(115200);
    settings_.load();
    settings_.boot++;
    // Counted as a failed boot until the broker is reached (onMqttConnected clears it): the main and the spare Wi-Fi
    // network take turns while boots keep failing (cmd/wifi mode stage).
    const uint32_t failedBootsBefore = settings_.wifiFails;
    settings_.wifiFails++;
    settings_.saveBoot();
    Serial.printf("\n[hornet] %s fw=%s boot=%u reset=%s\n", role_.type(), HORNET_FW_VERSION, settings_.boot, platform::resetReason());

    platform::watchdogBegin(kWatchdogMs);

    // Last config from NVS: the hornet behaves the same after a reboot without the broker (spec 5.6).
    const String saved = settings_.loadConfig();
    role_.setup(*this);
    // Over-the-air update, available to every role (spec 5.5).
    onCommand("ota", [this](JsonObjectConst payload) { return queueOta(payload); });
    // Moving to another Wi-Fi network, with a way back (contracts/mqtt-topics.md).
    onCommand("wifi", [this](JsonObjectConst payload) { return queueWifi(payload); });
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
    // After cmd/wifi this boot tries the new network; the old one is still in settings_ and comes back if it fails.
    wifiTrial_ = settings_.wifiNextSsid.length() > 0;
    usingSpare_ = !wifiTrial_ && wifichange::useSpareAtBoot(settings_.wifi2Ssid.length() > 0, failedBootsBefore);
    platform::WifiHint hint;
    if (wifiTrial_) {
        Serial.printf("[hornet] trying the new Wi-Fi \"%s\"\n", settings_.wifiNextSsid.c_str());
        WiFi.begin(settings_.wifiNextSsid.c_str(), settings_.wifiNextPass.c_str());  // the hint belongs to the old access point
    } else if (usingSpare_) {
        Serial.printf("[hornet] the main network failed last time, trying the spare \"%s\"\n", settings_.wifi2Ssid.c_str());
        WiFi.begin(settings_.wifi2Ssid.c_str(), settings_.wifi2Pass.c_str());
    } else if (sleepy() && platform::loadWifiHint(hint)) {
        WiFi.begin(settings_.ssid.c_str(), settings_.pass.c_str(), hint.channel, hint.bssid);  // no scan
    } else {
        WiFi.begin(settings_.ssid.c_str(), settings_.pass.c_str());
    }
    applyWifiPowerSave();
    if (sleepy()) Serial.printf("[hornet] battery mode: sleep %u s between readings (type 'awake' to stay up)\n", static_cast<unsigned>(sleepS_));

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
    platform::watchdogFeed();
    serialConsole();
    if (settings_.complete()) {
        mqtt.loop();
        connectMqtt();
        if (online() && millis() - lastHealthMs_ >= healthIntervalS_ * 1000UL) {
            lastHealthMs_ = millis();
            publishHealth();
        }
        if (ota_.queued && online()) runOta();
        checkOtaOutcome();
        checkWifiTrial();  // before the sleep logic: a deep sleeper must decide before its awake limit
        checkWifiSpare();
        if (wifiRestartAtMs_ && millis() >= wifiRestartAtMs_) {
            Serial.println("[wifi] restarting onto the new network");
            mqtt.disconnect();
            for (int i = 0; i < 10; ++i) {
                mqtt.loop();
                delay(10);
            }
            ESP.restart();
        }
        if (sleepy()) {
            if (sleepAtMs_ && millis() >= sleepAtMs_) sleepNow();
            if (millis() > kAwakeLimitMs) {
                // No network this time; a stale access point hint may be the reason, so scan next time.
                if (WiFi.status() != WL_CONNECTED) platform::clearWifiHint();
                Serial.println("[hornet] no connection, sleeping anyway");
                sleepNow();
            }
        }
    }
    role_.loop(*this);
    // An idle CPU lets the chip nap between Wi-Fi beacons; 50 ms keeps commands and the button responsive.
    if (lightSleep()) delay(50);
}

// Light sleep: the radio naps between beacons and wakes for traffic, so MQTT stays connected.
void Core::applyWifiPowerSave() {
#if defined(ESP8266)
    WiFi.setSleepMode(lightSleep() ? WIFI_LIGHT_SLEEP : WIFI_NONE_SLEEP, lightSleep() ? 3 : 0);
#else
    WiFi.setSleep(lightSleep());
#endif
}

// The command only queues the update and is acknowledged at once; the download runs from loop() once the ack is out.
AckResult Core::queueOta(JsonObjectConst payload) {
    const String url = payload["url"] | "";
    const String md5 = payload["md5"] | "";
    const String version = payload["version"] | "";
    const uint32_t size = payload["size"] | 0U;
    const String sig = payload["sig"] | "";
    if (ota::signatureRequired() && sig.length() != 128) return AckResult(AckStatus::Rejected, "unsigned image refused");
    if (!url.startsWith("http://") || md5.length() != 32 || !size || !version.length()) return AckResult::error("bad ota command");
    if (ota_.queued) return AckResult::error("update already running");
    if (version == HORNET_FW_VERSION) return AckResult(AckStatus::Rejected, "already on " + version);
    if (size > ota::freeSpace()) return AckResult(AckStatus::Rejected, "image does not fit: " + String(size) + " > " + String(ota::freeSpace()));
    ota_.queued = true;
    ota_.url = url;
    ota_.md5 = md5;
    ota_.size = size;
    ota_.version = version;
    ota_.sig = sig;
    return AckResult::ok();
}

void Core::runOta() {
    JsonDocument started;
    started["phase"] = "started";
    started["version"] = ota_.version;
    publishEvent("ota", started);
    for (int i = 0; i < 10; ++i) {  // let the ack and the event leave before the network goes quiet
        mqtt.loop();
        delay(10);
    }
    Serial.printf("[ota] %s (%u bytes)\n", ota_.url.c_str(), static_cast<unsigned>(ota_.size));

    String error;
    const bool ok = ota::download(ota_.url, settings_.uploadToken, ota_.size, ota_.md5, ota_.sig, error, platform::watchdogFeed);
    if (!ok) {
        Serial.printf("[ota] failed: %s\n", error.c_str());
        JsonDocument failed;
        failed["phase"] = "failed";
        failed["severity"] = "warn";
        failed["version"] = ota_.version;
        failed["error"] = error.substring(0, 200);
        publishEvent("ota", failed);
        ota_ = PendingOta{};
        return;
    }
    // The new image runs next: remember what to confirm. It is "applied" only once it reaches the broker.
    settings_.otaPending = ota_.version;
    settings_.saveOta();
    Serial.println("[ota] installed, restarting");
    mqtt.disconnect();
    for (int i = 0; i < 10; ++i) {
        mqtt.loop();
        delay(10);
    }
    ESP.restart();
}

// After an update: report once the new image is online; if it never gets there, ESP32 goes back to the old one.
void Core::checkOtaOutcome() {
    if (!settings_.otaPending.length() || online()) return;
    if (millis() < kOtaConfirmMs) return;
    const String version = settings_.otaPending;
    settings_.otaPending = "";
    if (ota::rollBack()) {
        settings_.otaRolledBack = version;
        settings_.saveOta();
        Serial.println("[ota] new image did not come online, rolling back");
        delay(100);
        ESP.restart();
    }
    // No old image to go back to (ESP8266): keep running, and report "applied" if it gets online later.
    settings_.otaPending = version;
}

// The command only stores the new network next to the old one and is acknowledged at once; loop() restarts onto it
// once the ack has left. Nothing about the working network changes until the new one has proven itself.
AckResult Core::queueWifi(JsonObjectConst payload) {
    const String mode = payload["mode"] | "switch";
    if (mode == "forget") {
        // Drops the spare network (an old one that no longer leads to the hive). The network in use stays.
        if (usingSpare_) return AckResult(AckStatus::Rejected, "running on the spare network now");
        const String forgotten = settings_.wifi2Ssid;
        settings_.wifi2Ssid = "";
        settings_.wifi2Pass = "";
        settings_.saveWifiSpare();
        Serial.printf("[wifi] spare network \"%s\" forgotten\n", forgotten.c_str());
        return AckResult::ok();
    }
    const std::string ssid = (payload["ssid"] | "");
    const std::string pass = (payload["pass"] | "");
    if (!wifichange::validCredentials(ssid, pass)) return AckResult::error("bad wifi command");
    if (wifichange::sameAsCurrent(ssid, pass, settings_.ssid.c_str(), settings_.pass.c_str()))
        return AckResult(AckStatus::Rejected, "already on this network");
    if (wifiRestartAtMs_ || wifiTrial_) return AckResult::error("a wifi change is already running");
    if (mode == "stage") {
        // Only kept: nothing changes now. The spare takes over when the main network is gone.
        settings_.wifi2Ssid = ssid.c_str();
        settings_.wifi2Pass = pass.c_str();
        settings_.saveWifiSpare();
        JsonDocument staged;
        staged["phase"] = "staged";
        staged["ssid"] = settings_.wifi2Ssid;
        publishEvent("wifi", staged);
        return AckResult::ok();
    }
    settings_.wifiNextSsid = ssid.c_str();
    settings_.wifiNextPass = pass.c_str();
    settings_.wifiRolledBack = "";
    settings_.saveWifiChange();
    wifiRestartAtMs_ = millis() + 1500;
    return AckResult::ok();
}

// Trial boot: the new network is the hornet's own once it has reached the broker over it; if it has not by the
// deadline, the old one comes back. Which of the two happened is reported once the hornet is online again.
void Core::checkWifiTrial() {
    switch (wifichange::decide(wifiTrial_, online(), millis(), sleepy())) {
        case wifichange::Trial::Commit: {
            const String ssid = settings_.wifiNextSsid;
            settings_.wifi2Ssid = settings_.ssid;  // the network it leaves stays as the spare
            settings_.wifi2Pass = settings_.pass;
            settings_.saveWifiSpare();
            settings_.ssid = ssid;
            settings_.pass = settings_.wifiNextPass;
            settings_.wifiNextSsid = "";
            settings_.wifiNextPass = "";
            settings_.save();
            settings_.saveWifiChange();
            wifiTrial_ = false;
            platform::clearWifiHint();  // channel and access point of the old network are of no use now
            Serial.printf("[wifi] now on \"%s\"\n", ssid.c_str());
            JsonDocument applied;
            applied["phase"] = "applied";
            applied["ssid"] = ssid;
            publishEvent("wifi", applied);
            break;
        }
        case wifichange::Trial::RollBack:
            Serial.printf("[wifi] \"%s\" did not work, going back to the old network\n", settings_.wifiNextSsid.c_str());
            settings_.wifiRolledBack = settings_.wifiNextSsid;
            settings_.wifiFails = 0;  // this restart is not a failed boot of the main network
            settings_.saveBoot();
            settings_.wifiNextSsid = "";
            settings_.wifiNextPass = "";
            settings_.saveWifiChange();
            delay(100);
            ESP.restart();
            break;
        default:
            break;
    }
}

// The spare network (cmd/wifi mode stage): when it is the one that reached the broker it becomes the main network (and
// the old one the spare); an awake hornet whose Wi-Fi stays down swaps networks itself, a sleeper does so boot by boot.
void Core::checkWifiSpare() {
    if (wifichange::promoteSpare(usingSpare_, online())) {
        const String oldSsid = settings_.ssid;
        const String oldPass = settings_.pass;
        settings_.ssid = settings_.wifi2Ssid;
        settings_.pass = settings_.wifi2Pass;
        settings_.wifi2Ssid = oldSsid;
        settings_.wifi2Pass = oldPass;
        settings_.save();
        settings_.saveWifiSpare();
        usingSpare_ = false;
        platform::clearWifiHint();  // channel and access point of the old network are of no use now
        Serial.printf("[wifi] the spare network works, it is the main one now: \"%s\"\n", settings_.ssid.c_str());
        JsonDocument applied;
        applied["phase"] = "applied";
        applied["ssid"] = settings_.ssid;
        publishEvent("wifi", applied);
        return;
    }

    // An awake hornet that stays without Wi-Fi, or with Wi-Fi but without the broker, tries the other network (a
    // sleeper does it boot by boot, see begin()).
    if (sleepy() || wifiTrial_ || online()) {
        wifiDownSince_ = 0;
        brokerDownSince_ = 0;
        return;
    }
    const unsigned long now = millis();
    const bool wifiUp = WiFi.status() == WL_CONNECTED;
    if (!brokerDownSince_) brokerDownSince_ = now;
    if (wifiUp) wifiDownSince_ = 0;
    else if (!wifiDownSince_) wifiDownSince_ = now;
    if (wifichange::swapNetworks(settings_.wifi2Ssid.length() > 0, wifiTrial_, wifiUp, wifiDownSince_ ? now - wifiDownSince_ : 0,
                                 now - brokerDownSince_, usingSpare_)) {
        usingSpare_ = !usingSpare_;
        const String& ssid = usingSpare_ ? settings_.wifi2Ssid : settings_.ssid;
        const String& pass = usingSpare_ ? settings_.wifi2Pass : settings_.pass;
        Serial.printf("[wifi] %s, trying %s network \"%s\"\n", wifiUp ? "no broker" : "no connection", usingSpare_ ? "the spare" : "the main", ssid.c_str());
        WiFi.disconnect();
        WiFi.begin(ssid.c_str(), pass.c_str());
        wifiDownSince_ = now;
        brokerDownSince_ = now;
    }
}

void Core::readingDone() {
    if (sleepy() && !sleepAtMs_) sleepAtMs_ = millis() + kFlushBeforeSleepMs;
}

void Core::sleepNow() {
    Serial.printf("[hornet] sleeping %u s\n", static_cast<unsigned>(sleepS_));
    flushOutbox();
    // A clean DISCONNECT does not fire the LWT: the status stays "online" while the hornet sleeps (contracts/mqtt-topics.md).
    mqtt.disconnect();
    for (int i = 0; i < 30 && mqtt.connected(); ++i) {
        mqtt.loop();
        delay(10);
    }
    WiFi.disconnect(true);
    Serial.flush();
    platform::deepSleep(static_cast<uint64_t>(sleepS_) * 1000000ULL);
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
    platform::fillRandom(random, sizeof random);
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
    nextMqttAttemptMs_ = millis() + backoffMs(mqttAttempts_++) + (random(1000));
    Serial.printf("[hornet] mqtt connect %s:%u\n", settings_.mqttHost.c_str(), settings_.mqttPort);
    mqtt.connect();
}

void Core::onMqttConnected() {
    mqttAttempts_ = 0;
    if (settings_.wifiFails) {  // this boot got through: the next one starts with the main network again
        settings_.wifiFails = 0;
        settings_.saveBoot();
    }
    const std::string dev = settings_.deviceId.c_str();
    Serial.printf("[hornet] online as %s, ip %s\n", dev.c_str(), WiFi.localIP().toString().c_str());
    mqtt.subscribe(topics::cmdWildcard(dev).c_str(), 1);
    mqtt.subscribe(topics::config(dev).c_str(), 1);
    mqtt.publish(willTopic.c_str(), 1, true, "online");
    if (settings_.otaPending.length() || settings_.otaRolledBack.length()) {
        JsonDocument outcome;
        // "applied" only when the image that is running is the one that was ordered: an old image booting after an
        // update (the new one was not taken) must not be reported as success.
        const bool wrongImage = !settings_.otaRolledBack.length() && settings_.otaPending != HORNET_FW_VERSION;
        const bool rolledBack = settings_.otaRolledBack.length() > 0 || wrongImage;
        outcome["phase"] = rolledBack ? "rolled_back" : "applied";
        if (rolledBack) outcome["severity"] = "warn";
        if (wrongImage) outcome["error"] = String("running ") + HORNET_FW_VERSION;
        outcome["version"] = settings_.otaRolledBack.length() ? settings_.otaRolledBack : settings_.otaPending;
        publishEvent("ota", outcome);
        settings_.otaPending = "";
        settings_.otaRolledBack = "";
        settings_.saveOta();
    }
    if (settings_.wifiRolledBack.length()) {
        JsonDocument failed;
        failed["phase"] = "rolled_back";
        failed["severity"] = "warn";
        failed["ssid"] = settings_.wifiRolledBack;
        failed["error"] = "no connection to the hive over it";
        publishEvent("wifi", failed);
        settings_.wifiRolledBack = "";
        settings_.saveWifiChange();
    }
    if (sleepy() && !wifiTrial_) {  // during a trial the hint of the old access point must not be overwritten
        platform::WifiHint hint;
        hint.channel = static_cast<uint8_t>(WiFi.channel());
        memcpy(hint.bssid, WiFi.BSSID(), sizeof hint.bssid);
        platform::saveWifiHint(hint);
    }
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
    sleepS_ = doc["sleep_s"] | 0U;
    lightSleep_ = String(doc["sleep_mode"] | "deep") == "light";
    applyWifiPowerSave();
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
    if (ota::signatureRequired()) caps.add("ota.signed");
    caps.add("wifi.stage");  // can keep a spare Wi-Fi network (cmd/wifi mode stage)
    caps.add("wifi.forget");  // and drop it again (cmd/wifi mode forget)  // the hive can tell which hornets only take signed images
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
    if (platform::hasPsram()) health["psram_free"] = platform::freePsram();
    health["reset_reason"] = platform::resetReason();
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

void Core::refreshInfo() {
    if (online()) publishInfo();
}

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

void Core::printHelp() {
    Serial.println("commands:");
    Serial.println("  show                          what is stored (id, network, hive address, boot count, wifi switch state)");
    Serial.println("  set <key> <value>             key: ssid pass mqtt mqttuser mqttpass id upload token ntp (the value may contain spaces)");
    Serial.println("  enroll <hive-url> <code>      join a hive with a one-time code from the web UI (set ssid and pass first)");
    Serial.println("  nospare                       forget the spare Wi-Fi network");
    Serial.println("  awake                         do not sleep until reboot (battery hornets)");
    Serial.println("  reboot                        restart");
    Serial.println("  factory                       erase every setting");
}

void Core::serialConsole() {
    while (Serial.available()) {
        const char c = static_cast<char>(Serial.read());
        // Somebody is at the console (the hive's USB console sends a newline twice a second): a battery hornet
        // stays awake instead of going back to sleep in the middle of being set up.
        stayAwake_ = true;
        if (c != '\n' && c != '\r') {
            if (consoleLine_.length() < 200) consoleLine_ += c;
            continue;
        }
        String line = consoleLine_;
        consoleLine_ = "";
        line.trim();
        if (!line.length()) continue;
        if (line == "show") settings_.print(Serial);
        else if (line == "help") printHelp();
        else if (line == "nospare") {
            settings_.wifi2Ssid = "";
            settings_.wifi2Pass = "";
            settings_.saveWifiSpare();
            Serial.println("spare network removed");
        }
        else if (line == "reboot") ESP.restart();
        else if (line == "awake") {
            stayAwake_ = true;
            Serial.println("staying awake until reboot");
        }
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
        } else if (!settings_.command(line, Serial)) printHelp();
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
            platform::watchdogFeed();
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

    String url = hiveUrl;
    if (url.endsWith("/")) url.remove(url.length() - 1);
    String response;
    const int status = platform::httpPost(url + "/api/provision/enroll", "application/json",
                                          reinterpret_cast<const uint8_t*>(body.c_str()), body.length(), {}, &response);
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
