// Role ble: a bridge between a Bluetooth LE kitchen thermometer (Inkbird IHT-2PB) and the hive. The thermometer is
// connected over GATT, switched to send notifications, and its probe temperatures are published as the metrics
// temperature, temperature_2, temperature_3 like any other hornet.
//
// Config "ble": mac (the thermometer's address, AA:BB:CC:DD:EE:FF), dump (true: the raw notification frames go to the
// hive's log, to check the decoding against the Inkbird app). Without a mac the bridge scans for a minute and lists
// the Bluetooth devices it hears in the log, so the address can be found without a phone.
//
// Connecting takes seconds and blocks, so it runs in its own task; the loop only publishes what the notifications left.

#include <HornetCore.h>
#include <InkbirdIht2pb.h>
#include <NimBLEDevice.h>

#include <string>

namespace {

using namespace hornet;

constexpr const char* kService = "ffe0";
constexpr const char* kCommandChar = "ffe9";
constexpr const char* kNotifyChar = "ffe4";
constexpr uint32_t kRetryMinMs = 5000;
constexpr uint32_t kRetryMaxMs = 60000;
constexpr uint32_t kFreshMs = 120000;      // a probe that was not heard from for this long is not published
constexpr uint32_t kScanEveryMs = 60000;
constexpr uint32_t kDumpEveryMs = 3000;

// What the thermometer wants before it sends temperatures (from the open ESPHome project for this device).
constexpr uint8_t kStart1[] = {0x55, 0xAA, 0x19, 0x01, 0x00, 0x19};  // written to FFE9
constexpr uint8_t kStart2[] = {0x55, 0xAA, 0x1A, 0x01, 0x00, 0x1A};  // written to FFE4 (it does not announce writing, the device still needs it)

class Ble : public Role {
public:
    const char* type() const override { return "ble"; }
    const char* hw() const override { return HORNET_BOARD; }
    std::vector<const char*> caps() const override { return {"ble.inkbird-iht-2pb"}; }
    std::vector<const char*> metrics() const override { return {"temperature", "temperature_2", "temperature_3"}; }

    void setup(Core&) override {
        NimBLEDevice::init("village-swarm");
        self_ = this;
    }

    void applyConfig(Core&, JsonObjectConst config) override {
        teleIntervalMs_ = (config["tele_interval_s"] | 60U) * 1000UL;
        const char* mac = config["ble"]["mac"] | "";
        dump_ = config["ble"]["dump"] | false;
        if (mac_ != mac) {
            mac_ = mac;
            retryMs_ = kRetryMinMs;
            nextTryMs_ = 0;
            if (client_ != nullptr && client_->isConnected()) client_->disconnect();
        }
    }

    void loop(Core& core) override {
        const uint32_t now = millis();
        if (mac_.empty()) {
            scanStep(core, now);
            flushScan(core);
            return;
        }
        if (!busy_ && !connected_ && now >= nextTryMs_) startConnect();
        publish(core, now);
        logFrame(core, now);
        if (failed_) {
            failed_ = false;
            core.log(LogLevel::Warn, String("thermometer ") + mac_.c_str() + ": " + lastError_);
        }
    }

private:
    // --- connection (own task) ---

    void startConnect() {
        busy_ = true;
        xTaskCreate(&Ble::connectTask, "ble", 6144, this, 1, nullptr);
    }

    static void connectTask(void* arg) {
        auto* self = static_cast<Ble*>(arg);
        self->connectOnce();
        self->busy_ = false;
        vTaskDelete(nullptr);
    }

    void connectOnce() {
        if (client_ == nullptr) client_ = NimBLEDevice::createClient();
        // The address type is not known: public first, random on the next try.
        const NimBLEAddress address(mac_, usePublic_ ? BLE_ADDR_PUBLIC : BLE_ADDR_RANDOM);
        usePublic_ = !usePublic_;
        if (!client_->connect(address)) return fail("no connection");

        NimBLERemoteService* service = client_->getService(kService);
        if (service == nullptr) {
            client_->disconnect();
            return fail("service FFE0 not found");
        }
        NimBLERemoteCharacteristic* command = service->getCharacteristic(kCommandChar);
        NimBLERemoteCharacteristic* notify = service->getCharacteristic(kNotifyChar);
        if (command == nullptr || notify == nullptr) {
            client_->disconnect();
            return fail("characteristics FFE9/FFE4 not found");
        }
        if (!notify->subscribe(true, &Ble::onNotify)) {
            client_->disconnect();
            return fail("cannot subscribe to notifications");
        }
        command->writeValue(kStart1, sizeof kStart1, false);
        notify->writeValue(kStart2, sizeof kStart2, false);  // refused by the stack on some versions; harmless
        connected_ = true;
        retryMs_ = kRetryMinMs;
        Serial.printf("[ble] connected to %s\n", mac_.c_str());
        // Hold the task until the link is gone, then schedule the next try.
        while (client_->isConnected()) vTaskDelay(pdMS_TO_TICKS(1000));
        connected_ = false;
        retryMs_ = kRetryMinMs;
        nextTryMs_ = millis() + kRetryMinMs;
        Serial.println("[ble] disconnected");
    }

