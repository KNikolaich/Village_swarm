#!/usr/bin/env bash
# Hive1 at home and the city, with every hornet firmware rebuilt (and re-signed) first, changed or not.
set -uo pipefail
WITH_FIRMWARE=1 exec bash "$(cd "$(dirname "$0")" && pwd)/deploy-all.sh"
