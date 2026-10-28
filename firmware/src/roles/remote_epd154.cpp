// Remote monitor on the Waveshare ESP32-S3 1.54" e-paper (200x200, black and white): a panel far from the hive (the city
// flat, say) that shows the picture the hive draws for it (contracts/monitor-http.md, frame.bin). No MQTT, no enrollment:
// the monitor is added by hand on the hive's page ("Добавить удалённый монитор") and given a read-only token there.
//
// It sleeps almost all the time: wake by the timer, join Wi-Fi, ask the hive for frame.bin with the last ETag, draw a new
// picture only when it changed (304 = keep the old one), back to deep sleep. The e-paper keeps the picture without power.
// When the hive cannot be reached for half an hour, the last picture gets a "нет связи" banner, so an old number is never
// taken for a fresh one.
//
// Settings (two Wi-Fi networks, the hive's address, the token, how often to read) come in two ways:
//  - over USB, from the hive's page right after flashing: the console commands below. Nothing is typed by hand;
//  - on the panel's own Wi-Fi point: hold BOOT for 2 s (or start it with nothing set), join "VillageSwarm-xxxx", open
//    192.168.4.1. The hive's page shows a QR code with the address and the token: scan it with the phone's camera once
//    joined and the form opens filled in.
// A short press of BOOT reads the picture now; PWR turns the panel off (on battery).
//
// Console (115200, lines): set ssid|pass|ssid2|pass2|url|token|interval|rot|insecure <value>, show, go, save. "go" saves
// and reads the picture at once, answering "[monitor] frame <http code>" (200 new, 304 same) or "[monitor] wifi failed".
// "save" only saves (the panel is to stand at another Wi-Fi) and sleeps until its first read.
// After a power-on or a reset the panel waits 12 s for the console before it reads, repeating "[monitor] ready".
//
// Pins from the Waveshare examples for the V2 board (github.com/waveshareteam/ESP32-S3-ePaper-1.54).

#include <Arduino.h>
#include <DNSServer.h>
#include <GxEPD2_BW.h>
#include <HTTPClient.h>
#include <Preferences.h>
#include <SPI.h>
#include <U8g2_for_Adafruit_GFX.h>
#include <WebServer.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <driver/gpio.h>
#include <driver/rtc_io.h>
#include <esp_sleep.h>

#include "../../lib/hornet-core/src/RemoteMonitor.h"

