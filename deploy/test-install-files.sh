#!/usr/bin/env bash
# Checks install-files.sh in a temporary folder: no root, no Docker. `make deploy-test`.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
fails=0
ok() { printf '  ok   %s\n' "$1"; }
bad() { printf '  FAIL %s\n' "$1"; fails=$((fails + 1)); }
check() { local name="$1"; shift; if "$@" >/dev/null 2>&1; then ok "$name"; else bad "$name"; fi; }
refuse() { local name="$1"; shift; if "$@" >/dev/null 2>&1; then bad "$name"; else ok "$name"; fi; }

# A bundle like the one ship.sh stages: the real files of deploy/, compose.dev.yaml included to see it is left out.
bundle="$tmp/bundle"
mkdir -p "$bundle"
cp -r "$here"/compose*.yaml "$here/.env.example" "$here/mosquitto" "$here/install.sh" "$here/install-files.sh" \
	"$here/update.sh" "$here/restore.sh" "$bundle/"
run() { HIVE_DIR="$tmp/hive" bash "$bundle/install-files.sh" "$bundle"; }

check "first install: COMPOSE_FILE from .env.example is satisfied" run
for f in "$here"/compose.*.yaml; do
	name="$(basename "$f")"
	[ "$name" = compose.dev.yaml ] && continue
	check "copies $name" test -f "$tmp/hive/$name"
done
refuse "leaves compose.dev.yaml out" test -e "$tmp/hive/compose.dev.yaml"
check "scripts are executable" test -x "$tmp/hive/install-files.sh" -a -x "$tmp/hive/update.sh"

echo "COMPOSE_FILE=compose.yaml:compose.home.yaml:compose.minipc.yaml" >"$tmp/hive/.env"
check "an overlay named in .env that the bundle brings is accepted" run

echo "COMPOSE_FILE=compose.yaml:compose.nowhere.yaml" >"$tmp/hive/.env"
refuse "an overlay named in .env that nobody has stops the copy" run
message="$(run 2>&1 || true)"
case "$message" in *compose.nowhere.yaml*) ok "the message names the missing file" ;; *) bad "the message names the missing file" ;; esac

echo "COMPOSE_FILE=compose.yaml:compose.home.yaml" >"$tmp/hive/.env"
check "runs from /opt/hive itself (bundle = target)" env HIVE_DIR="$tmp/hive" bash "$tmp/hive/install-files.sh" "$tmp/hive"

[ "$fails" -eq 0 ] && echo "install-files: all passed" || { echo "install-files: $fails failed"; exit 1; }
