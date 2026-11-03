# MQTT contract v1

Source of truth for topics and payloads. Background: `docs/architecture.md`, section 4.
Changing a message format: first here (schema + examples), then firmware, backend, simulator, tests.

## Topics

Prefix: `vs/v1/`. `{id}` is a hornet device_id, e.g. `guard-gate1`.

| Topic | Direction | Retained | QoS | Schema |
|---|---|---|---|---|
| `vs/v1/dev/{id}/status` | dev → hive | yes | 1 | plain text `online` / `offline` (LWT) |
| `vs/v1/dev/{id}/info` | dev → hive | yes | 1 | `info` |
| `vs/v1/dev/{id}/health` | dev → hive | no | 0 | `health` |
| `vs/v1/dev/{id}/tele/{metric}` | dev → hive | no | 0 | `tele` |
| `vs/v1/dev/{id}/tele/batch` | dev → hive | no | 0 | `tele-batch` |
| `vs/v1/dev/{id}/state` | dev → hive | yes | 1 | `state` |
| `vs/v1/dev/{id}/event/{type}` | dev → hive | no | 1 | `event`, or `event-{type}` when defined |
| `vs/v1/dev/{id}/log` | dev → hive | no | 0 | `log` |
| `vs/v1/dev/{id}/cmd/{name}` | hive → dev | no | 1 | `cmd`, or `cmd-{name}` when defined |
| `vs/v1/dev/{id}/cmd/{name}/ack` | dev → hive | no | 1 | `ack` |
| `vs/v1/dev/{id}/config` | hive → dev | yes | 1 | `config` |
| `vs/v1/hive/{node}/heartbeat` | hive → all | no | 1 | `heartbeat` |
| `vs/v1/hive/active` | hive → dev | yes | 1 | `active` |
| `vs/v1/hive/display/{id}` | hive → dev | yes | 1 | `display` |
| `vs/v1/hive/drive/{id}` | hive → dev | no | 0 | `drive` |
| `vs/v1/hive/broadcast/cmd/{name}` | hive → dev | no | 1 | `cmd-{name}` |
| `vs/v1/prov/{mac}/...` | provisioning | — | — | step 10 |

Sleeping hornets (config `sleep_s` > 0, `sleep_mode` `deep` or absent): on each wake-up the hornet connects, publishes `status` `online`,
`info`, `health` and its readings, then disconnects cleanly and sleeps. A clean disconnect does not fire the LWT,
so `status` stays `online` while it sleeps; the hive marks it offline when it misses several wake-ups
(last message older than 3 x `sleep_s` + 60 s). Commands are not delivered while it sleeps; config is retained
and applied at the next wake-up.
An over-the-air update is therefore queued on the hive (`PUT /api/devices/{id}/ota/pending {build}`, `DELETE` cancels, `GET /api/ota/pending` lists):
at the first `health` of a wake-up (`reset_reason` `deepsleep`) the hive sends the usual `ota` command, which the hornet runs at once instead of going back to sleep.
It tries on at most 5 wake-ups and stops when the hornet reports the new version.
With `sleep_mode` `light` the hornet stays connected (Wi-Fi power saving, readings every `sleep_s`), so it
behaves like any other hornet: LWT, commands and config work as usual.

A hornet with a screen (config `display`) may show readings of other devices: the hive keeps the choice in config `display.items` (`device` absent = the hornet itself,
`metric`, `label`) and publishes the ready texts to `vs/v1/hive/display/{id}` (retained, on change; the hornet reads `vs/v1/hive/#` already). An empty `items` list
means nothing is chosen. The hornet does not read `display.items` itself. Layout: without `size` on any item the screen lays out up to 4 lines itself; with
`size` (1-10) every item carries `x`, `y` (pixels, top of the text) and `align` (`left` | `center` | `right`: which edge of the text `x` is), up to 8 items.
Text height = max(6, round(screen height × size / 10)) px; the screen takes its largest font not taller than that. A placed item's label is drawn in the smallest
font at `y`, the value right under it; the hive sends a placed label only when the person typed one.
An item may also be a **caption** (config `text` instead of `device` + `metric`): the hive sends it as the item's `text`, as typed (up to 24 characters).
For screens the hive draws itself (e-ink, `frame.bin` in monitor-http.md) an item may carry `font` (family id), `px` (6-120, the size of the font in pixels), `bold`
and `italic`; config `display.rotation` (0, 90, 180, 270) turns the picture, so items, coordinates and the background image are laid out in the turned screen.
The small OLED draws its own fonts and ignores these fields.
A reading outside the normal range set for it in the hive (the one the site paints light blue below and pink above) carries `range`: `low` or `high`.
The 0.42" OLED draws such a value black on a white plate with an arrow: up for `high`, down for `low`.

Infrared (roles `irrx` and `irtx`, ESP-01 on the WS2812 connector, GPIO2; `hw.ir_pin`): the **receiver** keeps the last code it saw in its retained state
`ir_last` = `"NEC 32 0x02FD807F"` (protocol, bits, data as hex; repeats and codes it cannot name are ignored), so a remote's button can be learned from the hive.
The **transmitter** does `cmd/ir` (`cmd-ir`: `protocol`, `bits`, `data`, `repeat`) and answers with an ack; infrared is one way, so `ok` means the code was sent, not that the device obeyed.
The buttons the family named are kept by the hive in config `ir.buttons`; `cmd/ir` from the API with `{button}` is turned into the code before it is sent. The same
code format and command are used for Tasmota devices (`contracts/tasmota.md`).

