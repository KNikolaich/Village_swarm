# MQTT topics (contract v1)

Source of truth for the topic hierarchy. Full description: `docs/architecture.md`, section 4.2.
JSON schemas for payloads land in `schemas/` (build step 2).

Prefix: `vs/v1/`. `{id}` is a hornet device_id, e.g. `guard-gate1`.

| Topic | Direction | Retained | QoS |
|---|---|---|---|
| `vs/v1/dev/{id}/status` | dev → hive | yes | 1 |
| `vs/v1/dev/{id}/info` | dev → hive | yes | 1 |
| `vs/v1/dev/{id}/health` | dev → hive | no | 0 |
| `vs/v1/dev/{id}/tele/{metric}` | dev → hive | no | 0 |
| `vs/v1/dev/{id}/state` | dev → hive | yes | 1 |
| `vs/v1/dev/{id}/event/{type}` | dev → hive | no | 1 |
| `vs/v1/dev/{id}/log` | dev → hive | no | 0 |
| `vs/v1/dev/{id}/cmd/{name}` | hive → dev | no | 1 |
| `vs/v1/dev/{id}/cmd/{name}/ack` | dev → hive | no | 1 |
| `vs/v1/dev/{id}/config` | hive → dev | yes | 1 |
| `vs/v1/hive/{node}/heartbeat` | hive → all | no | 1 |
| `vs/v1/hive/active` | hive → dev | yes | 1 |
| `vs/v1/hive/broadcast/cmd/{name}` | hive → dev | no | 1 |
| `vs/v1/prov/{mac}/...` | provisioning | — | — |

Rules:
- device_id matches `^[a-z][a-z0-9]*(-[a-z0-9]+)*$`, max 32 chars (`guard-gate1`, `meteo-out1`).
- Topic segments (`metric`, `type`, `name`) are lowercase `[a-z0-9_]+`. No spaces, no Cyrillic.
- Implementations: `backend/src/Hive.Contracts/Mqtt/Topics.cs`, `firmware/lib/hornet-core/src/Topics.h`.
  Both are covered by tests; keep them in sync with this file.
