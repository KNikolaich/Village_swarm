#!/usr/bin/env bash
# Delivers the hive from the PC to the RPi over SSH and installs or updates it:
#   deploy/ship.sh kirill@192.168.10.4 [install.sh options, e.g. --host 192.168.10.4 --rtc]
# Uses the images from deploy/build-images.sh. First time: install.sh (asks for the bot token);
# afterwards: update.sh with a database dump and rollback. Works from Git Bash on Windows.
set -euo pipefail
cd "$(dirname "$0")"

target="${1:?usage: ship.sh user@host [install options]}"
shift
version="$(cat dist/VERSION 2>/dev/null)" || { echo "run deploy/build-images.sh first" >&2; exit 1; }
tarball="$(ls -1t dist/hive-images-"$version"-*.tar.gz | head -1)"

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/hive-bundle/images"
cp -r compose.yaml compose.home.yaml .env.example mosquitto install.sh update.sh "$stage/hive-bundle/"
cp "$tarball" "$stage/hive-bundle/images/"
echo "$version" >"$stage/hive-bundle/images/VERSION"

echo "==> copying $version to $target"
ssh "$target" 'rm -rf /tmp/hive-bundle'
scp -rq "$stage/hive-bundle" "$target:/tmp/"

image="/tmp/hive-bundle/images/$(basename "$tarball")"
ssh -t "$target" "
	set -e
	if sudo test -f /opt/hive/.env; then
		sudo cp -r /tmp/hive-bundle/compose.yaml /tmp/hive-bundle/compose.home.yaml /tmp/hive-bundle/.env.example \
			/tmp/hive-bundle/mosquitto /tmp/hive-bundle/install.sh /tmp/hive-bundle/update.sh /opt/hive/
		sudo bash /opt/hive/update.sh '$version' '$image'
	else
		sudo bash /tmp/hive-bundle/install.sh $*
	fi
	rm -rf /tmp/hive-bundle
"
