#!/usr/bin/env bash
# Shared helpers of the backup scripts (spec 10). Sourced, not run.
#
# Environment (compose passes it): PGHOST PGUSER PGPASSWORD PGDATABASE, TZ,
#   BACKUP_DIR        where dumps live (default /backups)
#   BACKUP_KEEP       how many dumps stay (default 7)
#   RESTIC_REPOSITORY off-site repository: a path, rest:https://..., sftp:..., s3:...; empty: no off-site copy
#   RESTIC_PASSWORD   its password (required with a repository)
set -uo pipefail

export PGHOST="${PGHOST:-postgres}" PGUSER="${PGUSER:-hive}" PGDATABASE="${PGDATABASE:-hive}"
BACKUP_DIR="${BACKUP_DIR:-/backups}"
BACKUP_KEEP="${BACKUP_KEEP:-7}"
DB_DIR="$BACKUP_DIR/db"

say() { printf '%s %s\n' "$(date '+%F %T')" "$*"; }

now_epoch() { date +%s; }

# record_run <kind> <started-epoch> <ok:true|false> <bytes|-> <detail>
# Writes the journal row the API reads (backup_runs). A missing table (first start, before the API has migrated)
# or a briefly unreachable database must not fail the backup itself, so a failure here is only reported.
record_run() {
	local kind="$1" started="$2" ok="$3" bytes="$4" detail="${5:-}"
	local size="NULL"
	[ "$bytes" != "-" ] && size="$bytes"
	psql -X -q -v ON_ERROR_STOP=1 -v kind="$kind" -v started="$started" -v ok="$ok" -v detail="$detail" \
		-d "$PGDATABASE" <<SQL || say "WARNING: could not write the run into backup_runs"
INSERT INTO backup_runs (kind, started_at, finished_at, ok, bytes, detail)
VALUES (:'kind', to_timestamp(:'started'::double precision), now(), :'ok'::boolean, $size, left(:'detail', 512));
SQL
}

# Waits until the database answers (the stack may still be starting).
wait_for_db() {
	for _ in $(seq 1 30); do
		pg_isready -q -d "$PGDATABASE" && return 0
		sleep 2
	done
	return 1
}

restic_configured() { [ -n "${RESTIC_REPOSITORY:-}" ]; }

# Initialises the repository on first use; fails loudly when the password is missing.
restic_prepare() {
	[ -n "${RESTIC_PASSWORD:-}" ] || { say "RESTIC_REPOSITORY is set but RESTIC_PASSWORD is not"; return 1; }
	restic cat config >/dev/null 2>&1 && return 0
	say "Initialising the off-site repository"
	restic init
}

latest_dump() { ls -1t "$DB_DIR"/hive-*.dump 2>/dev/null | head -1; }
