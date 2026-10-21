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
| `Observer` | city while home is alive | do not run | only about `hive-home` (the other node) | read only: `POST/PUT/PATCH/DELETE /api/*` give 403, except `/api/auth/*` (signing in) `/api/me/*` (a person's own password and Telegram chats), `/api/settings/weather` and `/api/settings/language` (this node's own settings, never copied from home) |
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
