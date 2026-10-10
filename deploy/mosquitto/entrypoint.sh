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
exec mosquitto -c /mosquitto/config/mosquitto.conf
