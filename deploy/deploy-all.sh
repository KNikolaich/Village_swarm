#!/usr/bin/env bash
# Linux port of deploy_all.bat: hive1 at home (kirill@192.168.10.4) and then the city (kirill@192.168.1.3) in one go.
# Home first: the mini PC that builds is the home hive, so its copy takes seconds and the new version runs there
# while the bundle crawls to the city. The firmwares are built for home; the city finds them up to date
# (fw_stale.py) and reuses them, even when WITH_FIRMWARE=1 forced the first build. Both are amd64: the city's images
# come from the build cache.
#
#   deploy/deploy-all.sh                   only the hornet firmwares that changed
#   WITH_FIRMWARE=1 deploy/deploy-all.sh   every hornet firmware (deploy-all-firmware.sh does this)
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"

bash "$here/deploy.sh" kirill@192.168.10.4 || { echo "Дом: остановилось с ошибкой, город не трогаю."; exit 1; }
echo "Дом обновлён. Теперь город: копирование туда долгое, прогресс ниже."
WITH_FIRMWARE= bash "$here/deploy.sh" kirill@192.168.1.3 || { echo "Город: остановилось с ошибкой, см. выше. Дом уже обновлён."; exit 1; }
echo "Дом и город обновлены."
