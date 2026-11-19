#!/usr/bin/env bash
# Linux port of deploy_all.bat: the city (kirill@192.168.1.3) and then hive1 at home (kirill@192.168.10.4) in one go.
# The firmwares are built for the city; home finds them up to date (fw_stale.py) and reuses them, even when
# WITH_FIRMWARE=1 forced the city's build.
#
#   deploy/deploy-all.sh                   only the hornet firmwares that changed
#   WITH_FIRMWARE=1 deploy/deploy-all.sh   every hornet firmware (deploy-all-firmware.sh does this)
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"

bash "$here/deploy.sh" kirill@192.168.1.3 || { echo "Город: остановилось с ошибкой, дом не трогаю."; exit 1; }
WITH_FIRMWARE= bash "$here/deploy.sh" kirill@192.168.10.4 || { echo "Дом: остановилось с ошибкой, см. выше."; exit 1; }
echo "Город и дом обновлены."
