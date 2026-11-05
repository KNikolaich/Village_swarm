#pragma once

// Two brokers, home and city (spec 4.5, 11.3). An awake hornet that cannot reach home for kHomeDownBeforeCityMs while its
// Wi-Fi is up moves to the city broker, and every kHomeProbeEveryMs it tries home again for kHomeProbeWindowMs: if home
// answers it stays there, otherwise it goes back to the city. Pure logic with the times passed in, so it is tested natively.

#include <cstdint>

namespace hornet {
namespace failover {

constexpr uint32_t kHomeDownBeforeCityMs = 180000;  // 3 minutes without home (spec 4.5)
constexpr uint32_t kHomeProbeEveryMs = 300000;      // then every 5 minutes a look at whether home is back
constexpr uint32_t kHomeProbeWindowMs = 25000;      // how long one look waits for home to answer

enum class Broker : uint8_t { Home, City };

struct State {
    Broker broker = Broker::Home;
    bool probing = false;   // on home, only to see whether it answers (a way back from the city)
    uint32_t sinceMs = 0;   // when the current phase began: home not answering, city connection, or the probe
};

// Called often with the clock and what the network says; returns the broker to use now and updates the state.
//   wifiUp: the Wi-Fi is connected (a lost Wi-Fi says nothing about home)
//   connected: connected to the broker in use
//   hasCity: the hive has told this hornet where the city broker is (config mqtt_city)
inline Broker step(State& s, uint32_t nowMs, bool wifiUp, bool connected, bool hasCity) {
    if (!hasCity) {
        s.broker = Broker::Home;
        s.probing = false;
        if (connected || !wifiUp) s.sinceMs = nowMs;
        return s.broker;
    }
    if (s.broker == Broker::Home && !s.probing) {
        if (connected || !wifiUp) {
            s.sinceMs = nowMs;
        } else if (nowMs - s.sinceMs >= kHomeDownBeforeCityMs) {
            s.broker = Broker::City;
            s.sinceMs = nowMs;
        }
    } else if (s.broker == Broker::City) {
        if (nowMs - s.sinceMs >= kHomeProbeEveryMs) {
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
