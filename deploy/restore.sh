#!/usr/bin/env bash
# Puts a database dump back (docs/restore.md): stops the api, replaces the "hive" database with the dump, starts the api.
#
#   sudo /opt/hive/restore.sh                 the newest nightly dump from <data>/backups/db
#   sudo /opt/hive/restore.sh /path/to/file   a specific hive-*.dump (pg_dump -Fc) file
#
# Photos, keys and the rest come back from the off-site repository (see docs/restore.md); this script is the part
# every restore needs. It asks before it touches anything.
set -euo pipefail

say() { printf '\033[1;33m==>\033[0m %s\n' "$*"; }
die() { printf '\033[1;31mОшибка:\033[0m %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "запустите через sudo"
cd /opt/hive
data="$(grep -E '^HIVE_DATA=' .env | tail -1 | cut -d= -f2-)"
data="${data:-/srv/hive}"

dump="${1:-$(ls -1t "$data"/backups/db/hive-*.dump 2>/dev/null | head -1)}"
[ -n "$dump" ] && [ -f "$dump" ] || die "нет файла дампа (искал в $data/backups/db)"

printf 'База будет заменена содержимым %s (%s).\n' "$dump" "$(du -h "$dump" | cut -f1)"
read -r -p 'Продолжить? [yes/N] ' answer
[ "$answer" = "yes" ] || die "отменено"

say "Останавливаю api"
docker compose stop api
say "Создаю чистую базу"
docker compose exec -T postgres psql -U hive -d postgres -c 'DROP DATABASE IF EXISTS hive WITH (FORCE)' -c 'CREATE DATABASE hive OWNER hive'
say "Загружаю дамп"
docker compose exec -T postgres pg_restore -U hive -d hive --no-owner --exit-on-error <"$dump"
say "Запускаю api"
docker compose start api
say "Готово. Проверьте https://localhost/readyz и интерфейс."