    void fail(const char* why) {
        lastError_ = why;
        failed_ = true;
        retryMs_ = retryMs_ * 2 > kRetryMaxMs ? kRetryMaxMs : retryMs_ * 2;
        nextTryMs_ = millis() + retryMs_;
    }

    // --- notifications (BLE host task) ---

    static void onNotify(NimBLERemoteCharacteristic*, uint8_t* data, size_t length, bool) {
        if (self_ != nullptr) self_->handle(data, length);
    }

    void handle(const uint8_t* data, size_t length) {
        portENTER_CRITICAL(&mux_);
        frameLen_ = length > sizeof frame_ ? sizeof frame_ : length;
        memcpy(frame_, data, frameLen_);
        frameNew_ = true;
        portEXIT_CRITICAL(&mux_);

        inkbird::Reading r;
        if (!inkbird::parse(data, length, r)) return;
        portENTER_CRITICAL(&mux_);
        celsius_[r.probe] = r.celsius;
        heardMs_[r.probe] = millis();
        heard_[r.probe] = true;
        portEXIT_CRITICAL(&mux_);
    }

    // --- publishing ---

    void publish(Core& core, uint32_t now) {
        if (!core.online() || (published_ && now - lastPublishMs_ < teleIntervalMs_)) return;
        published_ = true;
        lastPublishMs_ = now;
        for (int p = 0; p < inkbird::kProbes; ++p) {
            float value = 0;
            bool fresh = false;
            portENTER_CRITICAL(&mux_);
            fresh = heard_[p] && now - heardMs_[p] < kFreshMs;
            value = celsius_[p];
            portEXIT_CRITICAL(&mux_);
            if (fresh) core.publishTele(inkbird::metricName(p), value, "C");
        }
    }

    // The raw frame, now and then, when "dump" is on.
    void logFrame(Core& core, uint32_t now) {
        if (!dump_ || now - lastDumpMs_ < kDumpEveryMs) return;
        uint8_t copy[32];
        size_t n = 0;
        bool fresh = false;
        portENTER_CRITICAL(&mux_);
        fresh = frameNew_;
        frameNew_ = false;
        n = frameLen_;
        memcpy(copy, frame_, n);
        portEXIT_CRITICAL(&mux_);
        if (!fresh) return;
        lastDumpMs_ = now;
        String hex;
        for (size_t i = 0; i < n; ++i) {
            if (copy[i] < 16) hex += '0';
            hex += String(copy[i], HEX);
            hex += ' ';
        }
        core.log(LogLevel::Info, String("ble frame: ") + hex);
    }

    // --- finding the thermometer (no mac yet) ---

    void scanStep(Core& core, uint32_t now) {
        if (scanning_ || (scannedOnce_ && now - lastScanMs_ < kScanEveryMs)) return;
        scanning_ = true;
        scannedOnce_ = true;
        lastScanMs_ = now;
        xTaskCreate(&Ble::scanTask, "blescan", 6144, this, 1, nullptr);
        (void)core;
    }

    static void scanTask(void* arg) {
        auto* self = static_cast<Ble*>(arg);
        NimBLEScan* scan = NimBLEDevice::getScan();
        scan->setActiveScan(true);
        NimBLEScanResults results = scan->getResults(8000, false);
        String list;
        int shown = 0;
        for (int i = 0; i < results.getCount() && shown < 15; ++i) {
            const NimBLEAdvertisedDevice* d = results.getDevice(i);
            if (d->getName().empty()) continue;
            list += String(d->getName().c_str()) + " " + d->getAddress().toString().c_str() + " " + String(d->getRSSI()) + "dBm; ";
            ++shown;
        }
        self->scanResult_ = list.length() ? list : String("no named Bluetooth devices heard");
        self->scanReady_ = true;
        self->scanning_ = false;
        vTaskDelete(nullptr);
    }

public:
    // The scan result is logged from the loop task (the logger is not meant for other tasks).
    void flushScan(Core& core) {
        if (!scanReady_) return;
        scanReady_ = false;
        core.log(LogLevel::Info, String("Bluetooth devices: ") + scanResult_ + " (set ble.mac in the config)");
    }

private:
    static Ble* self_;

    std::string mac_;
    uint32_t teleIntervalMs_ = 60000;
    bool dump_ = false;

    NimBLEClient* client_ = nullptr;
    volatile bool busy_ = false;
    volatile bool connected_ = false;
    volatile bool failed_ = false;
    bool usePublic_ = true;
    uint32_t retryMs_ = kRetryMinMs;
    volatile uint32_t nextTryMs_ = 0;
    String lastError_;

    portMUX_TYPE mux_ = portMUX_INITIALIZER_UNLOCKED;
    float celsius_[inkbird::kProbes] = {};
    uint32_t heardMs_[inkbird::kProbes] = {};
    bool heard_[inkbird::kProbes] = {};
    uint8_t frame_[32] = {};
    size_t frameLen_ = 0;
    bool frameNew_ = false;
    bool published_ = false;
    uint32_t lastPublishMs_ = 0;
    uint32_t lastDumpMs_ = 0;

    volatile bool scanning_ = false;
    volatile bool scanReady_ = false;
    bool scannedOnce_ = false;
    uint32_t lastScanMs_ = 0;
    String scanResult_;
};

Ble* Ble::self_ = nullptr;

}  // namespace

hornet::Role& hornetRole() {
    static Ble role;
    return role;
}
