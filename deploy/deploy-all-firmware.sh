#!/usr/bin/env bash
# The city and hive1 at home, with every hornet firmware rebuilt (and re-signed) first, changed or not.
set -uo pipefail
WITH_FIRMWARE=1 exec bash "$(cd "$(dirname "$0")" && pwd)/deploy-all.sh"