namespace {

using namespace hornet::remote;

constexpr int kWidth = 200;
constexpr int kHeight = 200;
constexpr size_t kFrameBytes = frameBytes(kWidth, kHeight);

constexpr int kPinDc = 10, kPinCs = 11, kPinSck = 12, kPinMosi = 13, kPinRst = 9, kPinBusy = 8;
constexpr gpio_num_t kPinEpdPower = GPIO_NUM_6;   // LOW = e-paper powered
constexpr gpio_num_t kPinPowerHold = GPIO_NUM_17; // HIGH keeps the battery switched on
constexpr gpio_num_t kPinAudioOff = GPIO_NUM_42;  // HIGH = audio amplifier off
constexpr gpio_num_t kPinBoot = GPIO_NUM_0;
constexpr gpio_num_t kPinPwr = GPIO_NUM_18;
constexpr int kPinBattery = 4;  // ADC through a 1:2 divider

constexpr uint32_t kWifiTimeoutMs = 15000;
constexpr uint32_t kPortalTimeoutMs = 10UL * 60 * 1000;
constexpr uint32_t kBadTokenSleepMin = 6 * 60;  // a new token needs the settings point anyway: just look now and then
constexpr int kLowBatteryMv = 3450;

// The hive's internet entrance (KeenDNS) has a Let's Encrypt certificate: its two roots. Inside the house the hive uses
// its own CA: tick "не проверять сертификат" or use http:// there.
const char kRootCa[] = R"PEM(-----BEGIN CERTIFICATE-----
MIIFazCCA1OgAwIBAgIRAIIQz7DSQONZRGPgu2OCiwAwDQYJKoZIhvcNAQELBQAw
TzELMAkGA1UEBhMCVVMxKTAnBgNVBAoTIEludGVybmV0IFNlY3VyaXR5IFJlc2Vh
cmNoIEdyb3VwMRUwEwYDVQQDEwxJU1JHIFJvb3QgWDEwHhcNMTUwNjA0MTEwNDM4
WhcNMzUwNjA0MTEwNDM4WjBPMQswCQYDVQQGEwJVUzEpMCcGA1UEChMgSW50ZXJu
ZXQgU2VjdXJpdHkgUmVzZWFyY2ggR3JvdXAxFTATBgNVBAMTDElTUkcgUm9vdCBY
MTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAK3oJHP0FDfzm54rVygc
h77ct984kIxuPOZXoHj3dcKi/vVqbvYATyjb3miGbESTtrFj/RQSa78f0uoxmyF+
0TM8ukj13Xnfs7j/EvEhmkvBioZxaUpmZmyPfjxwv60pIgbz5MDmgK7iS4+3mX6U
A5/TR5d8mUgjU+g4rk8Kb4Mu0UlXjIB0ttov0DiNewNwIRt18jA8+o+u3dpjq+sW
T8KOEUt+zwvo/7V3LvSye0rgTBIlDHCNAymg4VMk7BPZ7hm/ELNKjD+Jo2FR3qyH
B5T0Y3HsLuJvW5iB4YlcNHlsdu87kGJ55tukmi8mxdAQ4Q7e2RCOFvu396j3x+UC
B5iPNgiV5+I3lg02dZ77DnKxHZu8A/lJBdiB3QW0KtZB6awBdpUKD9jf1b0SHzUv
KBds0pjBqAlkd25HN7rOrFleaJ1/ctaJxQZBKT5ZPt0m9STJEadao0xAH0ahmbWn
OlFuhjuefXKnEgV4We0+UXgVCwOPjdAvBbI+e0ocS3MFEvzG6uBQE3xDk3SzynTn
jh8BCNAw1FtxNrQHusEwMFxIt4I7mKZ9YIqioymCzLq9gwQbooMDQaHWBfEbwrbw
qHyGO0aoSCqI3Haadr8faqU9GY/rOPNk3sgrDQoo//fb4hVC1CLQJ13hef4Y53CI
rU7m2Ys6xt0nUW7/vGT1M0NPAgMBAAGjQjBAMA4GA1UdDwEB/wQEAwIBBjAPBgNV
HRMBAf8EBTADAQH/MB0GA1UdDgQWBBR5tFnme7bl5AFzgAiIyBpY9umbbjANBgkq
hkiG9w0BAQsFAAOCAgEAVR9YqbyyqFDQDLHYGmkgJykIrGF1XIpu+ILlaS/V9lZL
ubhzEFnTIZd+50xx+7LSYK05qAvqFyFWhfFQDlnrzuBZ6brJFe+GnY+EgPbk6ZGQ
3BebYhtF8GaV0nxvwuo77x/Py9auJ/GpsMiu/X1+mvoiBOv/2X/qkSsisRcOj/KK
NFtY2PwByVS5uCbMiogziUwthDyC3+6WVwW6LLv3xLfHTjuCvjHIInNzktHCgKQ5
ORAzI4JMPJ+GslWYHb4phowim57iaztXOoJwTdwJx4nLCgdNbOhdjsnvzqvHu7Ur
TkXWStAmzOVyyghqpZXjFaH3pO3JLF+l+/+sKAIuvtd7u+Nxe5AW0wdeRlN8NwdC
jNPElpzVmbUq4JUagEiuTDkHzsxHpFKVK7q4+63SM1N95R1NbdWhscdCb+ZAJzVc
oyi3B43njTOQ5yOf+1CceWxG1bQVs5ZufpsMljq4Ui0/1lvh+wjChP4kqKOJ2qxq
4RgqsahDYVvTH9w7jXbyLeiNdd8XM2w9U/t7y0Ff/9yi0GE44Za4rF2LN9d11TPA
mRGunUHBcnWEvgJBQl9nJEiU0Zsnvgc/ubhPgXRR4Xq37Z0j4r7g1SgEEzwxA57d
emyPxgcYxn/eR44/KJ4EBs+lVDR3veyJm+kXQ99b21/+jh5Xos1AnX5iItreGCc=
-----END CERTIFICATE-----
-----BEGIN CERTIFICATE-----
MIICGzCCAaGgAwIBAgIQQdKd0XLq7qeAwSxs6S+HUjAKBggqhkjOPQQDAzBPMQsw
CQYDVQQGEwJVUzEpMCcGA1UEChMgSW50ZXJuZXQgU2VjdXJpdHkgUmVzZWFyY2gg
R3JvdXAxFTATBgNVBAMTDElTUkcgUm9vdCBYMjAeFw0yMDA5MDQwMDAwMDBaFw00
MDA5MTcxNjAwMDBaME8xCzAJBgNVBAYTAlVTMSkwJwYDVQQKEyBJbnRlcm5ldCBT
ZWN1cml0eSBSZXNlYXJjaCBHcm91cDEVMBMGA1UEAxMMSVNSRyBSb290IFgyMHYw
EAYHKoZIzj0CAQYFK4EEACIDYgAEzZvVn4CDCuwJSvMWSj5cz3es3mcFDR0HttwW
+1qLFNvicWDEukWVEYmO6gbf9yoWHKS5xcUy4APgHoIYOIvXRdgKam7mAHf7AlF9
ItgKbppbd9/w+kHsOdx1ymgHDB/qo0IwQDAOBgNVHQ8BAf8EBAMCAQYwDwYDVR0T
AQH/BAUwAwEB/zAdBgNVHQ4EFgQUfEKWrt5LSDv6kviejM9ti6lyN5UwCgYIKoZI
zj0EAwMDaAAwZQIwe3lORlCEwkSHRhtFcP9Ymd70/aTSVaYgLXTWNLxBo1BfASdW
tL4ndQavEi51mI38AjEAi/V3bNTIZargCyzuFJ0nN6T5U6VR5CmD1/iQMVtCnwr1
/q4AaOeMSQ+2b1tbFfLn
-----END CERTIFICATE-----
)PEM";

