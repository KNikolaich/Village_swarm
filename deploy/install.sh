#!/usr/bin/env bash
# Installs or re-configures the hive on a Raspberry Pi (spec 13.4). Idempotent: run it again after moving
# the RPi to another network (refreshes the addresses) or to change options; passwords in .env are kept.
#
#   sudo ./install.sh [--host ADDR]... [--rtc] [--w1-pin N] [--no-sensors] [--yes]
#
# --host adds an address the UI will be opened by later, e.g. the dacha IP while preparing the RPi in the city.
#
# Run it from the folder ship.sh copied to the RPi (or from /opt/hive). Images come from images/*.tar.gz
# next to this script when present, otherwise from the registry in .env (HIVE_IMAGES).
set -euo pipefail

HIVE_DIR=/opt/hive
RTC=0
W1_PIN=4
SENSORS=1
ASSUME_YES=0
EXTRA_HOSTS=()
while [ $# -gt 0 ]; do
	case "$1" in
		--host) EXTRA_HOSTS+=("$2"); shift ;;
		--rtc) RTC=1 ;;
		--w1-pin) W1_PIN="$2"; shift ;;
		--no-sensors) SENSORS=0 ;;
		--yes|-y) ASSUME_YES=1 ;;
		-h|--help) sed -n '2,12p' "$0"; exit 0 ;;
		*) echo "unknown option: $1" >&2; exit 2 ;;
	esac
	shift
done

say() { printf '\033[1;33m==>\033[0m %s\n' "$*"; }
die() { printf '\033[1;31mОшибка:\033[0m %s\n' "$*" >&2; exit 1; }
REBOOT=0

[ "$(id -u)" -eq 0 ] || die "запустите через sudo"
SRC="$(cd "$(dirname "$0")" && pwd)"

# 1. Checks: 64-bit OS, free space, where the root lives.
arch="$(dpkg --print-architecture)"
[ "$arch" = arm64 ] || [ "$arch" = amd64 ] || die "нужна 64-битная система (сейчас $arch). Поставьте Raspberry Pi OS Lite 64-bit."
. /etc/os-release
case "${VERSION_CODENAME:-}" in bookworm|trixie) ;; *) say "Система ${PRETTY_NAME:-?} не проверялась, продолжаю" ;; esac
free_gb=$(df -BG --output=avail / | tail -1 | tr -dc 0-9)
[ "$free_gb" -ge 4 ] || die "на диске свободно ${free_gb} ГБ, нужно хотя бы 4"
root_dev="$(findmnt -no SOURCE /)"
case "$root_dev" in /dev/mmcblk*) say "Система на SD-карте: PostgreSQL её быстро износит, лучше SSD/NVMe по USB (spec 3.1)" ;; esac
mem_mb=$(awk '/MemTotal/ {print int($2/1024)}' /proc/meminfo)
say "Память ${mem_mb} МБ, свободно на диске ${free_gb} ГБ, корень на ${root_dev}"

# 2. Packages: Docker from its official repository (Debian's has no compose v2), chrony for time, zram swap.
if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1; then
	say "Ставлю Docker"
	apt-get update -qq
	apt-get install -y -qq ca-certificates curl gnupg
	install -m 0755 -d /etc/apt/keyrings
	curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
	chmod a+r /etc/apt/keyrings/docker.asc
	echo "deb [arch=$arch signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian ${VERSION_CODENAME} stable" \
		>/etc/apt/sources.list.d/docker.list
	apt-get update -qq
	apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-compose-plugin
fi
apt-get install -y -qq chrony zram-tools i2c-tools curl openssl >/dev/null

# Swap in compressed RAM instead of on the disk (spec 13.2).
if [ -f /etc/default/zramswap ] && ! grep -q '^PERCENT=50' /etc/default/zramswap; then
	printf 'ALGO=zstd\nPERCENT=50\nPRIORITY=100\n' >/etc/default/zramswap
	systemctl restart zramswap || true
fi
if systemctl is-enabled dphys-swapfile >/dev/null 2>&1; then
	dphys-swapfile swapoff || true
	systemctl disable --now dphys-swapfile || true
fi

# 3. Boot config (Raspberry Pi only): memory cgroup for container limits, 1-Wire thermometer, I2C, RTC.
BOOT=/boot/firmware
[ -d "$BOOT" ] || BOOT=/boot
if [ -f "$BOOT/config.txt" ]; then
	cmdline="$BOOT/cmdline.txt"
	if ! grep -q 'cgroup_enable=memory' "$cmdline"; then
		sed -i '1 s/$/ cgroup_enable=memory cgroup_memory=1/' "$cmdline"
		REBOOT=1
	fi
	add_line() { grep -qxF "$1" "$BOOT/config.txt" || { echo "$1" >>"$BOOT/config.txt"; REBOOT=1; }; }
	if [ "$SENSORS" -eq 1 ] && ! grep -q '^dtoverlay=w1-gpio' "$BOOT/config.txt"; then
		add_line "dtoverlay=w1-gpio,gpiopin=${W1_PIN}"
	fi
	add_line "dtparam=i2c_arm=on"
	if [ "$RTC" -eq 1 ]; then
		add_line "dtoverlay=i2c-rtc,ds3231"
		# The RTC keeps the time; the fake-hwclock package would overwrite it on boot.
		apt-get remove -y -qq fake-hwclock >/dev/null 2>&1 || true
	fi
