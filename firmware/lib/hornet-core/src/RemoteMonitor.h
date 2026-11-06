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

// ------------------------------------------------------------------ no link (docs/display.md, "Нет связи")

/// Why a read failed, as the screen says it.
enum class Offline : uint8_t {
    None,
    NoWifi,      // no network of the panel's answered
    NoHive,      // Wi-Fi is up, the hive does not answer (timeout, TLS, 429, 5xx)
    WrongFrame,  // the hive answered with a picture of another size (another board chosen on the monitor's page)
};

inline const char* causeText(Offline cause) {
    switch (cause) {
        case Offline::NoWifi: return "нет Wi-Fi";
        case Offline::NoHive: return "Улей не отвечает";
        case Offline::WrongFrame: return "не тот кадр";
        default: return "";
    }
}

// ------------------------------------------------------------------ why the hive did not answer

/// What went wrong reaching the hive, as far as the panel can tell (the banner says it in ASCII; the log and "show" carry the detail).
enum class Fail : uint8_t {
    None,
    NoWifi,   // not joined to any network
    Dns,      // the hive's name did not resolve
    Tls,      // the handshake failed (certificate, clock, a middlebox)
    Refused,  // the connection was refused or reset at once
    Timeout,  // nothing answered in time (connect or read)
    Http,     // the hive answered with a status that is not a picture
    Frame,    // 200 with a picture of the wrong size
    Other,
};

/// The host of an address ("https://h.example:8088/x" -> "h.example"); empty when there is none.
inline std::string hostOf(const std::string& url) {
    const size_t scheme = url.find("://");
    if (scheme == std::string::npos) return "";
    const size_t begin = scheme + 3;
    size_t end = url.find_first_of("/?#", begin);
    if (end == std::string::npos) end = url.size();
    std::string authority = url.substr(begin, end - begin);
    const size_t at = authority.rfind('@');
    if (at != std::string::npos) authority = authority.substr(at + 1);
    if (!authority.empty() && authority[0] == '[') {  // an IPv6 literal
        const size_t close = authority.find(']');
        return close == std::string::npos ? "" : authority.substr(1, close - 1);
    }
    const size_t colon = authority.find(':');
    return colon == std::string::npos ? authority : authority.substr(0, colon);
}

/// HTTPClient's negative codes (Arduino-ESP32 3.x): -1 refused, -4 not connected, -5 connection lost, -11 read timeout.
constexpr int kHttpRefused = -1, kHttpNotConnected = -4, kHttpLost = -5, kHttpReadTimeout = -11;

/// Why a read failed. <dnsOk>: the name resolved; <tlsError>: the TLS library's error code (0 when none); <elapsedMs> and
/// <connectTimeoutMs>: how long the attempt took and how long it was allowed (a refusal comes at once, a timeout takes all of it).
inline Fail classifyFailure(int httpCode, bool dnsOk, int tlsError, uint32_t elapsedMs, uint32_t connectTimeoutMs) {
    if (!dnsOk) return Fail::Dns;
    if (httpCode == 200) return Fail::Frame;
    if (httpCode > 0) return Fail::Http;
    if (tlsError != 0) return Fail::Tls;
    if (httpCode == kHttpReadTimeout) return Fail::Timeout;
    if (httpCode == kHttpRefused || httpCode == kHttpNotConnected || httpCode == kHttpLost)
        return elapsedMs + 500 >= connectTimeoutMs ? Fail::Timeout : Fail::Refused;
    return Fail::Other;
}

/// Short ASCII text for the e-ink banner (no Russian: the small fonts of the panel may lack it, and the log is read by people with a cable).
inline std::string failureText(Fail fail, int httpCode = 0) {
    switch (fail) {
        case Fail::NoWifi: return "no WiFi";
        case Fail::Dns: return "DNS failed";
        case Fail::Tls: return "TLS failed";
        case Fail::Refused: return "refused";
        case Fail::Timeout: return "timeout";
        case Fail::Http: return "HTTP " + std::to_string(httpCode);
        case Fail::Frame: return "bad frame";
        case Fail::Other: return "no reply " + std::to_string(httpCode);
        default: return "";
    }
}

