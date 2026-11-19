#!/usr/bin/env bash
# Checks desktop-icons.sh in a temporary folder and that the scripts the launchers run exist. `make deploy-test`.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
fails=0
ok() { printf '  ok   %s\n' "$1"; }
bad() { printf '  FAIL %s\n' "$1"; fails=$((fails + 1)); }

if DESKTOP_DIR="$tmp" bash "$here/desktop-icons.sh" >/dev/null 2>&1; then ok "desktop-icons.sh runs"; else bad "desktop-icons.sh runs"; fi
for name in hive-deploy-hive1 hive-deploy-all hive-deploy-all-firmware; do
	f="$tmp/$name.desktop"
	if [ -x "$f" ]; then ok "$name.desktop is there and executable"; else bad "$name.desktop is there and executable"; continue; fi
	script="$(sed -n 's/^Exec=.*bash \([^"]*\)".*/\1/p' "$f")"
	if [ -f "$script" ]; then ok "$name runs an existing script ($(basename "$script"))"; else bad "$name runs an existing script: '$script'"; fi
	if bash -n "$script"; then ok "$(basename "$script") parses"; else bad "$(basename "$script") parses"; fi
	if command -v desktop-file-validate >/dev/null 2>&1; then
		if desktop-file-validate "$f" >/dev/null 2>&1; then ok "$name.desktop validates"; else bad "$name.desktop validates: $(desktop-file-validate "$f" 2>&1 | head -1)"; fi
	fi
done

[ "$fails" -eq 0 ] && echo "desktop-icons: all passed" || { echo "desktop-icons: $fails failed"; exit 1; }
