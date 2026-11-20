#!/usr/bin/env bash
# Puts launchers for the deploy scripts on the XFCE desktop of the current user (no root needed):
#   deploy/desktop-icons.sh            run again after moving the repository: the launchers point to its current path
# Each launcher opens a terminal that stays open after the script ends, so the result and any sudo prompt are visible.
# DESKTOP_DIR overrides the folder (deploy/test-desktop-icons.sh uses it).
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
desktop="${DESKTOP_DIR:-$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")}"
mkdir -p "$desktop"

launcher() { # file name, title, comment, icon, script
	local file="$desktop/$1.desktop"
	cat >"$file" <<EOF
[Desktop Entry]
Type=Application
Version=1.0
Name=$2
Comment=$3
Icon=$4
Exec=xfce4-terminal --hold --title="$2" --command="bash $repo/deploy/$5"
Terminal=false
Categories=Development;
EOF
	chmod +x "$file"
	# XFCE 4.18+ runs a desktop launcher without asking only when it is marked trusted by its checksum.
	if command -v gio >/dev/null 2>&1; then
		gio set -t string "$file" metadata::xfce-exe-checksum "$(sha256sum "$file" | cut -d' ' -f1)" 2>/dev/null || true
	fi
	echo "$file"
}

launcher hive-deploy-hive1 "Улей: обновить дом (hive1)" \
	"Собрать изменённые прошивки и образы, доставить на kirill@192.168.10.4" system-software-update deploy.sh
launcher hive-deploy-all "Улей: обновить город и дом" \
	"Собрать изменённые прошивки и образы, доставить в город (192.168.1.3), затем домой (192.168.10.4)" network-server deploy-all.sh
launcher hive-deploy-all-firmware "Улей: город и дом, все прошивки заново" \
	"Пересобрать и переподписать все прошивки шершней, затем обновить город и дом" view-refresh deploy-all-firmware.sh
