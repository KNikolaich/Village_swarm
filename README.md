# Village Swarm

Home automation for a country house: ESP32 "hornets" talk MQTT to the "hive" (Raspberry Pi 4),
which runs Mosquitto, PostgreSQL 17, a .NET 10 backend and a React UI.
Spec: [docs/architecture.md](docs/architecture.md). MQTT contract: [contracts/](contracts/).

## Layout

| Path | What |
|---|---|
| `backend/` | .NET 10 modular monolith (`Hive.slnx`), tests, swarm simulator |
| `frontend/` | Vite + React 19 + TypeScript + Tailwind |
| `firmware/` | PlatformIO, `lib/hornet-core` + one role per env (`guard-cam` first) |
| `contracts/` | MQTT topics, JSON schemas, examples |
| `deploy/` | docker compose files, Mosquitto config |

## Development

```sh
make dev-up      # Mosquitto on :1883, PostgreSQL 17 on :5432 (user/pass/db: hive)
make check       # build + tests + lint for backend, frontend, firmware
dotnet run --project backend/src/Hive.Api   # http://localhost:5080/healthz
```

Without `make` (Windows), run the commands from the `Makefile` directly.

## Swarm simulator

`backend/tools/Hive.Simulator` plays virtual hornets that speak the MQTT contract, so the backend and UI
can be developed without hardware. Scenarios live in `backend/tools/Hive.Simulator/scenarios/`:
`default.yaml` (guard-gate1, meteo-out1, heat-living1 with a scripted outage and reboot) and
`swarm.yaml` (20 hornets).

```sh
make dev-up
dotnet run --project backend/tools/Hive.Simulator -- backend/tools/Hive.Simulator/scenarios/default.yaml
docker compose -f deploy/compose.dev.yaml exec mosquitto mosquitto_sub -v -t 'vs/v1/#'
```

Each hornet publishes status (with LWT), info, health, telemetry, events and state; answers commands
(`arm`, `snapshot`, `relay`, `reboot`) with acks, honouring `ttl_s` and `cid` idempotency; applies the
retained `config` and confirms it with `event/config_applied`. While offline, events are buffered and
replayed with their original ids. The heater follows the local failsafe (min_c / max_c / max_on_s,
anti-freeze timer when the sensor fails) and reports `event/failsafe`. Camera photos are POSTed to
`{upload_url}/api/ingest/photo` once photo ingest exists (step 5).
