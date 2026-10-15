#pragma once

// Switching a running hornet to another Wi-Fi network without a cable (contracts/mqtt-topics.md "Switching Wi-Fi").
// The new credentials are kept NEXT to the old ones and only become the hornet's own once it has reached the broker
// over them; otherwise the old network comes back. The decisions live here, Arduino-free, so native tests cover them.

#include <cstdint>
#include <string>

namespace hornet {
namespace wifichange {

// A trial lasts this long from boot; a deep sleeper gives up sooner, before its own 30 s awake limit puts it to sleep.
constexpr uint32_t kTrialMs = 60000;
constexpr uint32_t kSleepyTrialMs = 27000;

// SSID of 1..32 bytes; the passphrase empty (open network) or 8..63 characters (WPA), as the contract says.
inline bool validCredentials(const std::string& ssid, const std::string& pass) {
    if (ssid.empty() || ssid.size() > 32) return false;
    return pass.empty() || (pass.size() >= 8 && pass.size() <= 63);
}

enum class Trial {
    None,      // no change in progress
    Waiting,   // trying the new network, not at the broker yet
    Commit,    // reached the broker over the new network: it is the hornet's own now
    RollBack,  // time is up without the broker: forget the new network, go back to the old one
};

inline Trial decide(bool pending, bool online, uint32_t elapsedMs, bool sleepy) {
    if (!pending) return Trial::None;
    if (online) return Trial::Commit;
    return elapsedMs >= (sleepy ? kSleepyTrialMs : kTrialMs) ? Trial::RollBack : Trial::Waiting;
}

// The command asks for what the hornet already uses: nothing to do (and no reason to risk a restart).
inline bool sameAsCurrent(const std::string& ssid, const std::string& pass, const std::string& currentSsid, const std::string& currentPass) {
    return ssid == currentSsid && pass == currentPass;
}

}  // namespace wifichange
}  // namespace hornet
