#!/bin/sh
# First start: create the dynamic security file with the api's admin login (spec 13.4 step 5).
# Changing MQTT_ADMIN_PASSWORD later has no effect: the login lives in the file from then on.
set -e
file=/mosquitto/data/dynamic-security.json
if [ ! -f "$file" ]; then
	: "${MQTT_ADMIN_USER:?MQTT_ADMIN_USER is not set}"
	: "${MQTT_ADMIN_PASSWORD:?MQTT_ADMIN_PASSWORD is not set}"
	mosquitto_ctrl dynsec init "$file" "$MQTT_ADMIN_USER" "$MQTT_ADMIN_PASSWORD" >/dev/null
	echo "dynamic security initialised for $MQTT_ADMIN_USER"
fi
chown -R mosquitto:mosquitto /mosquitto/data

# Home node only: copy the hornets' topics to the city broker and take the city's back (spec 11.2). Rewritten at every start.
mkdir -p /mosquitto/config/conf.d
rm -f /mosquitto/config/conf.d/city-bridge.conf
if [ -n "${CITY_MQTT_HOST:-}" ]; then
	: "${CITY_MQTT_PASSWORD:?CITY_MQTT_PASSWORD is not set}"
	cat >/mosquitto/config/conf.d/city-bridge.conf <<EOF
connection city
address ${CITY_MQTT_HOST}:${CITY_MQTT_PORT:-1883}
remote_clientid hive-home-bridge
remote_username ${CITY_MQTT_USER:-bridge-home}
remote_password ${CITY_MQTT_PASSWORD}
bridge_protocol_version mqttv311
try_private true
cleansession false
start_type automatic
restart_timeout 5 60
keepalive_interval 30
# What home publishes goes to the city (hornets, home's heartbeat); what the city says comes back.
topic vs/v1/dev/# out 1
topic vs/v1/hive/home/# out 1
# The home catalog (devices, rules, zones, users) is retained, so a restarted city has it at once. Hornets cannot read this topic.
topic vs/v1/sync/home/# out 1
topic vs/v1/hive/city/# in 1
# The city's answer to a request for events home missed while it was down. Hornets cannot read it.
topic vs/v1/sync/city/# in 1
# What the city sends back to a request for events missed while home was down. Hornets cannot read it.
topic vs/v1/sync/city/# in 1
topic vs/v1/hive/active both 1
EOF
	echo "bridge to the city: ${CITY_MQTT_HOST}:${CITY_MQTT_PORT:-1883}"
fi
exec mosquitto -c /mosquitto/config/mosquitto.conf