GxEPD2_BW<GxEPD2_154_D67, GxEPD2_154_D67::HEIGHT> display(GxEPD2_154_D67(kPinCs, kPinDc, kPinRst, kPinBusy));
U8G2_FOR_ADAFRUIT_GFX text;
SPIClass epdSpi(FSPI);

// Kept across deep sleep (lost when the power goes): the picture on the screen and how it came to be there.
RTC_DATA_ATTR uint8_t lastFrame[kFrameBytes];
RTC_DATA_ATTR bool haveFrame = false;
RTC_DATA_ATTR char lastEtag[72] = "";
RTC_DATA_ATTR uint32_t offlineMin = 0;  // minutes without a picture from the hive
RTC_DATA_ATTR uint32_t bannerAtMin = 0; // offlineMin when the banner was last drawn
RTC_DATA_ATTR bool messageShown = false; // a message replaced the picture: draw the picture again even on 304
RTC_DATA_ATTR uint32_t refreshS = 0;     // the hive's monitoring: read this often (X-Frame-Refresh), 0 = own interval

struct Settings {
    String ssid[2], pass[2], url, token;
    int intervalMin = kDefaultIntervalMin;
    int rotation = 0;
    bool insecure = false;

    bool ready() const { return ssid[0].length() > 0 && frameUrl(url.c_str()).length() > 0 && token.length() > 0; }

    void load() {
        Preferences p;
        p.begin("rmon", true);
        for (int i = 0; i < 2; i++) {
            ssid[i] = p.getString(i ? "ssid2" : "ssid1", "");
            pass[i] = p.getString(i ? "pass2" : "pass1", "");
        }
        url = p.getString("url", "");
        token = p.getString("token", "");
        intervalMin = clampInterval(p.getInt("interval", kDefaultIntervalMin));
        rotation = p.getInt("rot", 0) & 3;
        insecure = p.getBool("insecure", false);
        p.end();
    }

    void save() const {
        Preferences p;
        p.begin("rmon", false);
        p.putString("ssid1", ssid[0]);
        p.putString("pass1", pass[0]);
        p.putString("ssid2", ssid[1]);
        p.putString("pass2", pass[1]);
        p.putString("url", url);
        p.putString("token", token);
        p.putInt("interval", intervalMin);
        p.putInt("rot", rotation);
        p.putBool("insecure", insecure);
        p.end();
    }
} settings;

[[noreturn]] void readAndSleep();

// ------------------------------------------------------------------ console over USB

String consoleLine;

enum class Order { None, Go, Save };

