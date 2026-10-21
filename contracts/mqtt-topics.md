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
