#!/usr/bin/env bash
# Updates the hive to another version (spec 13.5): database dump first, then the new images, then a health
# check; if /readyz does not answer within 120 s the previous version is started again.
#
#   sudo /opt/hive/update.sh <version> [images.tar.gz]
#
# Without a tar file the images are pulled from HIVE_IMAGES. ship.sh from the PC calls this for you.
set -euo pipefail

say() { printf '\033[1;33m==>\033[0m %s\n' "$*"; }
die() { printf '\033[1;31mОшибка:\033[0m %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "запустите через sudo"
[ $# -ge 1 ] || { sed -n '2,7p' "$0"; exit 2; }
version="$1"
tarball="${2:-}"
cd /opt/hive

get_env() { grep -E "^$1=" .env | tail -1 | cut -d= -f2-; }
set_env() { sed -i "s|^$1=.*|$1=$2|" .env; }

previous="$(get_env HIVE_VERSION)"
data="$(get_env HIVE_DATA)"
data="${data:-/srv/hive}"

if [ -n "$tarball" ]; then
	say "Загружаю образы из $tarball"
	if [[ "$tarball" == *.gz ]]; then gunzip -c "$tarball" | docker load; else docker load -i "$tarball"; fi
fi

dump="$data/backups/pre-update-${previous}-$(date +%Y%m%d-%H%M%S).sql.gz"
say "Дамп базы перед обновлением: $dump"
docker compose exec -T postgres pg_dump -U hive -d hive | gzip >"$dump"
# Keep the last five pre-update dumps.
ls -1t "$data"/backups/pre-update-*.sql.gz 2>/dev/null | tail -n +6 | xargs -r rm -f

# Folders added by newer versions (the first install creates them; an update must too).
mkdir -p "$data/keys" "$data/restic"
chown 1654:1654 "$data/keys" # "app" user of the .NET image

set_env HIVE_VERSION "$version"
[ -n "$tarball" ] || docker compose pull api web
docker compose up -d --remove-orphans

say "Проверяю $version"
for _ in $(seq 1 40); do
	if curl -fsk https://localhost/readyz >/dev/null 2>&1; then
		say "Готово: $previous → $version"
		docker image prune -f >/dev/null
		# Every build has its own tag, so old versions are never "dangling" and prune leaves them: each costs
		# hundreds of MB on the SD card. Keep this version and the previous one (for a manual rollback).
		prefix="$(get_env HIVE_IMAGES || true)"
		prefix="${prefix:-ghcr.io/knikolaich/village-swarm}"
		for part in api web backup; do
			docker image ls --format '{{.Repository}}:{{.Tag}}' "$prefix-$part" \
				| grep -v -x -F -e "$prefix-$part:$version" -e "$prefix-$part:$previous" \
				| xargs -r docker rmi >/dev/null 2>&1 || true
		done
		say "Место на диске: $(df -h "$data" | awk 'NR==2 {print $3 " из " $2 " (" $5 ")"}')"
		exit 0
	fi
	sleep 3
done

say "Версия $version не ответила, возвращаю $previous"
docker compose logs --tail 50 api || true
set_env HIVE_VERSION "$previous"
docker compose up -d
cat <<EOF
Если новая версия успела изменить базу и старая с ней не работает, восстановите дамп:
  docker compose stop api
  docker compose exec -T postgres psql -U hive -d postgres -c 'DROP DATABASE hive WITH (FORCE)' -c 'CREATE DATABASE hive OWNER hive'
  gunzip -c $dump | docker compose exec -T postgres psql -U hive -d hive
  docker compose start api
EOF
exit 1