/// Applies one console line; Go = save and read now, Save = save and sleep.
Order runCommand(String line) {
    line.trim();
    if (line.isEmpty()) return Order::None;
    if (line == "go" || line == "save") {
        if (!settings.ready()) {
            Serial.println("[monitor] error: need ssid, url (https://...) and token first");
            return Order::None;
        }
        settings.save();
        lastEtag[0] = '\0';
        Serial.println("[monitor] saved");
        return line == "go" ? Order::Go : Order::Save;
    }
    if (line == "show") {
        Serial.printf("[monitor] ssid=%s ssid2=%s url=%s token=%s interval=%d rot=%d insecure=%d\n", settings.ssid[0].c_str(),
                      settings.ssid[1].c_str(), settings.url.c_str(), settings.token.isEmpty() ? "-" : "set", settings.intervalMin,
                      settings.rotation, settings.insecure);
        return Order::None;
    }
    if (!line.startsWith("set ")) {
        Serial.println("[monitor] error: commands are set <key> <value>, show, go");
        return Order::None;
    }
    const int space = line.indexOf(' ', 4);
    const String key = space < 0 ? line.substring(4) : line.substring(4, space);
    String value = space < 0 ? "" : line.substring(space + 1);  // the value may hold spaces (Wi-Fi names)
    if (key == "ssid") settings.ssid[0] = value;
    else if (key == "pass") settings.pass[0] = value;
    else if (key == "ssid2") settings.ssid[1] = value;
    else if (key == "pass2") settings.pass[1] = value;
    else if (key == "url") settings.url = value;
    else if (key == "token") settings.token = value;
    else if (key == "interval") settings.intervalMin = clampInterval(value.toInt());
    else if (key == "rot") settings.rotation = value.toInt() & 3;
    else if (key == "insecure") settings.insecure = value == "1";
    else {
        Serial.printf("[monitor] error: unknown key %s\n", key.c_str());
        return Order::None;
    }
    Serial.println("[monitor] ok");
    return Order::None;
}

/// Reads what came over USB; Go or Save when that order arrived. Any input says somebody is at the console.
Order pollConsole(bool& activity) {
    while (Serial.available()) {
        const char c = static_cast<char>(Serial.read());
        activity = true;
        if (c == '\n') {
            const String line = consoleLine;
            consoleLine = "";
            const Order order = runCommand(line);
            if (order != Order::None) return order;
        } else if (c != '\r' && consoleLine.length() < 300) {
            consoleLine += c;
        }
    }
    return Order::None;
}

// ------------------------------------------------------------------ power

void powerOn() {
    gpio_hold_dis(kPinPowerHold);
    gpio_hold_dis(kPinEpdPower);
    gpio_hold_dis(kPinAudioOff);
    pinMode(kPinPowerHold, OUTPUT);
    digitalWrite(kPinPowerHold, HIGH);
    pinMode(kPinAudioOff, OUTPUT);
    digitalWrite(kPinAudioOff, HIGH);
    pinMode(kPinEpdPower, OUTPUT);
    digitalWrite(kPinEpdPower, LOW);
}

int batteryMv() {
    return static_cast<int>(analogReadMilliVolts(kPinBattery)) * 2;
}

/// Deep sleep until the timer (minutes, 0 = none) or a button. The e-paper keeps its picture without power.
[[noreturn]] void sleepFor(uint32_t minutes) {
    display.hibernate();
    WiFi.disconnect(true);
    WiFi.mode(WIFI_OFF);
    digitalWrite(kPinEpdPower, HIGH);
    gpio_hold_en(kPinEpdPower);
    gpio_hold_en(kPinPowerHold);
    gpio_hold_en(kPinAudioOff);
    gpio_deep_sleep_hold_en();
    rtc_gpio_pullup_en(kPinBoot);
    rtc_gpio_pullup_en(kPinPwr);
    esp_sleep_enable_ext1_wakeup((1ULL << kPinBoot) | (1ULL << kPinPwr), ESP_EXT1_WAKEUP_ANY_LOW);
    if (minutes > 0) esp_sleep_enable_timer_wakeup(static_cast<uint64_t>(minutes) * 60ULL * 1000000ULL);
    Serial.flush();
    esp_deep_sleep_start();
}

// ------------------------------------------------------------------ screen

void initScreen() {
    epdSpi.begin(kPinSck, -1, kPinMosi, kPinCs);
    display.epd2.selectSPI(epdSpi, SPISettings(4000000, MSBFIRST, SPI_MODE0));
    // The panel lost power while asleep: every picture is a full refresh (a partial one needs the old image in its RAM).
    display.init(0, true, 2, false);
    display.setRotation(settings.rotation);
    display.setTextColor(GxEPD_BLACK);
    text.begin(display);
    text.setFontMode(1);
    text.setForegroundColor(GxEPD_BLACK);
    text.setBackgroundColor(GxEPD_WHITE);
}

