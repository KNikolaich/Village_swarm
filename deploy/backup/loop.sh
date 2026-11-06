#!/usr/bin/env bash
# The container's main process: a backup every night at BACKUP_AT (default 03:00, the container's TZ), a restore check
# on BACKUP_VERIFY_DAY (1 = Monday ... 7 = Sunday, default 7, 0 = never) right after it. Plain sleeping: no cron needed.
. /usr/local/bin/lib.sh

at="${BACKUP_AT:-03:00}"
verify_day="${BACKUP_VERIFY_DAY:-7}"
say "Backups at $at every day; restore check on day $verify_day; off-site: ${RESTIC_REPOSITORY:-none}"

# A new install gets its first dump soon, not at the next 03:00 (a couple of minutes for the api to create the tables).
if [ -z "$(latest_dump)" ]; then
	say "No dump yet: the first one in 2 minutes"
	sleep 120
	backup.sh || say "The backup reported problems"
fi

while true; do
	# Seconds until the next HH:MM (today if it is still ahead, else tomorrow).
	target="$(date -d "today $at" +%s)"
	[ "$target" -gt "$(date +%s)" ] || target="$(date -d "tomorrow $at" +%s)"
	wait=$((target - $(date +%s)))
	say "Next backup in $((wait / 3600)) h $((wait % 3600 / 60)) min"
	sleep "$wait"
	backup.sh || say "The backup reported problems"
	if [ "$verify_day" != "0" ] && [ "$(date +%u)" = "$verify_day" ]; then
		verify.sh || say "The restore check reported problems"
	fi
done
