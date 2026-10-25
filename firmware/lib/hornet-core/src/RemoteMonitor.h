#pragma once
// Decisions of a remote monitor (an e-ink panel far away that reads its picture over HTTPS, contracts/monitor-http.md),
// kept free of Arduino so the host tests cover them. Header only: the panel's firmware uses it without the rest of
// hornet-core (no MQTT there).
#include <cstddef>
#include <cstdint>
#include <string>

namespace hornet::remote {

constexpr int kDefaultIntervalMin = 10;
constexpr int kMinIntervalMin = 5;
constexpr int kMaxIntervalMin = 120;
// Offline for this long: the old picture gets a "no link" banner, then again every kBannerEveryMin.
constexpr uint32_t kBannerAfterMin = 30;
constexpr uint32_t kBannerEveryMin = 60;

/// Bytes of a frame.bin: one bit a pixel, rows padded to whole bytes.
constexpr size_t frameBytes(int width, int height) {
    return width <= 0 || height <= 0 ? 0 : static_cast<size_t>((width + 7) / 8) * static_cast<size_t>(height);
}

inline std::string trim(const std::string& s) {
    size_t b = 0, e = s.size();
    while (b < e && (s[b] == ' ' || s[b] == '\t' || s[b] == '\r' || s[b] == '\n')) b++;
    while (e > b && (s[e - 1] == ' ' || s[e - 1] == '\t' || s[e - 1] == '\r' || s[e - 1] == '\n')) e--;
    return s.substr(b, e - b);
}

/// The hive's address as people paste it ("https://hive.x.keenetic.pro/", with or without a path to the frame).
/// Empty when it is not an http(s) address.
inline std::string frameUrl(const std::string& base) {
    std::string url = trim(base);
    if (url.rfind("https://", 0) != 0 && url.rfind("http://", 0) != 0) return "";
    static const std::string kJson = "/api/monitor/v1/frame";
    static const std::string kBin = "/api/monitor/v1/frame.bin";
    if (url.size() >= kBin.size() && url.compare(url.size() - kBin.size(), kBin.size(), kBin) == 0) return url;
    if (url.size() >= kJson.size() && url.compare(url.size() - kJson.size(), kJson.size(), kJson) == 0) return url + ".bin";
    if (url.size() <= url.find("//") + 2 || url[url.find("//") + 2] == '/') return "";  // no host
    while (url.back() == '/') url.pop_back();
    return url + kBin;
}

inline bool isHttps(const std::string& url) { return url.rfind("https://", 0) == 0; }

inline int clampInterval(int minutes) {
    if (minutes <= 0) return kDefaultIntervalMin;
    return minutes < kMinIntervalMin ? kMinIntervalMin : minutes > kMaxIntervalMin ? kMaxIntervalMin : minutes;
}

/// Whether the old picture should be redrawn with the "no link" banner now. bannerAtMin: offline minutes when it was
/// last drawn, 0 = not yet.
inline bool bannerDue(uint32_t offlineMin, uint32_t bannerAtMin) {
    if (offlineMin < kBannerAfterMin) return false;
    return bannerAtMin == 0 || offlineMin - bannerAtMin >= kBannerEveryMin;
}

enum class Answer {
    Draw,      // a new picture of the expected size
    Keep,      // 304: nothing changed
    BadToken,  // 401/403: no use asking again until the token is replaced
    NotDrawn,  // 404: the hive does not draw this screen (wrong board on the monitor's page)
    Retry,     // anything else: network, 429, 5xx, a picture of the wrong size
};

inline Answer classify(int httpCode, size_t bytes, int width, int height, int expectWidth, int expectHeight) {
    switch (httpCode) {
        case 200:
            return width == expectWidth && height == expectHeight && bytes == frameBytes(width, height) ? Answer::Draw : Answer::Retry;
        case 304: return Answer::Keep;
        case 401:
        case 403: return Answer::BadToken;
        case 404: return Answer::NotDrawn;
        default: return Answer::Retry;
    }
}

}  // namespace hornet::remote
