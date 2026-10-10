#!/usr/bin/env bash
# Builds the hive images on the PC and packs them for the RPi (spec 13.3, without a registry):
#   deploy/build-images.sh [linux/arm64|linux/amd64]
# Result: deploy/dist/hive-images-<version>-<arch>.tar.gz and deploy/dist/VERSION. ship.sh copies them.
# Build the firmware first (pio run) if the "add hornet" wizard should offer fresh images: firmware/dist is
# baked into the api image.
set -euo pipefail
cd "$(dirname "$0")/.."

platform="${1:-linux/arm64}"
arch="${platform#linux/}"
version="${VERSION:-$(git describe --tags --always --dirty | tr '+' '-')}"
prefix="${HIVE_IMAGES:-ghcr.io/knikolaich/village-swarm}"
mkdir -p deploy/dist

for part in api web; do
	echo "==> $prefix-$part:$version ($platform)"
	docker buildx build --platform "$platform" --build-arg HIVE_VERSION="$version" \
		-f "deploy/docker/$part.Dockerfile" -t "$prefix-$part:$version" --load .
done

out="deploy/dist/hive-images-$version-$arch.tar.gz"
docker save "$prefix-api:$version" "$prefix-web:$version" | gzip -1 >"$out"
echo "$version" >deploy/dist/VERSION
echo "==> $out ($(du -h "$out" | cut -f1))"
