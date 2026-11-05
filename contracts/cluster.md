# Home and city (cluster)

Two nodes run the same stack (docs/architecture.md, 11). Settings: `Hive__Node` (`home` | `city`), `Hive__Role` (`active` | `observer`), `Cluster__Enabled`.

## Topics

| Topic | From | Retained | What |
|---|---|---|---|
| `vs/v1/hive/{node}/heartbeat` | each node, every `Cluster__HeartbeatSeconds` (30) | no | `heartbeat.schema.json`: `{v, ts, node, role, version}` |
| `vs/v1/hive/active` | the node that takes the active role | yes | `active.schema.json`: `{v, ts, node}`. The city publishes `city` when it goes standby; home publishes `home` when it is back |
| `vs/v1/sync/home/catalog` | home | yes | the catalog snapshot below, gzip-compressed JSON (binary payload) |

The Mosquitto bridge on home (written by `deploy/mosquitto/entrypoint.sh` from `CITY_MQTT_*`) copies out `vs/v1/dev/#`, `vs/v1/hive/home/#`, `vs/v1/sync/home/#`
and exchanges `vs/v1/hive/active`; it takes in `vs/v1/hive/city/#`. The login of the bridge on the city broker (`bridge-home`) may publish exactly those and read the city's.
Hornets cannot subscribe to `vs/v1/sync/#` (their ACL reads their own topics and `vs/v1/hive/#`): the catalog carries password hashes.

## Roles

| Role | Node | Rules | Notifications | UI |
|---|---|---|---|---|
| `Active` | home | run, may command | all | writes |
| `Observer` | city while home is alive | do not run (except the rules about the city's own devices) | only about `hive-home` (the other node) and the city's own devices | read only for the other node's data (the city's own hornets, rules, zones and the provisioning wizard stay open, see "The city's own hornets"): `POST/PUT/PATCH/DELETE /api/*` give 403, except `/api/auth/*` (signing in) `/api/me/*` (a person's own password and Telegram chats), `/api/settings/weather` and `/api/settings/language` (this node's own settings, never copied from home) |
| `Standby` | city while home is silent and a hornet speaks on the city broker | run, only notify | all | writes |

The city makes events on the device `hive-home`: `home_offline` (warn, `Cluster__HomeSilenceS`, 180 s without a heartbeat), `home_offline_long` (alarm,
`Cluster__HomeAlarmS`, 900 s), `home_online` (info). `GET /api/cluster` answers `{enabled, node, role, home: {phase, lastHeartbeatAt, silentSeconds}, activeNode, catalogAt, catalog}`.

## Catalog snapshot (`vs/v1/sync/home/catalog`)

Published by home when the content changes (checked every `Cluster__CatalogCheckS`, 60 s) and at least every `Cluster__CatalogEveryS` (300 s). JSON (camelCase), gzip:

```
{ v: 1, ts: <unix ms>, node: "home",
  zones:   [{ name, kind, sort }],
  devices: [{ deviceId, type, name, zone (name), driver, sort, disabled, info (only for drivers other than hornet) }],
  rules:   [{ name, enabled, definition }],
  users:   [{ userName, displayName, passwordHash, roles }] }
```

The city applies a snapshot that is newer than the last one it applied, by name (ids differ between the two databases):
zones and devices are created or updated (a device removed at home arrives as `disabled`); rules are created or updated, and a rule that came from home
(`updated_by = sync:home`) and is no longer in the snapshot is deleted; users are created or updated with the same password hash and roles.
Nothing the city has of its own is deleted: no zone, no device, no user, no rule it made itself. Runtime data (status, readings, firmware, photos) is not in the catalog.

## Catch-up of events after home is back (`vs/v1/hive/home/replica/request`, `vs/v1/sync/city/replica`)

While home was gone and hornets spoke to the city broker directly, the city saw events home never got (the bridge only copies home to city). When home takes the active role back from the city
(it saw `vs/v1/hive/active = city`) it asks the city for what it missed, over the same bridge:

