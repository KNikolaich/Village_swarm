# Hornet firmware

PlatformIO, Arduino-ESP32 3.x. `lib/hornet-core` speaks the MQTT contract (`contracts/`);
each role in `src/roles/` is one env in `platformio.ini`.

```sh
pio run -e guard-cam              # build
pio run -e guard-cam -t upload    # flash (ESP32-CAM-MB or USB-UART, see below)
pio device monitor                # serial console, 115200
pio test -e native                # logic tests on the PC (needs gcc)
```

In VS Code the PlatformIO toolbar does the same (✓ build, → upload, plug icon for the monitor).

## Builds: role × board

| Env | Board | Chip |
|---|---|---|
| `guard-cam` | ESP32-CAM AI-Thinker | ESP32 |
| `meteo-c3`, `relay-c3` | ESP32-C3 mini (SuperMini) | ESP32-C3 |
| `meteo-c3oled` | ESP32-C3 with the 0.42" OLED (72×40) | ESP32-C3 |
| `meteo-d1mini`, `relay-d1mini` | Wemos D1 mini | ESP8266 |
| `meteo-nodemcu`, `relay-nodemcu` | NodeMCU v3 | ESP8266 |
| `meteo-esp01`, `relay-esp01` | ESP-01 / ESP-01S (1 MB), incl. "ESP-01 Relay v1.0" and DS18B20 modules | ESP8266 |
| `meteo-esp01dht` | "ESP-01S DHT11 v1.0" module: DHT11 on GPIO2, powered 3.7–12 V through its own regulator | ESP8266 |

Every build lands in `firmware/dist/<env>/` with a `build.json` (chip, flash offsets); the web flasher in
"Добавить шершня" offers the boards that exist there for the chosen role.

Default pins (override per hornet with the config `hw` section: `onewire_pin`, `sda_pin`, `scl_pin`,
`relay_pin`, `relay_active_low`, `button_pin`):

| Board | 1-Wire (DS18B20) | I2C SDA/SCL (BME280) | Relay | Button |
|---|---|---|---|---|
| ESP32-C3 mini | GPIO3 | GPIO8 / GPIO9 | GPIO10 | — |
| ESP32-C3 0.42" OLED | GPIO3 | GPIO5 / GPIO6 (shared with the OLED) | — | — |
| D1 mini, NodeMCU | D5 (GPIO14) | D2 / D1 (GPIO4/5) | D6 (GPIO12) | D3 (GPIO0, FLASH on NodeMCU) |
| ESP-01 | GPIO2 | GPIO0 / GPIO2 | GPIO0, active low | — |

DS18B20 needs a 4.7 kΩ pull-up from data to 3.3 V (the ready-made modules have it).

- **meteo**: detects a BME280 (0x76/0x77) and any number of DS18B20 at start. Publishes `temperature`,
  `humidity`, `pressure` (hPa) and further thermometers as `temperature_2`, `temperature_3`...
- **DHT11** (`meteo-esp01dht`): temperature and humidity once per `tele_interval_s`; the DHT shares GPIO2 with
  1-Wire and I2C on the ESP-01, so that build reads only the DHT. Accuracy ±2 °C / ±5 %, and the ESP next to it
  warms it by a degree or two.
- **OTA**: every build can be updated over the air with the `ota` command (see [docs/ota.md](../docs/ota.md)); the version is
  `0.2.0+<commit>` (scripts/version.py) and goes into `build.json`. ESP8266 has no rollback.
- **Battery mode** (any meteo build): config `sleep_s` (set in the swarm screen) makes the hornet wake, send one
  reading, disconnect cleanly and deep-sleep. ESP8266 boards need GPIO16 wired to RST (see
  [docs/hornet-esp01-battery.md](../docs/hornet-esp01-battery.md)); type `awake` on the console to keep it up.