void drawBanner(const String& line) {
    display.fillRect(0, kHeight - 22, kWidth, 22, GxEPD_BLACK);
    text.setFont(u8g2_font_9x15_t_cyrillic);
    text.setForegroundColor(GxEPD_WHITE);
    text.setBackgroundColor(GxEPD_BLACK);
    text.setCursor(4, kHeight - 6);
    text.print(line);
    text.setForegroundColor(GxEPD_BLACK);
    text.setBackgroundColor(GxEPD_WHITE);
}

void showFrame(const String& banner) {
    display.setFullWindow();
    display.firstPage();
    do {
        display.fillScreen(GxEPD_WHITE);
        display.drawBitmap(0, 0, lastFrame, kWidth, kHeight, GxEPD_BLACK);
        if (banner.length()) drawBanner(banner);
    } while (display.nextPage());
    messageShown = false;
}

/// A full-screen message: a title and up to six short lines (about 21 characters fit a line).
void showMessage(const char* title, std::initializer_list<String> lines) {
    display.setFullWindow();
    display.firstPage();
    do {
        display.fillScreen(GxEPD_WHITE);
        text.setFont(u8g2_font_10x20_t_cyrillic);
        text.setCursor(6, 26);
        text.print(title);
        display.drawFastHLine(6, 34, kWidth - 12, GxEPD_BLACK);
        text.setFont(u8g2_font_6x13_t_cyrillic);
        int y = 56;
        for (const String& line : lines) {
            text.setCursor(6, y);
            text.print(line);
            y += 18;
        }
    } while (display.nextPage());
    messageShown = true;
}

String batteryNote() {
    const int mv = batteryMv();
    return mv > 1000 && mv < kLowBatteryMv ? String("батарея ") + String(mv / 1000.0f, 2) + " В" : String();
}

// ------------------------------------------------------------------ settings point (captive portal)

WebServer server(80);
DNSServer dns;
String apName;
String networksHtml;
uint32_t portalStartedMs = 0;
uint32_t restartAtMs = 0;

String escapeHtml(const String& s) {
    String out;
    out.reserve(s.length() + 8);
    for (size_t i = 0; i < s.length(); i++) {
        const char c = s[i];
        if (c == '&') out += "&amp;";
        else if (c == '<') out += "&lt;";
        else if (c == '>') out += "&gt;";
        else if (c == '"') out += "&quot;";
        else out += c;
    }
    return out;
}

String pageHtml(const char* note) {
    String h;
    h.reserve(4096);
    h += F("<!doctype html><html lang=ru><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'>"
           "<title>Монитор Village Swarm</title><style>body{font-family:sans-serif;max-width:28em;margin:1em auto;padding:0 1em}"
           "label{display:block;margin-top:.8em;font-size:.9em}input,select{width:100%;padding:.4em;box-sizing:border-box}"
           "small{color:#666}button{margin-top:1.2em;padding:.6em 1.2em}</style><h2>Монитор Village Swarm</h2>");
    if (note && *note) h += String("<p><b>") + note + "</b></p>";
    h += F("<form method=post action=/save><datalist id=nets>");
    h += networksHtml;
    h += F("</datalist>");
    for (int i = 0; i < 2; i++) {
        h += String("<label>Wi-Fi ") + (i + 1) + (i ? " (запасная, можно пусто)" : "") + "<input name=ssid" + (i + 1) +
             " list=nets value=\"" + escapeHtml(settings.ssid[i]) + "\"></label>";
        h += String("<label>Пароль Wi-Fi ") + (i + 1) + "<input name=pass" + (i + 1) + " value=\"" + escapeHtml(settings.pass[i]) + "\"></label>";
    }
    h += "<label>Адрес Улья<input name=url placeholder='https://hive.имя.keenetic.pro' value=\"" + escapeHtml(settings.url) +
         "\"><small>Адрес, по которому Улей открывается из интернета.</small></label>";
    h += "<label>Токен экрана<input name=token autocapitalize=off placeholder='xxxx-xxxx-xxxx-xxxx-xxxx' value=\"" + escapeHtml(settings.token) +
         "\"><small>Выдаётся на странице монитора в Улье; там же QR-код, который заполняет эту форму.</small></label>";
    h += F("<label>Как часто обновлять<select name=interval>");
    for (int m : {5, 10, 15, 30, 60, 120}) {
        h += String("<option value=") + m + (m == settings.intervalMin ? " selected>" : ">") + m + " мин</option>";
    }
    h += F("</select><small>Чаще — свежее, реже — дольше живёт батарея.</small></label><label>Поворот экрана<select name=rot>");
    for (int r = 0; r < 4; r++) {
        h += String("<option value=") + r + (r == settings.rotation ? " selected>" : ">") + (r * 90) + "°</option>";
    }
    h += String("</select></label><label><input type=checkbox name=insecure style='width:auto'") + (settings.insecure ? " checked" : "") +
         "> не проверять сертификат <small>(только для адреса внутри дома)</small></label>";
    h += F("<button>Сохранить</button></form></html>");
    return h;
}

