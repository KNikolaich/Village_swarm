#pragma once

// Two brokers, home and city (spec 4.5, 11.3). An awake hornet that cannot reach home for kHomeDownBeforeCityMs while its
// Wi-Fi is up moves to the city broker, and every kHomeProbeEveryMs it tries home again for kHomeProbeWindowMs: if home
// answers it stays there, otherwise it goes back to the city. Pure logic with the times passed in, so it is tested natively.
// "Home" is the broker the hornet was enrolled with, "city" the other hive's: a hornet of the city hive that is carried to the
// dacha has the dacha as its "city" and uses short times (mqtt_city after_s / probe_s), so it settles on whichever hive it reaches.

#include <cstdint>

namespace hornet {
namespace failover {

constexpr uint32_t kHomeDownBeforeCityMs = 180000;  // 3 minutes without home (spec 4.5)
constexpr uint32_t kHomeProbeEveryMs = 300000;      // then every 5 minutes a look at whether home is back
constexpr uint32_t kHomeProbeWindowMs = 25000;      // how long one look waits for home to answer

// "Home" below is the PREFERRED broker, "City" the other one. Which of the two real brokers each is, the hornet's setting says
// (config prefer_hive): by default the preferred is the one it was enrolled with.
enum class Broker : uint8_t { Home, City };

// The real broker in use: true for the second one (config mqtt_city), false for the first (the enrollment's).
inline bool usesSecond(Broker applied, bool preferSecond, bool hasSecond) {
    if (!hasSecond) return false;
    return (applied == Broker::City) != preferSecond;
}

// How long home may stay silent before the other broker is used, and how often home is looked at from there (milliseconds).
struct Times {
    uint32_t downBeforeCityMs = kHomeDownBeforeCityMs;
    uint32_t probeEveryMs = kHomeProbeEveryMs;
};

struct State {
    Broker broker = Broker::Home;
    bool probing = false;   // on home, only to see whether it answers (a way back from the city)
    uint32_t sinceMs = 0;   // when the current phase began: home not answering, city connection, or the probe
};

// Called often with the clock and what the network says; returns the broker to use now and updates the state.
//   wifiUp: the Wi-Fi is connected (a lost Wi-Fi says nothing about home)
//   connected: connected to the broker in use
//   hasCity: the hive has told this hornet where the city broker is (config mqtt_city)
inline Broker step(State& s, uint32_t nowMs, bool wifiUp, bool connected, bool hasCity, const Times& times = Times()) {
    if (!hasCity) {
        s.broker = Broker::Home;
        s.probing = false;
        if (connected || !wifiUp) s.sinceMs = nowMs;
        return s.broker;
    }
    if (s.broker == Broker::Home && !s.probing) {
        if (connected || !wifiUp) {
            s.sinceMs = nowMs;
        } else if (nowMs - s.sinceMs >= times.downBeforeCityMs) {
            s.broker = Broker::City;
            s.sinceMs = nowMs;
        }
    } else if (s.broker == Broker::City) {
        if (nowMs - s.sinceMs >= times.probeEveryMs) {
            s.broker = Broker::Home;
            s.probing = true;
            s.sinceMs = nowMs;
        }
    } else {  // probing home
        if (connected) {
            s.probing = false;  // home is back: stay
            s.sinceMs = nowMs;
        } else if (nowMs - s.sinceMs >= kHomeProbeWindowMs) {
            s.probing = false;
            s.broker = Broker::City;
            s.sinceMs = nowMs;
        }
    }
    return s.broker;
}

}  // namespace failover
}  // namespace hornet