fi

# 4. Files: compose and configs to /opt/hive, data to /srv/hive.
if [ "$SRC" != "$HIVE_DIR" ]; then
	say "Копирую файлы в $HIVE_DIR"
	mkdir -p "$HIVE_DIR"
	cp -r "$SRC/compose.yaml" "$SRC/compose.home.yaml" "$SRC/.env.example" "$SRC/mosquitto" \
		"$SRC/install.sh" "$SRC/update.sh" "$HIVE_DIR/"
fi
chmod +x "$HIVE_DIR/install.sh" "$HIVE_DIR/update.sh"
cd "$HIVE_DIR"

ENV=.env
first_install=0
if [ ! -f "$ENV" ]; then
	first_install=1
	cp .env.example "$ENV"
	chmod 600 "$ENV"
fi
set_env() { # key value: replace or append, value written literally
	local key="$1" value="$2" tmp
	tmp="$(mktemp)"
	awk -v k="$key" -v v="$value" 'BEGIN{done=0} $0 ~ "^"k"=" {print k"="v; done=1; next} {print} END{if(!done) print k"="v}' "$ENV" >"$tmp"
	cat "$tmp" >"$ENV"
	rm -f "$tmp"
}
get_env() { grep -E "^$1=" "$ENV" | tail -1 | cut -d= -f2-; }
secret() { openssl rand -hex 16; }

for key in PG_PASSWORD MQTT_ADMIN_PASSWORD; do
	case "$(get_env $key)" in ""|change-me) set_env "$key" "$(secret)" ;; esac
done
admin_password=""
if [ "$first_install" -eq 1 ] && [ -z "$(get_env AUTH__BOOTSTRAPADMINPASSWORD)" ]; then
	admin_password="$(openssl rand -base64 12 | tr -d '/+=' | cut -c1-12)"
	set_env AUTH__BOOTSTRAPADMINPASSWORD "$admin_password"
fi

# Addresses the UI is opened by: every IPv4 of the RPi (not Docker's bridges), its mDNS name and localhost.
ips="$(hostname -I | tr ' ' '\n' | grep -E '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' | grep -Ev '^172\.(1[7-9]|2[0-9]|3[01])\.' || true)"
if [ ${#EXTRA_HOSTS[@]} -gt 0 ]; then
	set_env HIVE_EXTRA_HOSTS "${EXTRA_HOSTS[*]}"
fi
extra="$(get_env HIVE_EXTRA_HOSTS)"
hosts="$(printf '%s\n' $ips $extra "$(hostname).local" localhost | awk 'NF && !seen[$0]++' | paste -sd, - | sed 's/,/, /g')"
set_env HIVE_HOSTS "$hosts"
set_env HIVE_DEFAULT_SNI "$(printf '%s\n' $ips | head -1)"

if [ "$first_install" -eq 1 ] && [ "$ASSUME_YES" -eq 0 ] && [ -t 0 ] && [ -z "$(get_env TELEGRAM__TOKEN)" ]; then
	read -r -p "Токен Telegram-бота (Enter — пропустить, можно позже в $HIVE_DIR/.env): " token
	[ -n "$token" ] && set_env TELEGRAM__TOKEN "$token"
fi

# Images shipped from the PC carry their version (build-images.sh).
[ -f "$SRC/images/VERSION" ] && set_env HIVE_VERSION "$(cat "$SRC/images/VERSION")"

data="$(get_env HIVE_DATA)"
data="${data:-/srv/hive}"
mkdir -p "$data"/{pg,media,mosquitto,caddy,backups}
chown -R 1654:1654 "$data/media"     # "app" user of the .NET image
chown -R 1883:1883 "$data/mosquitto" # "mosquitto" user of the broker image

# 5-6. Images and start.
shopt -s nullglob
images=("$SRC"/images/*.tar "$SRC"/images/*.tar.gz)
if [ ${#images[@]} -gt 0 ]; then
	for f in "${images[@]}"; do
		say "Загружаю образы из $f"
		if [[ "$f" == *.gz ]]; then gunzip -c "$f" | docker load; else docker load -i "$f"; fi
	done
else
	say "Скачиваю образы"
	docker compose pull
fi
docker compose up -d --remove-orphans

say "Жду, пока Улей ответит"
ok=0
for _ in $(seq 1 60); do
	if curl -fsk https://localhost/readyz >/dev/null 2>&1; then ok=1; break; fi
	sleep 3
done
docker compose ps

ip1="$(printf '%s\n' $ips | head -1)"
echo
if [ "$ok" -eq 1 ]; then
	say "Улей работает: https://${ip1:-$(hostname).local}/"
	# The admin exists now; the bootstrap password is not needed in plain text any more.
	set_env AUTH__BOOTSTRAPADMINPASSWORD ""
else
	say "Улей ещё не ответил на /readyz. Логи: cd $HIVE_DIR && docker compose logs api"
fi
echo "   Шершням указывать адрес: http://${ip1}"
echo "   Сертификат для браузеров (убирает предупреждение): http://${ip1}/ca.crt"
if [ -n "$admin_password" ]; then
	echo "   Вход: admin / $admin_password  (смените пароль в Настройках)"
fi
if [ "$REBOOT" -eq 1 ]; then
	say "Изменены настройки загрузки (лимиты памяти, 1-Wire, I2C, RTC): перезагрузите RPi: sudo reboot"
fi
