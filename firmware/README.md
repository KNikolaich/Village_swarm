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

## guard-cam on ESP32-CAM AI-Thinker

| What | Pin |
|---|---|
| PIR OUT (HC-SR501 / AM312) | GPIO13 |
| PIR VCC / GND | 5V / GND (AM312: 3V3) |
| microSD | on-board slot, used in 1-bit mode (frees GPIO4/12/13) |

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
- Commands: `arm {armed}`, `snapshot {count}`, `reboot`, `config_reload`; expired (`ttl_s`) and repeated
  (`cid`) commands are handled per the contract.
- Retained `config` (guard, camera, upload sections) is applied, saved to NVS and confirmed with
  `event/config_applied`.
- Watchdog: the board restarts if the main loop hangs for 30 s; the reason shows in the next `health`.
