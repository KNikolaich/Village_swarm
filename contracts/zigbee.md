# Zigbee devices (zigbee2mqtt)

Zigbee sensors and plugs are paired with a USB dongle, driven by the zigbee2mqtt container, which publishes to the hive's broker
under its base topic (`Zigbee__BaseTopic`, default `zigbee2mqtt`). The hive reads those topics (`ZigbeeAdapter`) and turns them into the
same ingest items as a hornet's messages. Devices have `devices.driver = 'zigbee'`. Background: `docs/architecture.md`, 3.5.

Off unless `Zigbee__Enabled=true`. The friendly name of a paired device is its `device_id`: the hive renames the device in zigbee2mqtt when it is added.

## Broker logins

With dynamic security on, the hive creates the login of the container once, when it is missing: user `Zigbee__MqttUser` (default
`zigbee2mqtt`), password `Zigbee__MqttPassword`, role `zigbee2mqtt-bridge` that may publish and subscribe `{base}/#` and nothing else.
The api's own role gets `{base}/#` as well. To change the password later: delete the client `zigbee2mqtt` with `mosquitto_ctrl` and restart the api.

## Topics the hive reads

| Topic | What the hive does with it |
|---|---|
| `{base}/bridge/state` | `online` / `offline` (zigbee2mqtt 1.x) or `{"state": "online"}` (2.x): bridge status |
| `{base}/bridge/devices` (retained) | the catalog of paired devices: `ieee_address`, `friendly_name`, `type`, `supported`, `interview_completed`, `definition.{model,vendor,description}`; the coordinator is left out |
| `{base}/{id}/availability` | `online` / `offline` or `{"state": …}`: device status |
| `{base}/{id}` | a report, see below. `{id}` must be a valid device_id: a device that has not been added has its IEEE address as name and is ignored |

## Reports (`{base}/{id}`)

Every report marks the device online. Fields become:

| zigbee2mqtt | Hive |
|---|---|
| `temperature`, `humidity`, `pressure` | metrics `temperature`, `humidity`, `pressure_hpa` |
| `battery` | metric `battery_pct` |
| `voltage` | ≥ 1000: millivolts of a battery, metric `battery_voltage` (V); below: volts of a mains device, metric `voltage_v` |
| `linkquality`, `illuminance_lux` / `illuminance`, `power`, `current`, `energy` | metrics `linkquality`, `illuminance_lux`, `power_w`, `current_a`, `energy_kwh` |
| `state`, `state_l1`, `state_l2` … (`ON`/`OFF`) | device state `relay`, `relay1`, `relay2` … (`on`/`off`); also closes a waiting `relay` command |
| `occupancy` becoming `true` | event `motion` (info) |
| `contact` becoming `false` | event `door_open` (info) |
| `water_leak` becoming `true` | event `leak` (critical) |
| `smoke` becoming `true` | event `smoke` (critical) |
| `tamper` becoming `true` | event `tamper` (warn) |

An event is raised when the value changes to the one that matters, not for every report that repeats it. Whether a motion or an open door is an
alarm is decided by the rules (the guard mode), as for any other event.

## Commands

| Hive command | Sent |
|---|---|
| `relay` `{set: "on"}` | `{base}/{id}/set` `{"state":"ON"}` |
| `relay` `{set: "off"}` | `{"state":"OFF"}` |
| `relay` `{set: "toggle"}` | `{"state":"TOGGLE"}` |

A `relay` command with `for_s` is refused with 400: nothing on a Zigbee plug would switch it off if the hive were gone, so no heater should be
on a Zigbee plug for a timed run. `reboot` and the rest are refused too. The command is `ok` when the device's next report carries its state.

## Pairing API (admin)

| Request | What it does |
|---|---|
| `GET /api/zigbee` | `{enabled, bridge, permitJoinUntil, devices: [{ieee, friendlyName, type, vendor, model, description, supported, interviewCompleted, deviceId}]}`; `deviceId` is set when the device has been added |
| `POST /api/zigbee/permit-join` `{seconds}` | publishes `{base}/bridge/request/permit_join` `{"time": seconds}` (0 to 254; 0 ends pairing) |
| `POST /api/zigbee/devices` `{ieee, deviceId, type, name, zoneId?}` | publishes `{base}/bridge/request/device/rename` `{"from": <friendly name>, "to": deviceId}` and creates the device (`devices.mac` holds the IEEE address without `0x`) |
