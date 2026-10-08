#pragma once

// Node settings kept in NVS (spec 5.2 Config). Until provisioning (build step 10) they are entered in the
// serial console: `set ssid ...`, `set mqtt 192.168.20.2`, `set id guard-gate1`... No secrets in the source.

#include <Arduino.h>

namespace hornet {

struct Settings {
    String ssid;
    String pass;
    String mqttHost;
    uint16_t mqttPort = 1883;
    String mqttUser;
    String mqttPass;
    String deviceId;
    String uploadUrl;    // http://hive:8443, POST {uploadUrl}/api/ingest/photo
    String uploadToken;  // Bearer token for photo uploads
    String ntp = "pool.ntp.org";
    uint32_t boot = 0;   // incremented on every start (envelope "boot")

    bool complete() const { return ssid.length() && mqttHost.length() && deviceId.length(); }

    void load();
    void save() const;
    void saveBoot() const;
    void clear();

    // Raw retained config JSON from the hive, so the hornet works after a reboot without the broker.
    String loadConfig() const;
    void saveConfig(const String& json) const;

    // Handles one console line; returns false for unknown input.
    bool command(const String& line, Print& out);
    void print(Print& out) const;
};

}  // namespace hornet