- **Battery voltage** (any meteo or relay build): wire a divider from the 12 V battery to an ADC pin and set
  `hw.vbat_pin` and `hw.vbat_ratio` = (R1+R2)/R2 (both in the swarm screen, pins panel). Example: 47 kΩ + 10 kΩ
  gives 5.7, so 15 V at most reaches 2.6 V on the pin (keep it under ~3 V on ESP32-C3; on ESP32-C3 only GPIO0–4
  have the ADC). ESP8266 has one ADC, A0: set `vbat_pin` to 17 (any number turns the reading on, A0 is read).
  The NodeMCU / D1 mini A0 divider is in parallel with R2, so calibrate: new ratio = ratio × multimeter / reading.
  Published as `battery_voltage` (V) every `tele_interval_s`.
- **Screen** (`meteo-c3oled`): two values in large digits, `display.lines` in the config, e.g.
  `["temperature", "battery_voltage"]`; by default the first two of temperature, humidity, temperature_2,
  battery_voltage. A dot in the corner means the hornet is online.
- **relay**: `cmd/relay {channel, set: on|off, for_s}`, channel `relay` unless config `relay.channel` says
  otherwise (e.g. `light`). Off at every boot; switches off by itself after `for_s` or `relay.max_on_s`,
  whichever is shorter. The optional button toggles it locally. Heaters with a temperature failsafe will be a
  separate role (spec 5.6).

ESP8266 notes: 80 KB of RAM, so roles stay small; the watchdog is the chip's fixed one (~8 s). Settings live
in LittleFS through the same Preferences API as NVS on ESP32.

## guard-cam on ESP32-CAM AI-Thinker

| What | Pin |
|---|---|
| PIR OUT (HC-SR501 / AM312) | GPIO13 |
| PIR VCC / GND | 5V / GND (AM312: 3V3) |
| microSD | on-board slot, used in 1-bit mode (frees GPIO4/12/13) |
| Light output (`cmd/relay`) | GPIO4 = on-board white flash LED; a relay module for a floodlight can go here too. Set `hw.relay_pin` / `hw.relay_active_low` to move it (GPIO12, GPIO33, GPIO1/3); camera, SD, PSRAM and PIR pins are refused |

Power the board from a stable 5 V, 1 A or more: the camera and Wi-Fi together cause brownout resets
on weak USB ports.

Flashing: with the ESP32-CAM-MB base just plug USB. With a USB-UART adapter: U0R↔TX, U0T↔RX,
5V, GND, and GPIO0 to GND while powering on (remove it afterwards and press RST).

## First start: settings over the serial console

Until provisioning (build step 10) the board is configured from the serial monitor; values are kept in
NVS, nothing is compiled in.

```
set ssid MyWiFi
set pass secret
set mqtt 192.168.1.10          # PC or hive running Mosquitto (port 1883, or host:port)
set id guard-gate1
set upload http://192.168.1.10:5080
show
reboot
```

The PC's firewall must allow incoming 1883 (Mosquitto) and 5080 (api) for this; the api must listen on
all interfaces: `dotnet run --project backend/src/Hive.Api --urls http://0.0.0.0:5080`.

Other console commands: `factory` (wipe settings), `set mqttuser|mqttpass|token|ntp <value>`.

## What the guard-cam does

- PIR rising edge, at most once per `cooldown_s` (20 s): publishes `event/motion` with photo ids, then
  takes `burst` frames (3, 700 ms apart) and uploads them to `/api/ingest/photo` in a background task.
- Upload fails: the photo goes to `/outbox` on the SD card and is retried every 30 s.
- Broker gone: events wait in a RAM buffer (50) and are sent with their original ids on reconnect.
- Commands: `arm {armed}`, `snapshot {count}`, `relay {channel, set, for_s}`, `reboot`, `config_reload`; expired (`ttl_s`) and repeated
  (`cid`) commands are handled per the contract.
- Light output: off at every boot, works like the relay role (channel `relay` unless `relay.channel` says
  otherwise, `for_s` and `relay.max_on_s` limits, retained `state`). The flash LED gets hot if left on for long:
  set `relay.max_on_s` when nothing else switches it off.
- Retained `config` (guard, camera, upload, hw, relay sections) is applied, saved to NVS and confirmed with
  `event/config_applied`.
- Watchdog: the board restarts if the main loop hangs for 30 s; the reason shows in the next `health`.
