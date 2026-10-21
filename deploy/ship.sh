#!/usr/bin/env bash
# Delivers the hive from the PC to the RPi over SSH and installs or updates it:
#   deploy/ship.sh kirill@192.168.10.4 [install.sh options, e.g. --host 192.168.10.4 --rtc]
# Uses the images from deploy/build-images.sh (the ones for the architecture of the target: arm64 RPi, amd64 VM). First time: install.sh (asks for the bot token);
# afterwards: update.sh with a database dump and rollback. Works from Git Bash on Windows.
set -euo pipefail
cd "$(dirname "$0")"

target="${1:?usage: ship.sh user@host [install options]}"
shift
version="$(cat dist/VERSION 2>/dev/null)" || { echo "run deploy/build-images.sh first" >&2; exit 1; }
# The images are built per processor: arm64 for the RPi, amd64 for a PC or a Proxmox VM. Take the ones this machine runs.
arch="$(ssh "$target" dpkg --print-architecture)"
case "$arch" in arm64|amd64) ;; *) echo "unsupported architecture on $target: $arch" >&2; exit 1 ;; esac
tarball="dist/hive-images-$version-$arch.tar.gz"
[ -f "$tarball" ] || { echo "no images for $arch: run deploy/build-images.sh linux/$arch" >&2; exit 1; }

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/hive-bundle/images"
cp -r compose.yaml compose.home.yaml compose.zigbee.yaml compose.city.yaml .env.example mosquitto install.sh update.sh restore.sh "$stage/hive-bundle/"
cp "$tarball" "$stage/hive-bundle/images/"
echo "$version" >"$stage/hive-bundle/images/VERSION"

echo "==> copying $version to $target"
ssh "$target" 'rm -rf /tmp/hive-bundle'
scp -rq "$stage/hive-bundle" "$target:/tmp/"

image="/tmp/hive-bundle/images/$(basename "$tarball")"
ssh -t "$target" "
	set -e
	# Update only when the hive is running: a half-finished install has a .env but no database yet.
	if sudo test -f /opt/hive/.env && sudo docker compose -f /opt/hive/compose.yaml -f /opt/hive/compose.home.yaml --project-directory /opt/hive ps -q postgres | grep -q .; then
		sudo cp -r /tmp/hive-bundle/compose.yaml /tmp/hive-bundle/compose.home.yaml /tmp/hive-bundle/compose.zigbee.yaml /tmp/hive-bundle/compose.city.yaml /tmp/hive-bundle/.env.example \
			/tmp/hive-bundle/mosquitto /tmp/hive-bundle/install.sh /tmp/hive-bundle/update.sh /tmp/hive-bundle/restore.sh /opt/hive/
		sudo bash /opt/hive/update.sh '$version' '$image'
	else
		sudo bash /tmp/hive-bundle/install.sh $*
	fi
	rm -rf /tmp/hive-bundle
"
