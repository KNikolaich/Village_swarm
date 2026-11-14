#!/usr/bin/env bash
# The container's main process: a backup every night at BACKUP_AT (default 03:00, the container's TZ), a restore check
# on BACKUP_VERIFY_DAY (1 = Monday ... 7 = Sunday, default 7, 0 = never) right after it. Plain sleeping: no cron needed.
# The schedule and the time zone may be set in the web UI (Settings -> Parameters -> Backups): they live in the database
# (modes: cfg.backup.at, cfg.backup.verify_day, cfg.backup.repository, cfg.timezone) and win over the .env values, which stay as the fallback.
# They are re-read every minute while waiting, so a change applies without restarting the container.
. /usr/local/bin/lib.sh

env_at="${BACKUP_AT:-03:00}"
env_verify_day="${BACKUP_VERIFY_DAY:-7}"
env_tz="${TZ:-}"

# Reads the settings into at / verify_day and the TZ of this process. Anything malformed falls back to .env.
load_settings() {
	at="$(cfg_value backup.at "$env_at")"
	[[ "$at" =~ ^([01][0-9]|2[0-3]):[0-5][0-9]$ ]] || at="$env_at"
	verify_day="$(cfg_value backup.verify_day "$env_verify_day")"
	[[ "$verify_day" =~ ^[0-7]$ ]] || verify_day="$env_verify_day"
	local tz
	tz="$(cfg_value timezone "$env_tz")"
	if [ -n "$tz" ] && [ -e "/usr/share/zoneinfo/$tz" ]; then export TZ="$tz"; elif [ -n "$env_tz" ]; then export TZ="$env_tz"; fi
	load_repository
}

load_settings
say "Backups at $at every day; restore check on day $verify_day; time zone ${TZ:-UTC}; off-site: ${RESTIC_REPOSITORY:-none}"

# A new install gets its first dump soon, not at the next 03:00 (a couple of minutes for the api to create the tables).
if [ -z "$(latest_dump)" ]; then
	say "No dump yet: the first one in 2 minutes"
	sleep 120
	backup.sh || say "The backup reported problems"
fi

while true; do
	# Seconds until the next HH:MM (today if it is still ahead, else tomorrow). Waits in minutes, re-reading the settings each time.
	while true; do
		load_settings
		target="$(date -d "today $at" +%s)"
		[ "$target" -gt "$(date +%s)" ] || target="$(date -d "tomorrow $at" +%s)"
		wait=$((target - $(date +%s)))
		if [ "$at" != "${said_at:-}" ]; then
			say "Next backup at $at, in $((wait / 3600)) h $((wait % 3600 / 60)) min"
			said_at="$at"
		fi
		[ "$wait" -gt 60 ] || break
		sleep 60
	done
	said_at=""
	sleep "$wait"
	backup.sh || say "The backup reported problems"
	if [ "$verify_day" != "0" ] && [ "$(date +%u)" = "$verify_day" ]; then
		verify.sh || say "The restore check reported problems"
	fi
done
