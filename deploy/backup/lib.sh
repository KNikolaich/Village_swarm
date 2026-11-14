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

# cfg_value <key> <fallback>: the value saved in Settings (modes: cfg.<key>), or the fallback when the row is missing,
# empty or the database does not answer.
cfg_value() {
	local v
	v="$(psql -X -At -d "$PGDATABASE" -v key="cfg.$1" -c "SELECT value FROM modes WHERE key = :'key'" 2>/dev/null)" || v=""
	printf '%s' "${v:-$2}"
}

# The off-site repository set in Settings (Settings -> Data and maintenance -> Backups) wins over .env RESTIC_REPOSITORY,
# which stays the fallback. The password is never kept in the database: it stays in .env.
env_repository="${RESTIC_REPOSITORY:-}"
load_repository() { export RESTIC_REPOSITORY="$(cfg_value backup.repository "$env_repository")"; }
load_repository

restic_configured() { [ -n "${RESTIC_REPOSITORY:-}" ]; }

# Initialises the repository on first use; fails loudly when the password is missing.
restic_prepare() {
	[ -n "${RESTIC_PASSWORD:-}" ] || { say "RESTIC_REPOSITORY is set but RESTIC_PASSWORD is not"; return 1; }
	restic cat config >/dev/null 2>&1 && return 0
	say "Initialising the off-site repository"
	restic init
}

latest_dump() { ls -1t "$DB_DIR"/hive-*.dump 2>/dev/null | head -1; }

# --- "Check" button in Settings: can this container use the off-site address? ---------------------------------------
# The web page cannot reach the repository itself, so it leaves a request in the database (modes: backup.check.request =
# "<id>|<address>", the address may be empty = no off-site copy) and this container answers (modes: backup.check.result =
# "<id>|<true|false>|<one line>"). Nothing is written to the repository by a check, except a tiny file in a folder to prove it is writable.

# check_repository <address>: prints "<true|false>|<what we found>".
check_repository() {
	local repo="$1" out rc dev_repo dev_dumps free
	[ -n "$repo" ] || { echo "true|no address: copies stay on the hive's own drive only"; return; }
	[ -n "${RESTIC_PASSWORD:-}" ] || { echo "false|RESTIC_PASSWORD is not set in .env (the password of the copy stays there)"; return; }
	if [[ "$repo" == /* ]]; then
		[ -d "$repo" ] || { echo "false|folder $repo does not exist inside the backup container (the host folder BACKUP_REPO_DIR is mounted as /repo)"; return; }
		if ! touch "$repo/.hive-check.$$" 2>/dev/null; then echo "false|the folder is there, but the hive cannot write to it (check the share's rights)"; return; fi
		rm -f "$repo/.hive-check.$$"
		dev_repo="$(stat -c %d "$repo" 2>/dev/null)"; dev_dumps="$(stat -c %d "$BACKUP_DIR" 2>/dev/null)"
		free="$(df -Pk "$repo" 2>/dev/null | awk 'NR==2 {printf "%.1f GB free", $4 / 1048576}')"
		if [ -n "$dev_repo" ] && [ "$dev_repo" = "$dev_dumps" ]; then
			echo "false|the folder is writable ($free), but it is on the same drive as the hive's backups: it protects from nothing if that drive dies"; return
		fi
		out="$(timeout 60 restic -r "$repo" cat config 2>&1)"; rc=$?
		if [ "$rc" -eq 0 ]; then echo "true|folder is writable, $free; a backup repository is already there"; return; fi
		if [ -z "$(ls -A "$repo" 2>/dev/null)" ]; then echo "true|folder is writable, $free; empty: the repository is created at the first backup"; return; fi
		echo "false|the folder is not empty and holds no backup repository for this password: $(printf '%s' "$out" | tail -1 | cut -c1-140)"; return
	fi
	out="$(timeout 60 restic -r "$repo" cat config 2>&1)"; rc=$?
	if [ "$rc" -eq 0 ]; then echo "true|reachable, the repository is there and the password fits"; return; fi
	if [ "$rc" -eq 124 ]; then echo "false|no answer in 60 seconds (host down, wrong address or the network blocks it)"; return; fi
	if printf '%s' "$out" | grep -qiE 'wrong password|no key found'; then echo "false|reachable, but the password in .env does not fit the repository there"; return; fi
	if printf '%s' "$out" | grep -qiE 'is there a repository|does not exist|unable to open config file'; then echo "true|reachable, no repository yet: it is created at the first backup"; return; fi
	echo "false|$(printf '%s' "$out" | tail -1 | cut -c1-200)"
}

# handle_check_request: answers a new request from Settings (once per request id).
handle_check_request() {
	local row id repo done result
	row="$(psql -X -At -d "$PGDATABASE" -c "SELECT value FROM modes WHERE key = 'backup.check.request'" 2>/dev/null)" || return 0
	[ -n "$row" ] || return 0
	id="${row%%|*}"; repo="${row#*|}"
	done="$(psql -X -At -d "$PGDATABASE" -c "SELECT value FROM modes WHERE key = 'backup.check.result'" 2>/dev/null)" || return 0
	[ "${done%%|*}" != "$id" ] || return 0
	say "Checking the off-site address '${repo:-none}' (asked in Settings)"
	result="$(check_repository "$repo")"
	say "Check: $result"
	psql -X -q -v ON_ERROR_STOP=1 -v val="$id|$result" -d "$PGDATABASE" <<SQL || say "WARNING: could not write the check result"
INSERT INTO modes (key, value, updated_at, updated_by) VALUES ('backup.check.result', left(:'val', 250), now(), 'system:backup')
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by;
SQL
}
