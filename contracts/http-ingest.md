# HTTP photo ingest (contract v1)

Hornets upload photos over HTTP, separately from MQTT (spec 5.4, 7.2).

`POST {upload.primary}/api/ingest/photo`, body: the JPEG (max 2 MB), `Content-Type: image/jpeg`.

| Header | Required | Meaning |
|---|---|---|
| `Authorization: Bearer <upload_token>` | yes* | Token issued at provisioning; identifies the device |
| `X-Photo-Id` | yes | `<event ULID>-<frame>`, also listed in the event's `photos` |
| `X-Event-Id` | for event photos | ULID of the `event/{type}` message the photo belongs to |
| `X-Event-Type` | optional | Event type (`motion`, `snapshot`); used in the file name when the event has not reached the hive yet |
| `X-Ts` | yes | Capture time, unix ms by the hornet clock (0 if unsynced) |
| `X-Device-Id` | dev only | Device id; accepted instead of a token only when `Media:AllowUploadsWithoutToken` is on |

Responses:
- `201 Created`: stored. `200 OK`: this `X-Photo-Id` was already stored (safe retry, no duplicate).
- `400`: not a JPEG, bad ids. `413`: too large. Do not retry these.
- `401`: bad or missing token. `5xx` / network errors: keep the photo (SD outbox) and retry later.