Rules:
- device_id matches `^[a-z][a-z0-9]*(-[a-z0-9]+)*$`, max 32 chars (`guard-gate1`, `meteo-out1`).
- Topic segments (`metric`, `type`, `name`) are lowercase `[a-z0-9_]+`. No spaces, no Cyrillic.
- Implementations: `backend/src/Hive.Contracts/Mqtt/Topics.cs`, `firmware/lib/hornet-core/src/Topics.h`.

## Payloads

- `schemas/{name}.schema.json`: JSON Schema 2020-12. `common.schema.json` holds shared definitions
  (ULID, device_id, the envelope); `event` and `cmd` are base schemas the specific ones extend.
- Every JSON message from a hornet carries the envelope `v, id, ts, seq, boot` (spec 4.3),
  **including `ack`**. `id` is a ULID and the deduplication key.
- Commands from the hive carry `v, cid, ts, ttl_s, by`; `cid` is the idempotency key.
- Field names are snake_case, enum values lowercase strings. Unknown fields are rejected,
  except on generic events (`event.schema.json`), which allow type-specific extras.

## Car (role `car`, `vs/v1/hive/drive/{id}`)

A car is an ESP32-CAM on two motors behind an L298N (type `car`, build `car-cam`). Driving does not use commands: a held
control in the hive sends `drive` `{left, right}` (-100..100, percent of `car.max_speed`) every 200 ms, QoS 0, no ack, and
`{0, 0}` on release. The hornet reads it like the display (it reads `vs/v1/hive/#`).

- **The motors stop by themselves** (all pins low): 500 ms after the last `drive`; at once when Wi-Fi or MQTT drops; at boot
  (the pins are set low before anything else, GPIO12 included, which must be low at power-up); and after `car.max_run_s`
  (60 s) of moving without a pause, until a `{0, 0}` comes. A `drive` older than 1 s by its `ts` (clock synced) is ignored.
- Wiring (config `hw`): `motor_pins` [IN1, IN2, IN3, IN4], IN1/IN2 the left motor (default 14, 15, 13, 12); `motor_pwm`
  (default true) the speed by PWM on the IN pins (20 kHz, above hearing), false full speed; `car.min_speed` (40 %) the duty
  where the motors start to turn, every speed above 0 gets at least that; `car.invert_left`, `car.invert_right`, `car.swap_sides`
  for a motor mounted the other way. Pins the camera or PSRAM use are refused (the old ones stay, the journal says why).
  No SD card on a car: its pins drive the motors.
- The light: `cmd/relay` `{channel: "light"}` on `hw.relay_pin` (GPIO4, the white LED), off at boot, `relay.max_on_s`.
- State (retained): `{"drive": "stopped" | "moving", "light": "on" | "off"}` on each change; health adds `stopped_by`
  (`deadman`, `link`, `max_run`, `hive`) of the last stop.
- Video: the camera's MJPEG stream at `http://{ip}:81/stream`, Basic login `hive` / `car.stream_key` (set by the hive at
  enrollment); the hive's go2rtc is the only one that opens it. A frame: `http://{ip}:81/jpg`, the same login.
- The hive: `POST /api/cars/{deviceId}/drive` `{left, right}` (users who may control, 15 a second per user, audit
  `car.drive` when a move starts after a stop) publishes the message; `{0, 0}` is a stop. `GET`/`PUT /api/cars/{deviceId}/setup`
  `{motorPins, motorPwm, invertLeft, invertRight, swapSides, maxSpeed, maxRunS, lightPin}` reads and sets the wiring and
  limits in the config (PUT: administrators). Pins: the free ones of the ESP32-CAM, 2, 4, 12, 13, 14, 15, all different.

## Switching Wi-Fi (`cmd/wifi`, `event/wifi`)

The hive can move running hornets to another network (a new router, a new password) without a cable. Three modes:

- **`stage`** (hornets that advertise `wifi.stage` in `info.caps`): the hornet keeps the network as a **spare** and changes
  nothing now (`event/wifi` phase `staged`). When it cannot join its own network (the router was changed), it tries the
  spare; once it has reached the broker over it, the spare becomes its own and the old network becomes the spare
  (`applied`). Changing the password of the same network works this way. Joining Wi-Fi is not enough: an awake hornet
  that has Wi-Fi but no broker leaves the spare after 2 minutes and the main network after 5 (the hive itself may be
  restarting), so an old network without a way to the hive cannot hold it.
- **`forget`** (hornets that advertise `wifi.forget`; no `ssid` or `pass`): the hornet drops its spare network. Refused
  while it runs on the spare.
- **`switch`** (the default): the hornet acks, stores the new credentials next to the old ones, restarts and tries them.
  Reached the broker: the new network is its own (`applied`). Not within about a minute (sooner for deep sleepers): it
  forgets them, restarts on the old network and reports `rolled_back`.

The password travels only in the command; neither the hive's command journal nor its MQTT monitor shows it.

## Examples

`examples/{schema}/{case}.json` must validate against `schemas/{schema}.schema.json`;
`examples/{schema}/invalid/{case}.json` must not. `backend/tests/Hive.Tests.Unit/ContractExamplesTests.cs`
checks both and round-trips every valid example through the C# DTOs in `Hive.Contracts.Messages`.
A new schema needs at least one example and a DTO, or the test fails.
