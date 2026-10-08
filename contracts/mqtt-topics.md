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

## Examples

`examples/{schema}/{case}.json` must validate against `schemas/{schema}.schema.json`;
`examples/{schema}/invalid/{case}.json` must not. `backend/tests/Hive.Tests.Unit/ContractExamplesTests.cs`
checks both and round-trips every valid example through the C# DTOs in `Hive.Contracts.Messages`.
A new schema needs at least one example and a DTO, or the test fails.