| Topic | From | What |
|---|---|---|
| `vs/v1/hive/home/replica/request` | home | JSON `{v: 1, id, since}`: `id` is a ULID, `since` unix ms (the time the city took the role, minus 5 minutes) |
| `vs/v1/sync/city/replica` | city | gzip JSON chunk, not retained: `{v: 1, requestId, part, parts, more, lastTs, events: [{id, deviceId, type, severity, ts, receivedAt, payload, armed}]}` |

The city answers with the events whose `ts` is after `since`, oldest first, except the ones about `hive-home` (those are the city's own view of home). At most 2000 events per request, in chunks of 100;
`more: true` means the limit cut the list and `lastTs` is where to ask again. An empty answer is one chunk with no events, so home knows it is done.
Home inserts the events it does not have (by `id`; devices it does not know are skipped) with `via_node = City`. Nothing is notified and no rule runs for them: the city already told people.
Home repeats a request every 60 s until the whole answer is in. Hornets cannot read `vs/v1/sync/#`; the bridge on home takes `vs/v1/sync/city/#` in, and the bridge login on the city may read it.

## The city's own hornets (`devices.local`, `vs/v1/sync/city/locals`)

The city is also a hive of its own: hornets enrolled through its screen (the provisioning wizard, a board trading its code, `POST /api/provision/*`) are marked `local`; everything else it has is a copy of home's. An observer city
refuses changes to the copies (403) and allows them for local devices: `/api/devices/{id}/...` of a local device, `/api/provision/*`, `/api/ingest/photo`, `/api/rules`, `/api/zones`, `/api/failover`. A rule runs on the
observer when its trigger device is local, and may command a local device; notifications about a local device go out. Rules and zones the catalog sent are put back by the next catalog.

The city tells home which hornets it made itself, retained, gzip JSON on `vs/v1/sync/city/locals` (taken in by the bridge): `{v: 1, ts, node: "city", devices: [{deviceId, type, name, hw}]}`. Home makes each of them a login on its
broker from the shared secret and the id (`CityBrokerLogin`) and a row in the swarm with `visitor` set (not reported as missing while away). The city writes the dacha's broker into the config of its own hornets (`mqtt_city`,
`after_s` 20, `probe_s` 900; `PUT /api/failover` on the city names the dacha), so a hornet carried to the dacha leaves the city broker within 20 seconds and settles on the dacha's. Home's data is not copied back.

## Photos home to city (`PUT /api/ingest/replica/media/{id}`, spec 7.3)

Home sends every photo to the city over HTTP (through the router's tunnel), alarm photos first. Settings → "Запасной брокер (город)" holds the address (`failover.city`: `host`, optional `replicaUrl`; the photo address is
`replicaUrl`, else `https://{host}`). Home's queue is the `media` table itself: a photo is waiting while `replicated_at` is null, in three classes read in this order: alarm photos (event severity alarm or
critical; kept in the queue 7 days), photos of other events (24 h), snapshots and time-lapse frames (6 h). A refused photo is tried again after 1, 2, 4... minutes (at most an hour, `replica_tries`, `replica_next_at`);
a city that does not answer ends the round without counting against the photo. `GET /api/replica/media` (admin) answers `{enabled, url, waiting, waitingAlarms, lastSentAt, lastError}`.

Request: body is the JPEG; headers `X-Replica-Key` (made from `Cluster__BridgePassword`, the secret both nodes share: `CityBrokerLogin.Password(secret, "replica-media")`), `X-Device-Id`, `X-Kind` (`Photo`, `Snapshot`, `Timelapse`),
`X-Ts` (unix ms), `X-Sha256` (of the body), `X-Event-Id` and `X-Event-Type` when the photo belongs to an event. The city checks the key, that the bytes are a JPEG with that SHA-256 and that the id is `<ULID>-<n>`, and answers
`201` (kept), `200` (it had the photo already), `401` (key), `400` (not the photo that was named). The copy has `uploaded_via` = `replica`. An observer city lets this one call through, like sign-in.
Home's HTTP client accepts the city's self-signed certificate: the way is the tunnel and the key only the two nodes can make.