/// "A0:B7:65:12:34:56" from the six bytes of the Wi-Fi station address (the factory address on the chip, lowest byte first).
inline std::string macText(const uint8_t* bytes) {
    static const char kHex[] = "0123456789ABCDEF";
    std::string out;
    for (int i = 0; i < 6; i++) {
        if (i) out += ':';
        out += kHex[bytes[i] >> 4];
        out += kHex[bytes[i] & 15];
    }
    return out;
}

/// Minutes to the next try after <failures> failed reads in a row (1 = the first): 5, 10, 20, then 30, and never longer than
/// the panel's own interval. The first success starts it over.
inline uint32_t retryMinutes(uint32_t failures, int intervalMin) {
    const uint32_t step = failures <= 1 ? 5 : failures == 2 ? 10 : failures == 3 ? 20 : 30;
    const uint32_t own = static_cast<uint32_t>(clampInterval(intervalMin));
    return step < own ? step : own;
}

/// How old the picture on the screen is.
struct Age {
    uint32_t minutes;
    bool exact;  // from the clock; else counted from the minutes slept offline (roughly)
};

/// From the clock when both times are known (unix seconds, 0 = not known), else the minutes counted offline.
inline Age dataAge(uint32_t now, uint32_t lastOk, uint32_t offlineMin) {
    if (now != 0 && lastOk != 0 && now >= lastOk) return {(now - lastOk) / 60, true};
    return {offlineMin, false};
}

/// "40 мин", "3 ч", "2 дн"; "~" in front when the age is only counted.
inline std::string ageText(Age age) {
    const std::string about = age.exact ? "" : "~";
    if (age.minutes < 60) return about + std::to_string(age.minutes) + " мин";
    if (age.minutes < 48 * 60) return about + std::to_string(age.minutes / 60) + " ч";
    return about + std::to_string(age.minutes / (24 * 60)) + " дн";
}

/// The banner over the old picture: "нет Wi-Fi, данные 40 мин назад"; <brief> for a narrow screen: "нет Wi-Fi, 40 мин".
/// <reason>: why the hive could not be read (failureText, ASCII, e.g. "DNS failed"); the banner of a hive that does not answer says it.
inline std::string offlineBanner(Offline cause, Age age, bool brief, const std::string& reason = "") {
    std::string what = causeText(cause);
    if (cause == Offline::NoHive && !reason.empty()) what = "Улей: " + reason;
    return what + (brief ? ", " : ", данные ") + ageText(age) + (brief ? "" : " назад");
}

/// The banner (or the full message) is redrawn at most this often while the cause stays the same: e-ink flashes on every refresh.
constexpr uint32_t kBannerEveryMin = 30;

/// Whether the screen should say it now: nothing said yet (<shown> None), another cause, or what it says is kBannerEveryMin behind.
/// <shownAgeMin>: the age the screen shows.
inline bool bannerDue(Offline cause, Offline shown, uint32_t ageMin, uint32_t shownAgeMin) {
    if (cause == Offline::None) return false;
    if (shown != cause) return true;
    return ageMin >= shownAgeMin + kBannerEveryMin || ageMin < shownAgeMin;
}

