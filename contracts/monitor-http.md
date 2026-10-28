# Monitor screens over HTTP (contract v1)

A monitor anywhere in the world (an e-ink panel at the city flat, say) reads what its screen should show over HTTPS,
through the hive's internet entrance (KeenDNS → Caddy :8088, docs/keenetic-access.md) or inside the house. It only reads:
the token opens the texts of that one monitor's screen and nothing else (no commands, no device list, no history).

`GET /api/monitor/v1/frame`

| Header | Required | Meaning |
|---|---|---|
| `Authorization: Bearer <screen_token>` | yes | Issued on the monitor's page ("Доступ снаружи"); shown once, the hive keeps only its SHA-256. 20 letters and digits in groups of four (`k7m2-9xqe-...`), none that look alike; dashes, spaces and case do not matter. Tokens issued before (43 characters, case-sensitive) still work |
| `If-None-Match` | optional | The `ETag` of the last frame: nothing changed → `304` with no body |

Responses:
- `200 OK`, `application/json`: the same message the hive publishes to `vs/v1/hive/display/{id}`
  (`schemas/display.schema.json`: `v`, `ts`, `items` with `label`, `text` and, placed, `x`, `y`, `size`, `align`),
  with `ETag` (changes only when the texts or their places change, not with `ts`) and `Cache-Control: no-store`.
- `304 Not Modified`: same frame as `If-None-Match`. A sleeping panel keeps its picture and goes back to sleep.
- `401`: no token, a wrong or revoked one, or the device is not a monitor any more. Do not retry until reconfigured.
- `429`: more than 12 reads a minute from one address. Back off; a frame changes at most every 15 s anyway.
- `5xx` / network errors: keep showing the last frame and mark it as old (for example "нет связи с 14:20").

`GET /api/monitor/v1/frame.bin`: the same headers and answers, but the body is the picture the hive draws for the screen
(e-ink panels: the hive owns fonts, layout and background, the panel only shows the picture):

- `200 OK`, `application/octet-stream`: one bit a pixel, rows top to bottom, 8 pixels a byte with the leftmost in the high
  bit, each row padded to whole bytes; a set bit is black. Headers `X-Frame-Width`, `X-Frame-Height` give the size (the
  screen of the monitor's board, e.g. 200×200 = 5000 bytes for the Waveshare 1.54"). `ETag` / `304` as above.
- `404`: the hive does not draw this monitor's screen (a board with its own fonts, like the 0.42" OLED): use `frame`.

The picture is made of the screen's background image (if the person uploaded one; its size is that of the screen as it is turned), the items in their fonts
(`font`, `px`, `bold`, `italic`), the rotation the person chose and whether the picture is inverted (white on black, config
`display.invert`, every tab alike); the `ETag` changes with any of them, and with the fonts the hive knows. `X-Frame-Width` and
`X-Frame-Height` are those of the panel itself, not of the turned screen: the hive turns the picture back before it sends it.

### Tabs (panels with buttons)

A board with several screens to flip through (Elecrow CrowPanel 3.7", `crowpanel-epd37`: 240×416, a rocker with a press,
MENU and EXIT) reads them one at a time: `GET /api/monitor/v1/frame.bin?tab=<n>`, `n` from 0. A board without tabs ignores
`tab`. Answers as above, plus:

- `X-Frame-Tabs`: how many tabs the board has (3 for `crowpanel-epd37`: 0 Weather, 1 Measure, 2 Calendar). A `tab` beyond
  them is `404`. The panel takes the count from this header.
- `X-Frame-Rotation`: how the hive turned the picture (0, 90, 180, 270, clockwise; the board's default when the person did
  not choose: 270 for `crowpanel-epd37`, which stands landscape). One turn for all the tabs: each lays itself out in a
  landscape or a portrait screen. The panel draws its own marks (the tab cursor, the "нет связи" banner) in the same turn.
- `X-Frame-Tab-Weights`: how wide the cells of the tab bar are relative to each other, left to right, e.g. `2,3,3`
  (Weather, the shortest word, gets the least room). Absent: equal cells. The panel inverts the cell under its cursor by it:
  the left edge of cell `i` is `width * (w0 + … + w(i-1)) / (w0 + … + w(n-1))` (integer division), its right edge that of
  the next cell, the last one's the screen's.
- The `ETag` is per tab.

Every tab ends in a tab bar the panel can find without asking: the bottom 22 pixels of the turned screen, as many cells
left to right as there are tabs (`X-Frame-Tab-Weights` says how wide), the shown tab drawn inverted (white on black). While the person moves the rocker,
the panel shows only its cursor inverted: it inverts the cell under the cursor and turns the shown tab's cell back to white
(the panel knows which tab it shows). The press shows the chosen tab from its memory at once and then reads it anew. Every
tab is read at the panel's own interval. Times are the hive's time zone.

`X-Frame-Refresh` (both `frame` and `frame.bin`, also on `304`): while the person monitors the panel (its page,
"Мониторинг": for 0.5-12 hours, every 1, 2, 5 or 10 minutes), the seconds until the panel should read again. The panel
reads at the shorter of this and its own interval; when the header is gone, the monitoring is over and its own interval
holds again.

A panel may say its battery with every read: request header `X-Battery-mV` (integer millivolts, 2500..4500; absent when
it cannot measure it). The hive keeps it as the monitor's `battery_voltage` reading (volts) and draws the battery on the
tabs that have one; a value outside the range is ignored.

Monitors far away that speak only HTTP (no MQTT, no enrollment) are added by hand in the UI: `POST /api/devices/remote-monitors`
`{deviceId, name, board}` (admin), then a token on their page. Their first read marks them online; the time of the last read
is when the hive last saw them.

A reading older than 30 minutes comes as `--`, so a frame never shows an old number as if it were fresh. The hive notes
the time and address of the last read (shown on the monitor's page); a new token replaces the old one at once.