void handleSave() {
    for (int i = 0; i < 2; i++) {
        const String n = String(i + 1);
        settings.ssid[i] = server.arg("ssid" + n);
        settings.ssid[i].trim();
        settings.pass[i] = server.arg("pass" + n);
    }
    settings.url = server.arg("url");
    settings.url.trim();
    settings.token = server.arg("token");
    settings.token.trim();
    settings.intervalMin = clampInterval(server.arg("interval").toInt());
    settings.rotation = server.arg("rot").toInt() & 3;
    settings.insecure = server.hasArg("insecure");
    if (!settings.ready()) {
        server.send(200, "text/html; charset=utf-8",
                    pageHtml("Нужны сеть Wi-Fi, адрес Улья (https://...) и токен."));
        return;
    }
    settings.save();
    lastEtag[0] = '\0';  // another hive or token: read the picture anew
    server.send(200, "text/html; charset=utf-8",
                "<!doctype html><meta charset=utf-8><p>Сохранено. Монитор перезапускается и читает картинку из Улья.</p>");
    restartAtMs = millis() + 1500;
}

void startPortal() {
    const uint64_t mac = ESP.getEfuseMac();
    char suffix[5];
    snprintf(suffix, sizeof suffix, "%04x", static_cast<unsigned>((mac >> 32) & 0xffff));
    apName = String("VillageSwarm-") + suffix;

    WiFi.mode(WIFI_AP_STA);
    const int found = WiFi.scanNetworks();
    for (int i = 0; i < found && i < 20; i++) networksHtml += "<option value=\"" + escapeHtml(WiFi.SSID(i)) + "\">";
    WiFi.scanDelete();
    WiFi.softAP(apName.c_str());
    dns.start(53, "*", WiFi.softAPIP());
    server.on("/", HTTP_GET, [] {
        // The hive's page shows a QR code with these in the address: the phone's camera opens the form filled in.
        const char* note = nullptr;
        if (server.hasArg("url") || server.hasArg("token") || server.hasArg("ssid")) {
            if (server.hasArg("ssid")) settings.ssid[0] = server.arg("ssid");
            if (server.hasArg("pass")) settings.pass[0] = server.arg("pass");
            if (server.hasArg("url")) settings.url = server.arg("url");
            if (server.hasArg("token")) settings.token = server.arg("token");
            if (server.hasArg("interval")) settings.intervalMin = clampInterval(server.arg("interval").toInt());
            note = "Поля заполнены из QR-кода. Проверьте Wi-Fi и нажмите «Сохранить».";
        }
        server.send(200, "text/html; charset=utf-8", pageHtml(note));
    });
    server.on("/save", HTTP_POST, handleSave);
    server.onNotFound([] {  // phones probe some page to find a captive portal: send them to the form
        server.sendHeader("Location", "http://192.168.4.1/", true);
        server.send(302, "text/plain", "");
    });
    server.begin();
    portalStartedMs = millis();
    showMessage("Настройка", {"Подключитесь к Wi-Fi", apName, "и откройте", "http://192.168.4.1", "", "10 минут на настройку"});
}

bool portalMode = false;

// ------------------------------------------------------------------ reading the picture

