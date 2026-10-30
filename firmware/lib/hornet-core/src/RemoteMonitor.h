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

// ------------------------------------------------------------------ panels with tabs (contracts/monitor-http.md, tabs)

/// The bottom of every tab: the cells of the tab bar, the shown one inverted; the panel inverts the one under its cursor.
constexpr int kTabBarHeight = 22;

/// The address of one tab of the picture; <sleep>: the picture for a panel put to sleep (the tab is the one it showed).
inline std::string tabUrl(const std::string& base, int tab, bool sleep = false) {
    const std::string url = frameUrl(base);
    return url.empty() ? url : url + "?tab=" + std::to_string(tab) + (sleep ? "&sleep=1" : "");
}

/// The address of the picture for a panel without tabs that is switched off: the person's sleep picture, 404 without one.
inline std::string sleepUrl(const std::string& base) {
    const std::string url = frameUrl(base);
    return url.empty() ? url : url + "?sleep=1";
}

/// The tab a step of the rocker leads to, going round at the ends.
inline int stepTab(int tab, int step, int count) {
    if (count <= 0) return 0;
    return ((tab + step) % count + count) % count;
}

/// The most tabs a panel keeps in its flash.
constexpr int kMaxTabs = 4;

/// How many tabs the hive says the board has (X-Frame-Tabs); a missing or odd answer keeps <current>.
inline int tabCount(int said, int current) { return said >= 1 && said <= kMaxTabs ? said : current; }

/// Seconds to the next read: the panel's interval, or sooner while the hive monitors it (X-Frame-Refresh, at least a minute).
inline uint32_t nextReadSeconds(int intervalMin, uint32_t refreshS) {
    const uint32_t own = static_cast<uint32_t>(intervalMin) * 60;
    if (refreshS == 0) return own;
    const uint32_t monitored = refreshS < 60 ? 60 : refreshS;
    return monitored < own ? monitored : own;
}

/// How long a tab that was read counts as fresh when the person only switches to it (no need to ask the hive again): five minutes at most,
/// less when the hive monitors the panel quicker (X-Frame-Refresh, at least a minute). Monitored every 10 minutes, a tab read 7 minutes ago is still not fresh.
constexpr uint32_t kFreshSeconds = 300;
inline uint32_t freshSeconds(uint32_t refreshS) {
    if (refreshS == 0) return kFreshSeconds;
    const uint32_t pace = refreshS < 60 ? 60 : refreshS;
    return pace < kFreshSeconds ? pace : kFreshSeconds;
}

/// A tab read less than <fresh> seconds ago (both times in the hive's unix seconds; 0 = not known: then it is not fresh).
inline bool isFresh(uint32_t now, uint32_t readAt, uint32_t fresh) { return now != 0 && readAt != 0 && now >= readAt && now - readAt < fresh; }

/// Seconds to sleep so that a tab is read when it is due: the usual pause less its age, at least a minute (the pause itself when its age is not known).
inline uint32_t secondsToDue(uint32_t pause, uint32_t now, uint32_t readAt) {
    if (now == 0 || readAt == 0 || now < readAt) return pause;
    const uint32_t age = now - readAt;
    const uint32_t left = pause > age ? pause - age : 0;
    return left < 60 ? 60 : left;
}

/// Seconds to sleep so that the panel wakes just after the minute turns (a second of slack for the hive's clock).
inline uint32_t secondsToNextMinute(uint32_t unixSeconds) { return 60 - unixSeconds % 60 + 1; }

/// A cell of the tab bar in the turned screen: x, y, width, height.
struct Cell {
    int x, y, w, h;
};

/// Reads X-Frame-Tab-Weights ("2,3,3") into <weights>; returns how many it found (0 for a missing or odd text: equal cells).
inline int parseTabWeights(const char* text, uint8_t* weights, int maxCount) {
    int count = 0;
    int value = 0;
    bool digits = false;
    for (const char* c = text ? text : ""; ; c++) {
        if (*c >= '0' && *c <= '9') {
            value = value * 10 + (*c - '0');
            digits = true;
            if (value > 100) return 0;
        } else if (*c == ',' || *c == '\0') {
            if (!digits || value == 0 || count >= maxCount) return 0;
            weights[count++] = static_cast<uint8_t>(value);
            value = 0;
            digits = false;
            if (*c == '\0') break;
        } else if (*c != ' ') {
            return 0;
        }
    }
    return count;
}

/// The cell of <tab> among <count>: equal widths, or as <weights> (<count> of them) say.
inline Cell tabCell(int tab, int count, int width, int height, const uint8_t* weights = nullptr) {
    if (count <= 0) return {0, height - kTabBarHeight, width, kTabBarHeight};
    int total = count, before = tab, through = tab + 1;
    if (weights) {
        total = 0;
        before = 0;
        through = 0;
        for (int i = 0; i < count; i++) {
            if (i == tab) before = total;
            total += weights[i];
            if (i == tab) through = total;
        }
    }
    const int left = width * before / total;
    const int right = tab == count - 1 ? width : width * through / total;
    return {left, height - kTabBarHeight, right - left, kTabBarHeight};
}

/// The battery in millivolts from the ADC behind a 1:2 divider, or -1 when nothing sensible is there (no divider soldered).
inline int batteryMv(int pinMv) {
    const int mv = pinMv * 2;
    return mv >= 2500 && mv <= 4500 ? mv : -1;
}

/// Seconds since 1970 from an HTTP Date header ("Tue, 27 Oct 2026 20:41:02 GMT"), 0 when it does not read.
inline uint32_t httpDate(const std::string& date) {
    static const char* kMonths[] = {"Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"};
    const size_t comma = date.find(", ");
    if (comma == std::string::npos || date.size() < comma + 22) return 0;
    const std::string s = date.substr(comma + 2);  // "27 Oct 2026 20:41:02 GMT"
    auto num = [&](size_t at, size_t len) {
        int v = 0;
        for (size_t i = at; i < at + len; i++) {
            if (s[i] < '0' || s[i] > '9') return -1;
            v = v * 10 + (s[i] - '0');
        }
        return v;
    };
    const int day = num(0, 2), year = num(7, 4), hh = num(12, 2), mm = num(15, 2), ss = num(18, 2);
    int month = -1;
    for (int i = 0; i < 12; i++)
        if (s.compare(3, 3, kMonths[i]) == 0) month = i + 1;
    if (day < 1 || year < 2020 || month < 0 || hh < 0 || mm < 0 || ss < 0) return 0;
    // days from the civil date (Howard Hinnant's algorithm)
    const int y = year - (month <= 2);
    const int era = y / 400;
    const unsigned yoe = static_cast<unsigned>(y - era * 400);
    const unsigned doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
    const unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    const int64_t days = static_cast<int64_t>(era) * 146097 + static_cast<int64_t>(doe) - 719468;
    return static_cast<uint32_t>(days * 86400 + hh * 3600 + mm * 60 + ss);
}

}  // namespace hornet::remote
