#include "Settings.h"

#include <Preferences.h>

#include "Topics.h"

namespace hornet {

namespace {
constexpr const char* kNs = "hornet";
}

void Settings::load() {
    Preferences p;
    p.begin(kNs, true);
    ssid = p.getString("ssid", "");
    pass = p.getString("pass", "");
    mqttHost = p.getString("mqtt", "");
    mqttPort = p.getUShort("mqttport", 1883);
    mqttUser = p.getString("mqttuser", "");
    mqttPass = p.getString("mqttpass", "");
    deviceId = p.getString("id", "");
    uploadUrl = p.getString("upload", "");
    uploadToken = p.getString("token", "");
    ntp = p.getString("ntp", "pool.ntp.org");
    boot = p.getUInt("boot", 0);
    p.end();
}

void Settings::save() const {
    Preferences p;
    p.begin(kNs, false);
    p.putString("ssid", ssid);
    p.putString("pass", pass);
    p.putString("mqtt", mqttHost);
    p.putUShort("mqttport", mqttPort);
    p.putString("mqttuser", mqttUser);
    p.putString("mqttpass", mqttPass);
    p.putString("id", deviceId);
    p.putString("upload", uploadUrl);
    p.putString("token", uploadToken);
    p.putString("ntp", ntp);
    p.end();
}

void Settings::saveBoot() const {
    Preferences p;
    p.begin(kNs, false);
    p.putUInt("boot", boot);
    p.end();
}

void Settings::clear() {
    Preferences p;
    p.begin(kNs, false);
    p.clear();
    p.end();
    *this = Settings{};
}

String Settings::loadConfig() const {
    Preferences p;
    p.begin(kNs, true);
    String json = p.getString("config", "");
    p.end();
    return json;
}

void Settings::saveConfig(const String& json) const {
    Preferences p;
    p.begin(kNs, false);
    p.putString("config", json);
    p.end();
}

bool Settings::command(const String& line, Print& out) {
    // "set <key> <value>"; the value may contain spaces (Wi-Fi names do).
    if (!line.startsWith("set ")) return false;
    const int sp = line.indexOf(' ', 4);
    if (sp < 0) return false;
    const String key = line.substring(4, sp);
    const String value = line.substring(sp + 1);

    if (key == "ssid") ssid = value;
    else if (key == "pass") pass = value;
    else if (key == "mqtt") {
        const int colon = value.indexOf(':');
        mqttHost = colon < 0 ? value : value.substring(0, colon);
        if (colon >= 0) mqttPort = static_cast<uint16_t>(value.substring(colon + 1).toInt());
    } else if (key == "mqttuser") mqttUser = value;
    else if (key == "mqttpass") mqttPass = value;
    else if (key == "id") {
        if (!topics::isValidDeviceId(value.c_str())) {
            out.println("bad id: lowercase like guard-gate1, max 32 chars");
            return true;
        }
        deviceId = value;
    } else if (key == "upload") uploadUrl = value;
    else if (key == "token") uploadToken = value;
    else if (key == "ntp") ntp = value;
    else return false;

    save();
    out.printf("saved %s; `reboot` to apply\n", key.c_str());
    return true;
}

void Settings::print(Print& out) const {
    out.printf("id=%s ssid=%s pass=%s mqtt=%s:%u mqttuser=%s upload=%s token=%s ntp=%s boot=%u\n",
               deviceId.c_str(), ssid.c_str(), pass.length() ? "***" : "", mqttHost.c_str(), mqttPort,
               mqttUser.c_str(), uploadUrl.c_str(), uploadToken.length() ? "***" : "", ntp.c_str(), boot);
}

}  // namespace hornet
