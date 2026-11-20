#!/usr/bin/env bash
# One command (Linux port of deploy.bat): build the hornet firmwares that changed, build the hive images for the
# target's processor and deliver them (docs/deploy.md).
#
#   deploy/deploy.sh [user@host] [extra install.sh options, e.g. --rtc]     default: kirill@192.168.10.4 (hive1, home)
#
#   WITH_FIRMWARE=1   rebuild every hornet firmware, not only the changed ones
#   HIVE_PLATFORM     linux/arm64 or linux/amd64; default: asked from the target (RPi arm64, mini PC / VM amd64)
#
# Needs: Docker usable without sudo (the docker group), ssh key access to the target, PlatformIO (pio) for firmware.
set -uo pipefail

TARGET="${1:-kirill@192.168.10.4}"
shift || true
HOST="${TARGET#*@}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="$HOME/.local/bin:$PATH" # pipx puts pio here

say() { printf '\n\033[1;33m=== %s ===\033[0m\n' "$*"; }
die() { printf '\033[1;31mОшибка:\033[0m %s\n' "$*" >&2; exit 1; }

docker info >/dev/null 2>&1 \
	|| die "Docker недоступен без sudo. Пользователь должен быть в группе docker (sudo usermod -aG docker $USER), после этого перезайдите в сеанс."
ssh -o BatchMode=yes -o ConnectTimeout=10 "$TARGET" true 2>/dev/null \
	|| die "нет входа по ssh-ключу на $TARGET. Проверьте адрес и выполните ssh-copy-id $TARGET."

platform="${HIVE_PLATFORM:-}"
if [ -z "$platform" ]; then
	arch="$(ssh -o BatchMode=yes "$TARGET" dpkg --print-architecture)" || die "не удалось узнать процессор $TARGET"
	platform="linux/$arch"
fi
case "$platform" in linux/arm64|linux/amd64) ;; *) die "неподдерживаемая платформа: $platform" ;; esac

# Hornet firmware first: firmware/dist is baked into the api image, and the hive offers it in the "add hornet" wizard
# and for over-the-air updates. Only the firmwares whose inputs changed since their last build are rebuilt
# (firmware/scripts/fw_stale.py), so the other hornets keep their version and get no "new firmware" mark.
if ! command -v pio >/dev/null 2>&1; then
	echo "PlatformIO не найден: на Улье останутся прежние прошивки из firmware/dist."
else
	key="${HORNET_OTA_KEY:-$HOME/.village-swarm/ota-signing.key}"
	[ -f "$key" ] || echo "ВНИМАНИЕ: нет ключа подписи $key: прошивки соберутся без подписи, шершни не примут их по воздуху."
	all=()
	[ "${WITH_FIRMWARE:-}" = 1 ] && all=(--all)
	if ! envs="$(python3 "$REPO/firmware/scripts/fw_stale.py" list "${all[@]}")"; then
		echo "Не удалось определить, какие прошивки изменились (fw_stale.py): собираю все."
		envs="$(sed -n 's/^\[env:\([^]]*\)\]$/\1/p' "$REPO/firmware/platformio.ini" | grep -vx native)"
	fi
	say "Прошивки шершней"
	built=0
	failed=0
	for env in $envs; do
		echo "--- $env"
		built=$((built + 1))
		if pio run -s -d "$REPO/firmware" -e "$env"; then
			python3 "$REPO/firmware/scripts/fw_stale.py" stamp "$env"
		else
			echo "$env не собралась, на Улье останется прежняя."
			failed=$((failed + 1))
		fi
	done
	[ "$built" -eq 0 ] && echo "Все прошивки уже собраны для текущих исходников, пересборка не нужна (WITH_FIRMWARE=1 пересоберёт всё)."
	[ "$failed" -eq 0 ] || echo "Не собралось прошивок: $failed. Ошибки выше."
fi

say "Образы backend и frontend ($platform), затем доставка на $TARGET"
cd "$REPO" && bash deploy/build-images.sh "$platform" && bash deploy/ship.sh "$TARGET" --host "$HOST" "$@"
code=$?
echo
if [ "$code" -eq 0 ]; then echo "Готово: $TARGET."; else echo "Остановилось с ошибкой, код $code."; fi
exit "$code"
