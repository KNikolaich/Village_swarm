#pragma once

// MQTT topic hierarchy, contract v1. Source of truth: contracts/mqtt-topics.md.
// Must stay in sync with backend/src/Hive.Contracts/Mqtt/Topics.cs.
// Header-only and Arduino-free so it builds in the native test env.

#include <cstddef>
#include <string>

namespace hornet {
namespace topics {

constexpr const char* kPrefix = "vs/v1";
constexpr size_t kMaxDeviceIdLength = 32;

// ^[a-z][a-z0-9]*(-[a-z0-9]+)*$, at most 32 chars.
inline bool isValidDeviceId(const std::string& id) {
    if (id.empty() || id.size() > kMaxDeviceIdLength) return false;
    if (id[0] < 'a' || id[0] > 'z') return false;
    char prev = id[0];
    for (char c : id) {
        bool alnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
        if (!alnum && c != '-') return false;
        if (c == '-' && prev == '-') return false;
        prev = c;
    }
    return id.back() != '-';
}

// Topic segment (metric, event type, command name): [a-z0-9_]+.
inline bool isValidSegment(const std::string& s) {
    if (s.empty()) return false;
    for (char c : s) {
        bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
        if (!ok) return false;
    }
    return true;
}

inline std::string device(const std::string& id) { return std::string(kPrefix) + "/dev/" + id; }

inline std::string status(const std::string& id) { return device(id) + "/status"; }
inline std::string info(const std::string& id) { return device(id) + "/info"; }
inline std::string health(const std::string& id) { return device(id) + "/health"; }
inline std::string state(const std::string& id) { return device(id) + "/state"; }
inline std::string log(const std::string& id) { return device(id) + "/log"; }
inline std::string config(const std::string& id) { return device(id) + "/config"; }
inline std::string tele(const std::string& id, const std::string& metric) { return device(id) + "/tele/" + metric; }
inline std::string event(const std::string& id, const std::string& type) { return device(id) + "/event/" + type; }
inline std::string cmd(const std::string& id, const std::string& name) { return device(id) + "/cmd/" + name; }
inline std::string cmdAck(const std::string& id, const std::string& name) { return cmd(id, name) + "/ack"; }

// Subscriptions a hornet needs (spec 4.2, ACL).
inline std::string cmdWildcard(const std::string& id) { return device(id) + "/cmd/#"; }
inline std::string hiveWildcard() { return std::string(kPrefix) + "/hive/#"; }

}  // namespace topics
}  // namespace hornet
