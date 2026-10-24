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
  - guard armed → `alarm`, `armed: true`; guard "arriving" → `warn` with `"arriving": true` (see below); disarmed → `info` (feed only).
  - photos: 3 snapshots 0.7 s apart, stored as media of the event (`{event id}-1..3`), only when the guard is armed or
    arriving, or the channel has `recordWhenDisarmed`. The event payload lists them in `photos`, so Telegram sends them with
    the alarm (spec 9.5) as for a guard-cam: the album goes 3 s after the first photo is saved, with what has come by then;
    photos that come later are sent as replies to the alarm (waited for up to 60 s), never dropped.
  - payload: `{"source": "camera", "channel": 2, "photos": ["…-1", "…-2", "…-3"], "via": "nvr"}`.
- **Time**: the NVR clock is not trusted; every event and photo has the hive's time of arrival.
- **Online**: the channels of an NVR are online while its event stream is up (a heartbeat or event at least every 30 s).
  Down or unreachable for more than 60 s → its channels go offline (and the usual "не на связи" after 15 minutes).
  `VideoLoss` `Start`/`Stop` takes one channel offline/online (camera unplugged from the NVR).
  The stream reconnects with backoff 2, 4, 8 … 60 s.
- **Snapshots**: per channel `snapshotSource` and `snapshotQuality`:
  - `auto` (default): `snapshot.cgi`, and when it gives no JPEG a frame of the RTSP stream. A Dahua NVR often has no snapshot
    of a third-party ONVIF camera (it answers 200 with `Error` text). Which way goes first (`snapshotVia`) changes only after
    three failures of it in a row, or when the other way gives its first frame clearly faster (median of the last five, 20 %
    less); a way left because it failed, or one that was faster, is tried first again every ten minutes (snapshot.cgi once
    at start if a channel was stored as rtsp). Each change is logged with its reason.
  - A burst cut short (a snapshot that does not come in time after the first ones) is finished by the next way for the frames
    still missing, not given up; the cut is logged with its reason. Each frame of a burst is logged with its delay and, from
    the stream, its time in the stream relative to the first (ffmpeg `showinfo`).
  - `cgi`: only `snapshot.cgi`; `rtsp`: only the stream.
  - RTSP: `ffmpeg -skip_frame nokey -analyzeduration 1000000 -probesize 1000000 -rtsp_transport tcp -i rtsp://{user}:{pass}@{host}:{rtspPort}/cam/realmonitor?channel=N&subtype=S -an -fps_mode passthrough -frames:v K -q:v 3 -f image2pipe -c:v mjpeg -`.
    A look (K = 1) decodes key frames only (`-skip_frame nokey`): decoding from whatever frame comes first (mid-GOP) gives grey
    pictures with fragments of what moved. An alarm's photos (K = 3) come from one run that keeps nothing before the first key
    frame and then one frame every 0.7 s (`-vf select='eq(pict_type\,I)+not(isnan(prev_selected_n))',fps=1000/700`, no
    `-skip_frame`): the frames after the key frame are decoded against it, so the burst takes the first key frame plus 1.4 s,
    not one key frame per photo. The first frame within 12 s, the next ones within 10 s each; each is stored as it comes. `snapshotQuality` `fast` (default) takes the light sub-stream
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

## A camera on its own (no NVR)

`POST /api/ipcams/probe` with `kind: "camera"` tries the address as a Dahua camera (its HTTP API, as above, channel 1) and then
as an ONVIF camera; `POST /api/ipcams` with `kind: "camera"` and exactly one channel (1) saves it. It is one `ipcam` device with
no parent (no `nvr` device). The answer of the probe also carries `vendor` (dahua, onvif), `model`, `snapshotUrl`, `rtspMain`,
`rtspSub`, `eventsUrl` and `motion` (dahua, onvif, none); the form sends them back on save. Addresses the camera gives with its
own idea of its host (NAT, another interface) get the host the hive reached it by.

ONVIF (SOAP 1.2, WS-UsernameToken with PasswordDigest = Base64(SHA1(nonce + created + password)), plus HTTP digest when asked):

