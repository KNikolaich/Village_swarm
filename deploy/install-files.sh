#!/usr/bin/env bash
# Copies the hive's compose files, broker config and scripts from a bundle to /opt/hive. install.sh and ship.sh use it.
#
#   sudo ./install-files.sh [bundle dir]      (default: the folder of this script)
#
# No list of compose files is kept here: every compose.*.yaml of the bundle goes along (compose.dev.yaml is for the PC),
# so turning an overlay on (zigbee, sms, minipc ...) is only an edit of COMPOSE_FILE in .env. Then every file that
# COMPOSE_FILE names (from .env, or .env.example before the first install) must be in /opt/hive: a missing one stops
# here with its name instead of failing later inside docker compose.
set -euo pipefail

HIVE_DIR="${HIVE_DIR:-/opt/hive}" # overridable for deploy/test-install-files.sh only
die() { printf '\033[1;31mОшибка:\033[0m %s\n' "$*" >&2; exit 1; }

SRC="$(cd "${1:-$(dirname "$0")}" && pwd)"
[ -f "$SRC/compose.yaml" ] || die "в $SRC нет compose.yaml"

if [ "$SRC" != "$(mkdir -p "$HIVE_DIR" && cd "$HIVE_DIR" && pwd)" ]; then
	files=("$SRC/compose.yaml")
	for f in "$SRC"/compose.*.yaml; do
		[ "$(basename "$f")" = compose.dev.yaml ] || files+=("$f")
	done
	for f in .env.example mosquitto install.sh install-files.sh update.sh restore.sh; do
		files+=("$SRC/$f")
	done
	cp -r "${files[@]}" "$HIVE_DIR/"
fi
chmod +x "$HIVE_DIR/install.sh" "$HIVE_DIR/install-files.sh" "$HIVE_DIR/update.sh" "$HIVE_DIR/restore.sh"

env_file="$HIVE_DIR/.env"
[ -f "$env_file" ] || env_file="$HIVE_DIR/.env.example"
compose_file="$(grep -E '^COMPOSE_FILE=' "$env_file" | tail -1 | cut -d= -f2-)"
compose_file="${compose_file:-compose.yaml}"
missing=()
IFS=: read -r -a listed <<<"$compose_file"
for f in "${listed[@]}"; do
	[ -n "$f" ] || continue
	case "$f" in /*) path="$f" ;; *) path="$HIVE_DIR/$f" ;; esac
	[ -f "$path" ] || missing+=("$f")
done
[ ${#missing[@]} -eq 0 ] || die "COMPOSE_FILE в $env_file называет файлы, которых нет в $HIVE_DIR: ${missing[*]}"
echo "compose: $compose_file"