bool joinWifi() {
    WiFi.persistent(false);
    WiFi.mode(WIFI_STA);
    for (int i = 0; i < 2; i++) {
        if (settings.ssid[i].isEmpty()) continue;
        WiFi.begin(settings.ssid[i].c_str(), settings.pass[i].c_str());
        const uint32_t started = millis();
        while (WiFi.status() != WL_CONNECTED && millis() - started < kWifiTimeoutMs) delay(50);
        if (WiFi.status() == WL_CONNECTED) return true;
        WiFi.disconnect(true);
    }
    return false;
}

/// Collects the body of a 200 into lastFrame-sized memory, chunked or not (HTTPClient::writeToStream).
class FrameSink : public Stream {
public:
    explicit FrameSink(uint8_t* buffer) : buffer_(buffer) {}
    size_t write(uint8_t b) override { return write(&b, 1); }
    size_t write(const uint8_t* data, size_t size) override {
        if (size_ + size > kFrameBytes) {
            overflow_ = true;
            return 0;
        }
        memcpy(buffer_ + size_, data, size);
        size_ += size;
        return size;
    }
    int available() override { return 0; }
    int read() override { return -1; }
    int peek() override { return -1; }
    size_t size() const { return overflow_ ? kFrameBytes + 1 : size_; }

private:
    uint8_t* buffer_;
    size_t size_ = 0;
    bool overflow_ = false;
};

uint8_t incoming[kFrameBytes];

struct Reply {
    int code = -1;
    Answer answer = Answer::Retry;
    String etag;
};

Reply fetchFrame() {
    Reply reply;
    const std::string url = frameUrl(settings.url.c_str());
    WiFiClientSecure secure;
    WiFiClient plain;
    HTTPClient http;
    http.setTimeout(15000);
    http.setConnectTimeout(10000);
    http.setReuse(false);
    bool begun;
    if (isHttps(url)) {
        if (settings.insecure) secure.setInsecure();
        else secure.setCACert(kRootCa);
        begun = http.begin(secure, url.c_str());
    } else {
        begun = http.begin(plain, url.c_str());
    }
    if (!begun) return reply;
    http.addHeader("Authorization", "Bearer " + settings.token);
    if (lastEtag[0] && haveFrame) http.addHeader("If-None-Match", lastEtag);
    const char* keys[] = {"ETag", "X-Frame-Width", "X-Frame-Height", "X-Frame-Refresh"};
    http.collectHeaders(keys, 4);
    reply.code = http.GET();
    if (reply.code == 200 || reply.code == 304)
        refreshS = http.hasHeader("X-Frame-Refresh") ? static_cast<uint32_t>(http.header("X-Frame-Refresh").toInt()) : 0;
    size_t bytes = 0;
    if (reply.code == 200) {
        FrameSink sink(incoming);
        http.writeToStream(&sink);
        bytes = sink.size();
        reply.etag = http.header("ETag");
    }
    reply.answer = classify(reply.code, bytes, http.header("X-Frame-Width").toInt(), http.header("X-Frame-Height").toInt(), kWidth, kHeight);
    http.end();
    return reply;
}

[[noreturn]] void readAndSleep() {
    const String battery = batteryNote();
    if (!joinWifi()) {
        Serial.println("[monitor] wifi failed");
        offlineMin += settings.intervalMin;
        if (!haveFrame) {
            showMessage("Нет Wi-Fi", {"Сеть " + settings.ssid[0], "не отвечает.", "", "Повтор через " + String(settings.intervalMin) + " мин.", "BOOT 2 с — настройка"});
        } else if (bannerDue(offlineMin, bannerAtMin)) {
            bannerAtMin = offlineMin;
            showFrame("нет Wi-Fi ~" + String(offlineMin) + " мин");
        }
        sleepFor(settings.intervalMin);
    }

    const Reply reply = fetchFrame();
    Serial.printf("[monitor] frame %d\n", reply.code);  // the hive's page waits for this line after flashing
    switch (reply.answer) {
        case Answer::Draw:
            memcpy(lastFrame, incoming, kFrameBytes);
            haveFrame = true;
            snprintf(lastEtag, sizeof lastEtag, "%s", reply.etag.c_str());
            offlineMin = bannerAtMin = 0;
            showFrame(battery);
            break;
        case Answer::Keep:
            if (offlineMin > 0 && bannerAtMin > 0) showFrame(battery);  // take the banner off
            else if (messageShown) showFrame(battery);
            offlineMin = bannerAtMin = 0;
            break;
        case Answer::BadToken:
            showMessage("Токен не принят", {"Выдайте новый на", "странице монитора", "в Улье и введите его:", "BOOT 2 с — настройка"});
            sleepFor(kBadTokenSleepMin);
        case Answer::NotDrawn:
            showMessage("Нет картинки", {"Улей не рисует этот", "экран: на странице", "монитора выберите", "плату Waveshare 1,54\"."});
            sleepFor(kBadTokenSleepMin);
        case Answer::Retry:
            offlineMin += settings.intervalMin;
            if (!haveFrame) {
                showMessage("Нет связи с Ульем", {"Ответ: " + String(reply.code), "Повтор через " + String(settings.intervalMin) + " мин.", "", "BOOT 2 с — настройка"});
            } else if (bannerDue(offlineMin, bannerAtMin)) {
                bannerAtMin = offlineMin;
                showFrame("нет связи ~" + String(offlineMin) + " мин");
            }
            break;
    }
    sleepFor(nextReadSeconds(settings.intervalMin, refreshS) / 60);  // sooner while the hive monitors the panel
}

