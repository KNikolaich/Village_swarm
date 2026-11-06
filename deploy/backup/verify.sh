#!/usr/bin/env bash
# "A backup that was never restored is not a backup" (spec 10.2): restores the newest dump into a scratch database
# on the same server, counts rows in the main tables, drops the scratch database; with an off-site repository it
# also reads a slice of it (restic check). The result is a "verify" row in backup_runs.
#   docker compose exec backup verify.sh
. /usr/local/bin/lib.sh

started="$(now_epoch)"
fail() { say "Verification failed: $*"; record_run verify "$started" false - "$*"; exit 1; }

wait_for_db || fail "database does not answer"
dump="$(latest_dump)"
[ -n "$dump" ] || fail "there is no dump to restore"

scratch="hive_verify_$$"
say "Restoring $(basename "$dump") into $scratch"
psql -X -q -d postgres -c "CREATE DATABASE $scratch" || fail "cannot create the scratch database"
cleanup() { psql -X -q -d postgres -c "DROP DATABASE IF EXISTS $scratch WITH (FORCE)" >/dev/null 2>&1; }
trap cleanup EXIT

# --no-owner: the roles of the original server do not matter for a check.
if ! errors="$(pg_restore --no-owner --exit-on-error -d "$scratch" "$dump" 2>&1)"; then
	fail "pg_restore: $(printf '%s\n' "$errors" | tail -1)"
fi
devices="$(psql -X -At -d "$scratch" -c 'SELECT count(*) FROM devices')" || fail "the restored database has no devices table"
events="$(psql -X -At -d "$scratch" -c 'SELECT count(*) FROM events')" || fail "the restored database has no events table"
live="$(psql -X -At -d "$PGDATABASE" -c 'SELECT count(*) FROM devices' 2>/dev/null || echo "?")"
say "Restored: $devices devices, $events events (live database: $live devices)"
detail="restored $(basename "$dump"): $devices devices, $events events"

if restic_configured; then
	restic_prepare || fail "off-site repository is not usable"
	say "Reading a slice of the off-site repository"
	if ! check="$(restic check --read-data-subset=5% 2>&1)"; then
		fail "restic check: $(printf '%s\n' "$check" | tail -1)"
	fi
	detail="$detail; off-site repository checked"
fi
record_run verify "$started" true - "$detail"
