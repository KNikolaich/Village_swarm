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

// ---- A spare network (cmd/wifi mode "stage") ------------------------------------------------------------------------
// The hornet keeps a second network and uses it only when its own is gone: after a failed boot (one that never reached
// the broker) the other network is tried, and while awake it swaps every kSwapAfterMs without Wi-Fi. Wi-Fi alone is not
// enough: a network that joins but does not lead to the broker (an old guest network after the router restarted) is
// left after kNoBrokerOnSpareMs, and the main one after kNoBrokerOnMainMs (longer: there the hive itself may be down).
// The network that reaches the broker becomes the main one (the old one is kept as the spare). cmd/wifi mode "forget"
// drops the spare.

constexpr uint32_t kSwapAfterMs = 45000;
constexpr uint32_t kNoBrokerOnSpareMs = 120000;
constexpr uint32_t kNoBrokerOnMainMs = 300000;

// Boots alternate networks while they keep failing: 0 failed boots before this one -> main, 1 -> spare, 2 -> main ...
inline bool useSpareAtBoot(bool spareStaged, uint32_t failedBootsBefore) { return spareStaged && (failedBootsBefore % 2 == 1); }

// An awake hornet tries the other network after kSwapAfterMs without Wi-Fi, or with Wi-Fi but without the broker for
// kNoBrokerOnSpareMs on the spare / kNoBrokerOnMainMs on the main network (never during a switch trial).
inline bool swapNetworks(bool spareStaged, bool trialRunning, bool wifiConnected, uint32_t wifiDownForMs,
                         uint32_t brokerDownForMs, bool usingSpare) {
    if (!spareStaged || trialRunning) return false;
    if (!wifiConnected) return wifiDownForMs >= kSwapAfterMs;
    return brokerDownForMs >= (usingSpare ? kNoBrokerOnSpareMs : kNoBrokerOnMainMs);
}

// The spare reached the broker: it becomes the main network.
inline bool promoteSpare(bool usingSpare, bool brokerReached) { return usingSpare && brokerReached; }

}  // namespace wifichange
}  // namespace hornet