/// Woken by BOOT: held for 2 s = the settings point, a short press = read now.
bool bootHeld() {
    pinMode(kPinBoot, INPUT_PULLUP);
    const uint32_t started = millis();
    while (millis() - started < 2000) {
        if (digitalRead(kPinBoot) == HIGH) return false;
        delay(20);
    }
    return true;
}

[[noreturn]] void saveAndSleep() {
    showMessage("Настроено", {"Wi-Fi: " + settings.ssid[0], "Первое чтение через", String(settings.intervalMin) + " мин у этой сети", "", "BOOT 2 с — настройка"});
    sleepFor(settings.intervalMin);
}

/// A moment for the console after a power-on or a reset (not after a timer or a button): the hive's page flashes the
/// board and sends the settings over USB. Says "ready" twice a second until somebody types, then waits for "go".
Order consoleWindow() {
    constexpr uint32_t kWaitMs = 12000, kMaxMs = 120000;
    const uint32_t started = millis();
    uint32_t until = kWaitMs, lastBanner = 0;
    for (;;) {
        bool activity = false;
        const Order order = pollConsole(activity);
        if (order != Order::None) return order;
        const uint32_t now = millis() - started;
        if (activity) until = now + kWaitMs < kMaxMs ? now + kWaitMs : kMaxMs;
        if (now >= until) return Order::None;
        if (now - lastBanner >= 500 || lastBanner == 0) {
            lastBanner = now;
            Serial.println("[monitor] ready");
        }
        delay(20);
    }
}

}  // namespace

void setup() {
    powerOn();
    Serial.begin(115200);
    settings.load();

    const esp_sleep_wakeup_cause_t cause = esp_sleep_get_wakeup_cause();
    const Order order = cause == ESP_SLEEP_WAKEUP_UNDEFINED ? consoleWindow() : Order::None;
    initScreen();
    if (order == Order::Save) saveAndSleep();

    const uint64_t buttons = cause == ESP_SLEEP_WAKEUP_EXT1 ? esp_sleep_get_ext1_wakeup_status() : 0;
    if (buttons & (1ULL << kPinPwr)) {
        showMessage("Выключено", {"Включение — кнопкой PWR"});
        display.hibernate();
        digitalWrite(kPinPowerHold, LOW);  // on battery the board switches off here
        sleepFor(0);                        // on USB it keeps power: sleep until a button
    }
    const bool wantPortal = !settings.ready() || ((buttons & (1ULL << kPinBoot)) && bootHeld());
    if (wantPortal) {
        portalMode = true;
        startPortal();
        return;
    }
    readAndSleep();
}

void loop() {
    if (!portalMode) return;
    dns.processNextRequest();
    server.handleClient();
    bool activity = false;
    if (const Order order = pollConsole(activity); order != Order::None) {  // settings came over USB: leave the Wi-Fi point
        server.stop();
        dns.stop();
        WiFi.softAPdisconnect(true);
        portalMode = false;
        if (order == Order::Save) saveAndSleep();
        readAndSleep();
    }
    if (restartAtMs && static_cast<int32_t>(millis() - restartAtMs) >= 0) ESP.restart();
    if (millis() - portalStartedMs > kPortalTimeoutMs) {
        if (haveFrame) showFrame(String());
        sleepFor(settings.ready() ? settings.intervalMin : 0);
    }
    delay(2);
}
