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
dotnet run --project backend/src/Hive.Api   # http://localhost:5080/readyz
```

In Development the api applies EF migrations on start; elsewhere run `Hive.Api --migrate` once.
It subscribes to `vs/v1/dev/+/#` and writes devices, telemetry (monthly partitions), events,
logs and health to PostgreSQL in batches, dropping duplicate message ids. Integration tests in
`backend/tests/Hive.Tests.Integration` start PostgreSQL and Mosquitto with Testcontainers (Docker required).

New migration: `cd backend && dotnet tool restore && dotnet ef migrations add <Name> --project src/Hive.Infrastructure --startup-project src/Hive.Api --output-dir Data/Migrations`.

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
`{upload_url}/api/ingest/photo` (see `contracts/http-ingest.md`).

## REST API (so far)

Everything under `/api` needs a session except `/api/auth/*` and `/api/ingest/photo`. On the first start with an
empty database the api creates the user `admin`: in Development the password is `hive-dev-admin`, elsewhere it is
generated and written to the log once (or set `Auth:BootstrapAdminPassword`). Swagger UI: `/swagger` (Development).
Live updates: SignalR hub `/hubs/live` with `Event`, `Telemetry`, `DeviceStatus`, `DeviceHealth`.

| Endpoint | What |
|---|---|
| `POST /api/auth/login`, `/api/auth/totp`, `/api/auth/logout` | Session cookie; TOTP step when enabled |
| `GET /api/me`, `POST /api/me/password`, `/api/me/totp/setup|enable|disable` | Own account, authenticator app |
| `GET /api/devices`, `/api/devices/{id}` | Device registry |
| `GET /api/events?type=&device=&zone=&severity=&from=&to=&cursor=&limit=` | Event feed, newest first, with photo links |
| `POST /api/events/{id}/ack` | Acknowledge an alarm |
| `GET /api/media?device=&date=YYYY-MM-DD&eventId=&cursor=` | Photos of a day (house time zone `Hive:TimeZone`) |
| `GET /api/media/days?from=&to=&device=` | Days that have photos, for the archive calendar |
| `GET /api/media/{id}`, `/api/media/{id}/thumb` | Original JPEG (Range, ETag) and 320 px WebP preview |
| `POST`/`DELETE /api/media/{id}/pin` | Keep forever / allow retention |
| `POST /api/ingest/photo` | Hornet photo upload (`contracts/http-ingest.md`) |

Media files live under `Media:Root` (`/srv/hive/media` on the hive, `deploy/.data/media` in development).

## Telegram bot

Put the token from @BotFather into `deploy/.env` (git-ignored; see `deploy/.env.example`):
`TELEGRAM__TOKEN=...`, plus `TELEGRAM__ENDPOINTS__0__PROXY=socks5://...` or `...__BASEURL=https://...`
when api.telegram.org is not reachable directly. In Development the api reads that file itself.
Then in the web UI: Settings → Telegram → "Получить код", and send `/link CODE` to the bot.
Alarms come with buttons (ack, more photos, 1 h mute) and photos; commands: /status /arm /disarm /photo
/events /temp /devices /mute /help. Without a token everything else works and alarms stay in the feed.

## Adding a hornet

Web UI → Рой → «Добавить шершня»: pick the type, id and name to get a one-time code, flash the board from the
browser (Chrome/Edge, ESP Web Tools; images come from `firmware/dist/<role>` after `pio run`), then send Wi-Fi
and the code over USB. The board calls `/api/provision/enroll`, gets its own Mosquitto login (dynsec, only its
own topics) and photo upload token, and comes online. «Заменить» issues a code for the same id (history kept,
the old board loses access); «Удалить» revokes the login. The simulator can do the same against a real hive:
`Hive.Simulator scenario.yaml --hive http://hive:5080 --login admin --password ...`.

## Users

`dotnet run --project backend/src/Hive.Api -- --add-user <login> <admin|member|viewer>` prints a one-time password.
