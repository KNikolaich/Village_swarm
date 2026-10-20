#!/usr/bin/env bash
# One backup run (spec 10.1): a database dump, then, when RESTIC_REPOSITORY is set, an off-site copy of the dumps,
# the photos and the things a replacement RPi needs (keys, broker accounts, the certificate authority, .env).
# Every step leaves a row in backup_runs. Exit code 0 only when everything that is configured worked.
#   docker compose exec backup backup.sh
. /usr/local/bin/lib.sh

status=0
mkdir -p "$DB_DIR"
if ! wait_for_db; then
	say "The database does not answer"
	record_run dump "$(now_epoch)" false - "database does not answer"
	exit 1
fi

# --- 1. dump ---------------------------------------------------------------------------------------------------
started="$(now_epoch)"
stamp="$(date +%Y%m%d-%H%M%S)"
file="$DB_DIR/hive-$stamp.dump"
say "Dumping the database to $file"
if error="$(pg_dump -Fc -d "$PGDATABASE" -f "$file.part" 2>&1)" && mv "$file.part" "$file"; then
	bytes="$(stat -c %s "$file")"
	say "Dump ready: $bytes bytes"
	record_run dump "$started" true "$bytes" "hive-$stamp.dump"
	# Keep the newest BACKUP_KEEP dumps.
	ls -1t "$DB_DIR"/hive-*.dump | tail -n +"$((BACKUP_KEEP + 1))" | xargs -r rm -f
else
	rm -f "$file.part"
	say "Dump failed: $error"
	record_run dump "$started" false - "$error"
	status=1
fi

# --- 2. off-site copy ---------------------------------------------------------------------------------------------
if restic_configured; then
	started="$(now_epoch)"
	if ! restic_prepare; then
		record_run offsite "$started" false - "repository is not usable (check RESTIC_REPOSITORY and RESTIC_PASSWORD)"
		exit 1
	fi
	say "Copying to $RESTIC_REPOSITORY"
	paths=("$DB_DIR")
	for p in /source/media /source/keys /source/mosquitto /source/zigbee2mqtt /source/caddy /source/config; do [ -e "$p" ] && paths+=("$p"); done
	if out="$(restic backup --host hive --tag hive --json "${paths[@]}" 2>&1)"; then
		added="$(printf '%s\n' "$out" | grep '"message_type":"summary"' | tail -1 | sed -n 's/.*"data_added":\([0-9]*\).*/\1/p')"
		say "Off-site copy ready (added ${added:-?} bytes)"
		# 7 daily, 5 weekly, 12 monthly snapshots stay (spec 10.1); the rest is pruned.
		restic forget --host hive --keep-daily 7 --keep-weekly 5 --keep-monthly 12 --prune >/dev/null 2>&1 \
			|| say "WARNING: restic forget failed (the copy itself is fine)"
		record_run offsite "$started" true "${added:--}" "snapshot of ${#paths[@]} locations"
	else
		say "Off-site copy failed: $(printf '%s\n' "$out" | tail -3)"
		record_run offsite "$started" false - "$(printf '%s\n' "$out" | tail -1)"
		status=1
	fi
fi
exit "$status"
