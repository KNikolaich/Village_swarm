#pragma once

// RAM buffer for QoS 1 messages (events, acks) while the broker is unreachable (spec 4.5): the newest N
// are kept and replayed in order with their original ids; the backend drops duplicates by id.

#include <cstddef>
#include <string>

namespace hornet {

struct OutboxMessage {
    std::string topic;
    std::string payload;
};

template <size_t N = 50>
class Outbox {
public:
    // Returns false when an old message had to be dropped to make room.
    bool push(const std::string& topic, const std::string& payload) {
        const bool dropped = count_ == N;
        if (dropped) {
            head_ = (head_ + 1) % N;
            --count_;
        }
        items_[(head_ + count_) % N] = OutboxMessage{topic, payload};
        ++count_;
        return !dropped;
    }

    bool empty() const { return count_ == 0; }
    size_t size() const { return count_; }
    const OutboxMessage& front() const { return items_[head_]; }

    void pop() {
        if (count_ == 0) return;
        items_[head_] = OutboxMessage{};
        head_ = (head_ + 1) % N;
        --count_;
    }

private:
    OutboxMessage items_[N];
    size_t head_ = 0;
    size_t count_ = 0;
};

// Reconnect delay 1 -> 2 -> 4 ... 60 s (spec 4.5); jitter is added by the caller.
inline unsigned long backoffMs(unsigned attempt) {
    unsigned long ms = 1000UL << (attempt > 6 ? 6 : attempt);
    return ms > 60000UL ? 60000UL : ms;
}

// PIR anti-bounce: at most one trigger per cooldown (spec 5.4, default 20 s).
class Cooldown {
public:
    bool tryFire(unsigned long nowMs, unsigned long cooldownMs) {
        if (fired_ && nowMs - last_ < cooldownMs) return false;
        fired_ = true;
        last_ = nowMs;
        return true;
    }

private:
    bool fired_ = false;
    unsigned long last_ = 0;
};

}  // namespace hornet