/// A Li-ion cell's charge from its voltage, roughly (no load), 0..100; -1 when the voltage is not known.
inline int batteryPercent(int mv) {
    if (mv <= 0) return -1;
    static const int kMv[] = {3300, 3600, 3700, 3800, 3900, 4000, 4100, 4200};
    static const int kPct[] = {0, 10, 30, 55, 70, 80, 90, 100};
    if (mv <= kMv[0]) return 0;
    for (int i = 1; i < 8; i++)
        if (mv <= kMv[i]) return kPct[i - 1] + (kPct[i] - kPct[i - 1]) * (mv - kMv[i - 1]) / (kMv[i] - kMv[i - 1]);
    return 100;
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

// ------------------------------------------------------------------ the console window after a power-on or a reset

/// The moment after a power-on or a reset when the panel listens to its USB console before it reads (the hive's page flashes it and
/// sends the settings then). The board resets when the page opens the port, and Windows may take seconds to hand the port over,
/// so a bare 12 s is not enough: input keeps the window open for a minute, and so does a USB host that is attached (a
/// computer, not a charger). Time is the caller's millis() since the window opened; nothing here waits.
struct ConsoleWindow {
    static constexpr uint32_t kFirstMs = 12000;    // nobody there: as before
    static constexpr uint32_t kActiveMs = 60000;   // each line typed keeps it open this long
    static constexpr uint32_t kMaxMs = 300000;     // never longer than this in all: a panel left on a computer still reads
    static constexpr uint32_t kReadyEveryMs = 500; // "[monitor] ready" this often while it is open

    /// Input arrived at <nowMs>.
    void touch(uint32_t nowMs) {
        lastInputMs_ = nowMs;
        touched_ = true;
    }

    /// Whether the window is still open at <nowMs>; <hostAttached>: a USB host is there (the console port is open on the other side).
    bool open(uint32_t nowMs, bool hostAttached) const {
        if (nowMs >= kMaxMs) return false;
        if (nowMs < kFirstMs) return true;
        if (hostAttached) return true;
        return touched_ && nowMs - lastInputMs_ < kActiveMs;
    }

    /// Whether "ready" is due again; <lastReadyMs> is when it was said last (0 = never).
    static bool readyDue(uint32_t nowMs, uint32_t lastReadyMs) { return lastReadyMs == 0 || nowMs - lastReadyMs >= kReadyEveryMs; }

private:
    uint32_t lastInputMs_ = 0;
    bool touched_ = false;
};

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

// ------------------------------------------------------------------ wired mode (external power)

/// What the person chose with "set wired": the panel decides (auto), never stays awake (off), always does (on, a panel that is always plugged in).
enum class WiredMode : uint8_t { Auto = 0, Off = 1, On = 2 };

inline WiredMode parseWiredMode(const std::string& text, WiredMode fallback) {
    if (text == "auto") return WiredMode::Auto;
    if (text == "off") return WiredMode::Off;
    if (text == "on") return WiredMode::On;
    return fallback;
}

inline const char* wiredModeName(WiredMode mode) { return mode == WiredMode::Off ? "off" : mode == WiredMode::On ? "on" : "auto"; }

/// A cell held at or above this by the charger (the charge ends at 4.20 V). A cell that has only been charged also rests here for a while
/// (see wiredNow), which is why the voltage alone is not trusted when it falls.
constexpr int kWiredFullMv = 4150;
/// Rising this much between two reads of the same panel is a charger pushing current into the cell (a cell on its own only falls).
constexpr int kWiredRiseMv = 8;
/// Falling this much between two reads: no charger, whatever the level.
constexpr int kWiredFallMv = 10;

/// Whether the panel is on external power now, from what the board gives: the Waveshare ESP32-S3-ePaper-1.54 has no pin that tells VBUS or the
/// charger's state to the chip (the cell's voltage on GPIO4 is all it reads), so the signals are: a USB host on the other side of the console
/// (reliable, but only a computer says it, not a charger) and the cell's voltage between two reads (a rise is charging; a high level that
/// is not falling is a full cell on the charger; otherwise it is on the cell). <lastMv> is the previous read (0 = none), kept by the caller.
///
/// Not reliable at the top: a cell that was just charged and unplugged is also high and flat, so it counts as wired until it falls 10 mV
/// between two reads (a few reads at the panel's interval). The cost of that error is a few hours of awake time at a full cell, not a dead one.
inline bool wiredNow(WiredMode mode, int mv, int lastMv, bool usbHost) {
    if (mode == WiredMode::Off) return false;
    if (mode == WiredMode::On) return true;
    if (usbHost) return true;
    if (mv < 2500 || mv > 4500) return false;  // no divider, or nonsense: nothing to go by
    if (lastMv >= 2500 && mv <= lastMv - kWiredFallMv) return false;
    if (lastMv >= 2500 && mv >= lastMv + kWiredRiseMv) return true;
    return mv >= kWiredFullMv;
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