| Request | Use |
|---|---|
| `GetDeviceInformation`, `GetCapabilities` (device service, `/onvif/device_service`) | maker, model, media and event service addresses |
| `GetProfiles`, `GetSnapshotUri`, `GetStreamUri` (media service) | snapshot address of the first profile, streams of the first two (main, sub) |
| `CreatePullPointSubscription` (event service), `PullMessages` (10 s, 20 messages), `Renew` (each minute), `Unsubscribe` | motion |
| `GetSystemDateAndTime` (no login) | sign of life of a camera without events |

- Motion: a notification whose topic speaks of motion (`tns1:RuleEngine/CellMotionDetector/Motion`, `tns1:VideoSource/MotionAlarm`…)
  with `IsMotion`/`State`/`Motion` true is a motion of channel 1, handled as a Dahua VideoMotion (cooldown, severity, photos).
- Snapshots: `snapshotUrl` in place of snapshot.cgi; RTSP from `rtspMain`/`rtspSub` (found, or `rtspMain` typed in) with the login
  added. Without a snapshot address only the stream is used.
- No event service: `motion: none`, the camera is online while it answers `GetSystemDateAndTime`, snapshots work, motion and
  alarms do not; the settings say so.
- A refused login (`ter:NotAuthorized` or 401) counts in the same lockout guard as Dahua's.

## "Едут хозяева" (guard mode arriving), for every camera

The cameras stay armed while the family is on its way, so their motion is most likely the family. Such motion is a quiet
message **with photos**:

- The event is `warn` with `"arriving": true` in its payload. IP cameras make it so themselves; a guard-cam reports `alarm`
  (its firmware knows only armed or not), and the hive turns its `motion`/`door_open` alarm into this when presence is
  `arriving`, before it is stored (`ArrivingMode`, `IngestPipeline`). No firmware change.
- Telegram: the text, silent, "🚗 Движение, едут хозяева: …", and its photos as a silent album (the same 3 s window, late
  photos as replies). Buttons: **🔓 Снять охрану** (asks "Да, снять / Отмена" in place, then disarms) and **🚨 Это не мы**
  (arms at once: guard armed, presence away). The message keeps its text and photos; its buttons say what was done.
- Grouped apart from alarms (`{device}:{type}:arriving`), so a real alarm right after is never folded into it.
- Other warnings stay text only, without photos.

## Live video

go2rtc (`alexxit/go2rtc`, version pinned in `deploy/compose.yaml`, 128 MB) runs as its own container, reachable only inside
the docker network; no port is published and it has no RTSP or WebRTC server of its own. Its image goes in the deploy bundle.

- The hive owns go2rtc's streams (`VideoStreams`): two per IP camera, `{device}-sub` and `{device}-main`, whose sources are
  the RTSP addresses with the login (the same as for snapshots). They are pushed through go2rtc's API (`PUT/DELETE
  /api/streams`) on start, on every change of the NVRs and cameras, and checked every minute (go2rtc forgets them when it
  restarts). go2rtc keeps them in a config file in RAM (tmpfs): no camera password on the disk. A camera is opened only
  while someone watches.
- The browser reaches go2rtc only through Caddy: `/streams/ws?src={stream}` (MSE over WebSocket, go2rtc's video-rtc
  protocol) and `/streams/frame.jpeg?src={stream}`. Before each, Caddy asks the api (`forward_auth` → `GET
  /api/video/authorize`, `X-Forwarded-Uri`): 204 for a signed-in user and a stream of the list, 403 otherwise (an unknown
  name, an address given as src, two src), 401 when not signed in. Every other `/streams/*` is 404; go2rtc's API and page
  are never reachable.
- `GET /api/video/streams`: `{enabled, cameras: [{deviceId, name, zoneId, zone, nvrId, channel, sub, main, status}]}`, no
  addresses, no logins. `enabled` is false when `Video:Go2RtcUrl` is empty.
- The overview widget «Видео»: «Нет камеры» by default (no connection at all); a camera picked by zone; «Снимки» (a frame
  every 5 s through `GET /api/ipcams/{id}/snapshot`, the same way as photos), «Видео SD» (sub), «Видео HD» (main, chosen
  for the full screen). The choice is kept per browser. Playback starts only on «Смотреть» and stops when the tab is
  hidden, the widget is off screen, or after 5 minutes without a touch («Продолжить просмотр?»). It shows the codec and
  bitrate, and says so plainly when the browser cannot play the stream's codec (H.265: switch the sub-stream to H.264 or
  use «Снимки»). An unacknowledged alarm of a camera offers «Показать …», which never starts by itself.
