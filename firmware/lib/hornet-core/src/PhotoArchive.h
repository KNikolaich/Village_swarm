#pragma once

// Arduino-free rules for the guard-cam SD card (spec 5.4): every photo is written to /outbox before it is
// uploaded and moved to /archive/<bucket>/ once delivered; the oldest archive buckets make room for new photos.
// Photo ids start with a ULID, so name order is time order (ids from an unsynced clock start with zeros and
// sort first, which makes them the first to go).

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace hornet {

// Free space kept on the card: 5% of it, but never under 32 MB.
inline uint64_t sdReserveBytes(uint64_t totalBytes) {
    const uint64_t floor = 32ULL * 1024 * 1024;
    const uint64_t share = totalBytes / 20;
    return share > floor ? share : floor;
}

// True when writing `incoming` more bytes would eat into the reserve.
inline bool sdNeedsRoom(uint64_t totalBytes, uint64_t usedBytes, uint64_t incoming) {
    const uint64_t freeBytes = usedBytes < totalBytes ? totalBytes - usedBytes : 0;
    return freeBytes < sdReserveBytes(totalBytes) + incoming;
}

// Archive bucket of a photo: the first 6 ULID characters, about 17.5 minutes of photos. FAT allows ~16k long
// names per directory, so one flat folder would fill up long before a large card does.
constexpr size_t kArchiveBucketChars = 6;

inline std::string archiveBucket(const std::string& photoId) {
    if (photoId.size() < kArchiveBucketChars) return std::string(kArchiveBucketChars, '0');
    return photoId.substr(0, kArchiveBucketChars);
}

// Keeps the `limit` smallest names seen so far, sorted ascending: one directory scan finds a batch to delete.
inline void keepOldest(std::vector<std::string>& oldest, const std::string& name, size_t limit) {
    if (limit == 0) return;
    size_t at = oldest.size();
    while (at > 0 && name < oldest[at - 1]) --at;
    if (at >= limit) return;
    oldest.insert(oldest.begin() + static_cast<std::ptrdiff_t>(at), name);
    if (oldest.size() > limit) oldest.pop_back();
}

// Upload attempts of a photo that has no copy on the card, and the pause before each: now, ~2 s, ~5 s.
constexpr int kUploadAttemptsWithoutSd = 3;

inline uint32_t uploadRetryPauseMs(int attempt) {
    if (attempt <= 0) return 0;
    return attempt == 1 ? 2000 : 5000;
}

// HTTP status of an upload: 2xx stored, or a 4xx that retrying cannot fix (contracts/http-ingest.md).
// 401 (token), 408 and 429 are worth another try; so are 5xx and network errors (negative codes).
inline bool uploadSettled(int code) {
    if (code >= 200 && code < 300) return true;
    return code >= 400 && code < 500 && code != 401 && code != 408 && code != 429;
}

}  // namespace hornet
