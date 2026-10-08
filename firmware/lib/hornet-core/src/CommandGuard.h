#pragma once

// Command rules from the contract (spec 4.4): a command older than ts + ttl_s is rejected as "expired",
// and a repeated cid is not executed again (the stored ack is re-sent). Arduino-free for native tests.

#include <cstddef>
#include <cstdint>
#include <string>

namespace hornet {

enum class CommandCheck { Execute, Expired, Duplicate };

template <size_t N = 16>
class CommandGuard {
public:
    // nowMs is 0 while the clock is not synced: expiry cannot be judged then, the command runs.
    CommandCheck check(const std::string& cid, uint64_t tsMs, uint32_t ttlS, uint64_t nowMs) const {
        if (find(cid) >= 0) return CommandCheck::Duplicate;
        if (nowMs != 0 && tsMs != 0 && nowMs > tsMs + static_cast<uint64_t>(ttlS) * 1000ULL) return CommandCheck::Expired;
        return CommandCheck::Execute;
    }

    // Remembers the ack payload sent for cid (the oldest entry is forgotten when full).
    void remember(const std::string& cid, const std::string& topic, const std::string& ack) {
        cids_[next_] = cid;
        topics_[next_] = topic;
        acks_[next_] = ack;
        next_ = (next_ + 1) % N;
    }

    // Stored ack for a duplicate, or nullptr.
    const std::string* ackFor(const std::string& cid, const std::string** topic = nullptr) const {
        const int i = find(cid);
        if (i < 0) return nullptr;
        if (topic) *topic = &topics_[i];
        return &acks_[i];
    }

private:
    int find(const std::string& cid) const {
        for (size_t i = 0; i < N; ++i)
            if (!cids_[i].empty() && cids_[i] == cid) return static_cast<int>(i);
        return -1;
    }

    std::string cids_[N];
    std::string topics_[N];
    std::string acks_[N];
    size_t next_ = 0;
};

}  // namespace hornet
