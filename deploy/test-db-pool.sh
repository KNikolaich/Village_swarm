#!/usr/bin/env bash
# Checks that in every compose combination the api's database pool (Maximum Pool Size) stays below postgres
# max_connections with room for the nightly dump and manual psql. Otherwise a burst of requests gets
# "too many clients" instead of waiting for a pooled connection. `make deploy-test`.
#
# The overlays are merged the way compose merges them: the last file that sets the value wins (postgres `command` is
# replaced as a whole, an environment key is replaced). With docker available the result is also read from
# `docker compose config` (as JSON: YAML output folds the long connection string), which does the real merge.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
RESERVE=10
fails=0
ok() { printf '  ok   %s\n' "$1"; }
bad() { printf '  FAIL %s\n' "$1"; fails=$((fails + 1)); }

last_value() { # pattern, files...: the value from the last file that has it
	local pattern="$1" v="" f x
	shift
	for f in "$@"; do
		x="$(sed -n "s/.*$pattern\([0-9][0-9]*\).*/\1/p" "$here/$f" | tail -1)"
		[ -n "$x" ] && v="$x"
	done
	echo "$v"
}

combos=(
	"compose.yaml"
	"compose.yaml compose.home.yaml"
	"compose.yaml compose.city.yaml"
	"compose.yaml compose.home.yaml compose.minipc.yaml"
)

for combo in "${combos[@]}"; do
	# shellcheck disable=SC2086
	max="$(last_value 'max_connections=' $combo)"
	# shellcheck disable=SC2086
	pool="$(last_value 'Maximum Pool Size=' $combo)"
	name="${combo// /+}"
	if [ -z "$max" ] || [ -z "$pool" ]; then
		bad "$name: max_connections='$max' pool='$pool' (both must be set)"
	elif [ $((pool + RESERVE)) -le "$max" ]; then
		ok "$name: pool $pool + reserve $RESERVE <= max_connections $max"
	else
		bad "$name: pool $pool + reserve $RESERVE > max_connections $max"
	fi
done

# The mini PC repeats the connection string; apart from the pool size it must stay the one of compose.yaml.
base="$(sed -n 's/^ *ConnectionStrings__Hive: //p' "$here/compose.yaml" | sed 's/;Maximum Pool Size=[0-9]*//')"
for f in "$here"/compose.*.yaml; do
	s="$(sed -n 's/^ *ConnectionStrings__Hive: //p' "$f" | sed 's/;Maximum Pool Size=[0-9]*//')"
	[ -z "$s" ] && continue
	if [ "$s" = "$base" ]; then ok "$(basename "$f"): connection string matches compose.yaml"; else bad "$(basename "$f"): connection string differs from compose.yaml: $s"; fi
done

if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
	tmp="$(mktemp -d)"
	trap 'rm -rf "$tmp"' EXIT
	# The api reads env_file .env from the project folder, so the check runs in its own folder with .env.example.
	{ cat "$here/.env.example"; echo; printf 'HIVE_VERSION=test\nPG_PASSWORD=test\nHIVE_BIND_IP=127.0.0.1\n'
		printf 'MQTT_ADMIN_PASSWORD=test\nCITY_MQTT_PASSWORD=test\n'; } >"$tmp/.env"
	for combo in "${combos[@]}"; do
		args=()
		for f in $combo; do args+=(-f "$here/$f"); done
		name="${combo// /+}"
		cfg="$(docker compose --project-directory "$tmp" --env-file "$tmp/.env" "${args[@]}" config --format json 2>&1)" \
			|| { bad "$name: docker compose config failed: $(echo "$cfg" | head -1)"; continue; }
		max="$(echo "$cfg" | sed -n 's/.*max_connections=\([0-9]*\).*/\1/p' | tail -1)"
		pool="$(echo "$cfg" | sed -n 's/.*Maximum Pool Size=\([0-9]*\).*/\1/p' | tail -1)"
		if [ -n "$max" ] && [ -n "$pool" ] && [ $((pool + RESERVE)) -le "$max" ]; then
			ok "$name (docker compose config): pool $pool, max_connections $max"
		else
			bad "$name (docker compose config): pool '$pool', max_connections '$max'"
		fi
	done
else
	echo "  skip docker compose config (no docker)"
fi

[ "$fails" -eq 0 ] && echo "db-pool: all passed" || { echo "db-pool: $fails failed"; exit 1; }
