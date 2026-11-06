# Tasmota and OpenBeken devices

Devices that run Tasmota (ESP8266/ESP8285/ESP32) or OpenBeken (Beken BK7231) instead of our own firmware.
They keep their own MQTT layout; the hive translates it (`TasmotaAdapter`, `TasmotaCommands`). Background: `docs/architecture.md`, 3.6.

Mark of such a device in the hive: `devices.driver = 'tasmota'`. Our own hornets have `'hornet'` and use `mqtt-topics.md`.

## Setup

The device's MQTT **Topic** is its `device_id` (`heat-bedroom1`), the default full topic `%prefix%/%topic%/` applies.
`POST /api/provision/tasmota` `{deviceId, type, name, maxOnMinutes?}` (admin) creates the device and its broker login
(role `hornet-{id}`) and returns the Tasmota console lines to paste (`Topic`, `TelePeriod 60`, `PowerOnState 0`,
`PulseTime1` for `heat`, `MqttHost/Port/User/Password`). The password is shown once and not stored. Asking again for the
same id issues a new login and the old one stops working. An id of one of our own hornets is refused.

## Topics

| Topic | Direction | What the hive does with it |
|---|---|---|
| `tele/{id}/LWT` | dev → hive | `Online` / `Offline`: device status |
| `tele/{id}/STATE` | dev → hive | health (`UptimeSec`, `Heap` in KB, `Wifi.Signal` in dBm, `MqttCount`) and relay state |
| `tele/{id}/SENSOR` | dev → hive | telemetry, see below |
| `stat/{id}/POWER`, `POWER2` … | dev → hive | `ON` / `OFF`: relay state, and the answer that closes a waiting `relay` command |
| `stat/{id}/RESULT` | dev → hive | only `{"Restart": …}`: closes a waiting `reboot` command |
| `cmnd/{id}/…` | hive → dev | commands, see below |

ACL of the device's login: publish `tele/{id}/#` and `stat/{id}/#`, subscribe `cmnd/{id}/#`, nothing else.
The api's login reads `tele/#` and `stat/#` and writes `cmnd/#`.

## Relay state

Stored as the device state `{ "relay": "on" }`, or `relay1`, `relay2` … for several relays (Tasmota `POWER`, `POWER1`, `POWER2`).
A report updates only the channels it names.

## Telemetry (`tele/{id}/SENSOR`)

| Tasmota | Metric |
|---|---|
| first sensor block with `Temperature` | `temperature` (the next ones `temperature2`, `temperature3`) |
| `Humidity` | `humidity` |
| `Pressure` | `pressure_hpa` |
| `ENERGY.Power`, `Voltage`, `Current`, `Today`, `Total` | `power_w`, `voltage_v`, `current_a`, `energy_today_kwh`, `energy_total_kwh` |

## Commands

| Hive command | Sent to the device |
|---|---|
| `relay` `{channel, set: "on"}` | `cmnd/{id}/POWER` `ON` (`POWER2` for channel `relay2`; any other channel name means the first relay) |
| `relay` `{channel, set: "off"}` | `POWER` `OFF` |
| `relay` `{channel, set: "toggle"}` | `POWER` `TOGGLE` |
| `relay` with `for_s` | `Backlog` `PulseTime1 {n}; POWER ON`: the device switches itself off when the time is up, even if the hive is gone. `n` is `for_s × 10` up to 11 s, otherwise `for_s + 100` (Tasmota's PulseTime); `for_s` is at most 64800 |
| `reboot` | `Restart 1` |

Anything else (`snapshot`, `wifi`, `ota` …) is refused with 400. A command is `ok` when the device answers on `stat/{id}/POWER`
(or `RESULT` for a restart) within two minutes; otherwise it times out like any other.

## Failsafe

- `PowerOnState 0`: after a power cut the relay stays off.
- `PulseTime1` set at setup for `heat`: a heater never stays on longer than `maxOnMinutes` (default 360) without a new order.
  A later `for_s` order changes it for that switch-on, so a plain `on` after a timed one is limited to that time too.

## Infrared

A Tasmota device with an IR LED and an IR receiver (`tasmota-ir` firmware, GPIOs `IRsend` and `IRrecv`) is added with `ir: true`
in `POST /api/provision/tasmota`; its announced commands then include `ir`.

| Hive | Device |
|---|---|
| command `ir` `{protocol, bits, data, repeat?}` | `cmnd/{id}/IRSend` `{"Protocol":"NEC","Bits":32,"Data":"0x02FD807F","Repeat":0}`; `protocol` is a name (not `UNKNOWN`), `bits` 1 to 64, `data` hex with `0x`, `repeat` 0 to 10 |
| command `ir` `{button}` | the code saved under that name (see below) is sent; an unknown button is refused with 400 |
| `stat/{id}/RESULT` `{"IRSend": …}` | closes the waiting `ir` command |
| `tele/{id}/RESULT` `{"IrReceived": {"Protocol", "Bits", "Data"}}` | device state `ir_last` = `"NEC 32 0x02FD807F"`, so a remote's button can be learned without filling the event feed |

Buttons: `GET` and `PUT /api/devices/{id}/ir-buttons` `{ "buttons": { "Питание": {"protocol": "NEC", "bits": 32, "data": "0x02FD807F"} } }`
(at most 40, names up to 32 characters), kept in the device config under `ir.buttons`. Infrared is one-way: a command is `ok` when the
device says it sent the code, not when the appliance took it.

## Console and log

`command` `tasmota` `{command, value?}` (administrators only, 403 otherwise) sends `cmnd/{id}/{command}` with `value` as the payload, as typed:
`command` is a Tasmota command name (letters and digits, up to 32), `value` up to 300 characters without control characters. It is `ok` when
the device answers with any `stat/{id}/RESULT`. Values of commands whose name contains `pass`, `key`, `token` or `secret` are kept as `***` in the journal.

Every `tele/{id}/RESULT` and `stat/{id}/RESULT` (a received RF or infrared code, an answer to a console command) is also stored as a device log line
(level `info`, up to 400 characters), at most two a second per device. Plain relay answers (`{"POWER":"ON"}`) are left out.

