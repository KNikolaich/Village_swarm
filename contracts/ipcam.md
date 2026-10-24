# IP cameras behind a Dahua NVR

Third-party IP cameras (an Asecam PTZ over ONVIF, a Dahua IPC…) recorded by a Dahua NVR. The hive talks to the NVR, never
to the cameras: one HTTP connection per NVR carries the motion of every channel, and the NVR hands out the snapshots.
Code: `Hive.Modules.IpCams`. Background: `docs/architecture.md`, 3.6 (other people's devices).

Mark of such a device in the hive: `devices.driver = 'ipcam'`, `devices.type = 'ipcam'`. Each NVR channel the family picked
is one device (`ipcam{nvr}-ch{channel}`, e.g. `ipcam1-ch2`), so it lives in the swarm, zones, the guard tile and the rules
like any hornet. Nothing goes over MQTT.

## Settings (`/api/ipcams`, admin)

| Call | What it does |
|---|---|
| `GET /api/ipcams` | NVRs with their channels. The password is never returned, only `passwordSet` |
| `POST /api/ipcams/probe` `{host, port?, username, password}` | "Проверить" before saving: reaches the NVR, checks the login, lists its channels |
| `POST /api/ipcams` `{name, host, port?, rtspPort?, username, password, channels: [{channel, name, zoneId?, recordWhenDisarmed?, snapshotSource?}]}` | adds the NVR and a device per channel; `rtspPort` 554 by default, `snapshotSource` see below |
| `PUT /api/ipcams/{id}` | same body; an empty `password` keeps the stored one. A channel left out is removed from the swarm (its history stays) |
| `DELETE /api/ipcams/{id}` | forgets the NVR, its devices are removed from the swarm |
| `POST /api/ipcams/{id}/probe` | "Проверить" with the stored login |
| `GET /api/ipcams/{id}/snapshot?channel=N` | a live JPEG taken the way photos are (any signed-in user); `X-Snapshot-Via: cgi\|rtsp`. None: 502 with the reason in `detail` |

The password is encrypted at rest (ASP.NET data protection, keys in `/srv/hive/keys`, like the hive's Wi-Fi password).

## What the hive asks the NVR (Dahua HTTP API, digest auth)

| Request | Use |
|---|---|
| `GET /cgi-bin/eventManager.cgi?action=attach&codes=[VideoMotion,VideoLoss]&heartbeat=5` | long-lived `multipart/x-mixed-replace` stream of events |
| `GET /cgi-bin/snapshot.cgi?channel=N` | JPEG of channel N (1-based); 6 s at most |
| `GET /cgi-bin/magicBox.cgi?action=getDeviceType` | probe: is it a Dahua, is the login right |
| `GET /cgi-bin/configManager.cgi?action=getConfig&name=ChannelTitle` | probe: channels and their names (`table.ChannelTitle[0].Name=Gate`) |
| `GET /cgi-bin/LogicDeviceManager.cgi?action=getCameraState&uniqueChannels[0]=-1` | probe, optional: which channels have a camera connected |

An event part of the stream is text like `Code=VideoMotion;action=Start;index=0` (`index` is the **0-based** channel; newer
firmware adds `;data={…}` with JSON over several lines, which is ignored). `Heartbeat` parts arrive every 5 s.
The video stream for a later stage is `rtsp://{host}/cam/realmonitor?channel=N&subtype=1`, kept as `rtspUrl` per channel.

## Behaviour

- **Motion**: `VideoMotion` `Start` of a channel → event `motion` of that device, through the same pipeline as MQTT events.
  Ignored for 20 s after the last one of the same channel (cooldown). `Stop` is ignored.
  - guard armed → `alarm`, `armed: true`; guard "arriving" → `warn`; disarmed → `info` (feed only).
  - photos: 3 snapshots 0.7 s apart, stored as media of the event (`{event id}-1..3`), only when the guard is armed or
    arriving, or the channel has `recordWhenDisarmed`. The event payload lists them in `photos`, so Telegram waits for them
    and sends them with the alarm (spec 9.5) exactly as for a guard-cam.
  - payload: `{"source": "camera", "channel": 2, "photos": ["…-1", "…-2", "…-3"], "via": "nvr"}`.
- **Time**: the NVR clock is not trusted; every event and photo has the hive's time of arrival.
- **Online**: the channels of an NVR are online while its event stream is up (a heartbeat or event at least every 30 s).
  Down or unreachable for more than 60 s → its channels go offline (and the usual "не на связи" after 15 minutes).
  `VideoLoss` `Start`/`Stop` takes one channel offline/online (camera unplugged from the NVR).
  The stream reconnects with backoff 2, 4, 8 … 60 s.
- **Snapshots**: per channel `snapshotSource` and `snapshotQuality`:
  - `auto` (default): `snapshot.cgi`, and when it gives no JPEG a frame of the RTSP stream. A Dahua NVR often has no snapshot
    of a third-party ONVIF camera (it answers 200 with `Error` text). The way that worked is kept as `snapshotVia` and goes
    first next time.
  - `cgi`: only `snapshot.cgi`; `rtsp`: only the stream.
  - RTSP: `ffmpeg -skip_frame nokey -analyzeduration 1000000 -probesize 1000000 -rtsp_transport tcp -i rtsp://{user}:{pass}@{host}:{rtspPort}/cam/realmonitor?channel=N&subtype=S -an -fps_mode passthrough -frames:v K -q:v 3 -f image2pipe -c:v mjpeg -`.
    Key frames only: decoding from whatever frame comes first (mid-GOP) gives grey pictures with fragments of what moved.
    The first key frame within 12 s, the next ones within 10 s each. An alarm's photos are K = 3 successive key frames of one
    run, each stored as it comes; a look is K = 1. `snapshotQuality` `fast` (default) takes the light sub-stream
    (`subtype=1`) first, `detailed` the main stream (`subtype=0`) first; the other one follows when the first gives nothing,
    for alarms and buttons alike. ffmpeg is in the api image. The URL with the login is passed as one argument and cut out
    of ffmpeg's messages; it is never logged or returned.
  - Every frame, from cgi or RTSP, is checked: a grey one (brightness mean 100–156 and nearly all pixels within ±4 of it, or
    almost no spread) is refused (`snapshot rejected: grey frame` in the log, the reason in the diagnostics) and the next
    stream or way is tried. Dark night pictures are flat but not grey, and stay.
  - One NVR is asked one frame at a time (a queue per NVR, 60 s at most per request with the wait); a request for a channel
    that already has one under way joins it. The size and time of each frame are logged.
  - A failure is logged (NVR id, channel, HTTP status, content type, the start of a non-JPEG body, ffmpeg's error) and written
    to the camera's journal as `warn` `snapshot failed: …`; a press or a motion that got no frame also leaves an `info` event
    `photo_failed` with the reason in the feed. Each `VideoMotion` that arrives is logged (Information), so the
    log shows whether the NVR reports motion of a camera at all.
- **Login lockout**: Dahua locks an account after a few wrong logins (403
  `Error {"ErrorCode":268632081,"Result":false,"RmLock":1320,"RmLogin":0}`: 0x10030011 = locked, `RmLock` seconds left,
  `RmLogin` attempts left). Every refusal (stream, snapshot, probe, ffmpeg's 401) is read:
  - locked (that code, `RmLogin` 0 or `RmLock` > 0): nothing goes to the NVR, RTSP included, until `RmLock` + 30 s;
  - 401 with attempts left: the same password waits 60 s; refused twice in a row, the hive stops until the login is saved again.
  - A 403 without a lock (an account without rights) is reported, not counted.
  - `GET /api/ipcams` gives `login: {state: ok|locked|wait|paused, until, attemptsLeft}`; the settings page says it.
- **Diagnostics** in `GET /api/ipcams` (kept in memory): `stream: {state: connected|connecting|down|off, since, lastLine,
  reconnects, lastError}`, per channel `lastMotionAt` and `lastSnapshot: {at, purpose, via, ok, ms, error, size}`.
- **Commands**: only `snapshot` is accepted (one frame from the NVR, stored as a snapshot), so the Telegram `/photo`, "📷 Ещё фото"
  and a rule's `snapshot` action work. Anything else is refused with 400.
