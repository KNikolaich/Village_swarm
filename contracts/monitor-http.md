# Monitor screens over HTTP (contract v1)

A monitor anywhere in the world (an e-ink panel at the city flat, say) reads what its screen should show over HTTPS,
through the hive's internet entrance (KeenDNS → Caddy :8088, docs/keenetic-access.md) or inside the house. It only reads:
the token opens the texts of that one monitor's screen and nothing else (no commands, no device list, no history).

`GET /api/monitor/v1/frame`

| Header | Required | Meaning |
|---|---|---|
| `Authorization: Bearer <screen_token>` | yes | Issued on the monitor's page ("Доступ снаружи"); shown once, the hive keeps only its SHA-256 |
| `If-None-Match` | optional | The `ETag` of the last frame: nothing changed → `304` with no body |

Responses:
- `200 OK`, `application/json`: the same message the hive publishes to `vs/v1/hive/display/{id}`
  (`schemas/display.schema.json`: `v`, `ts`, `items` with `label`, `text` and, placed, `x`, `y`, `size`, `align`),
  with `ETag` (changes only when the texts or their places change, not with `ts`) and `Cache-Control: no-store`.
- `304 Not Modified`: same frame as `If-None-Match`. A sleeping panel keeps its picture and goes back to sleep.
- `401`: no token, a wrong or revoked one, or the device is not a monitor any more. Do not retry until reconfigured.
- `429`: more than 12 reads a minute from one address. Back off; a frame changes at most every 15 s anyway.
- `5xx` / network errors: keep showing the last frame and mark it as old (for example "нет связи с 14:20").

A reading older than 30 minutes comes as `--`, so a frame never shows an old number as if it were fresh. The hive notes
the time and address of the last read (shown on the monitor's page); a new token replaces the old one at once.
