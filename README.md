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
