#!/usr/bin/env bash
# A throw-away dev copy of the hive for looking at UI changes from other machines of the LAN:
#   deploy/dev-lan.sh up | down | status
# Own broker (127.0.0.1:51883) and database (127.0.0.1:55432) in a separate compose project, so a real hive running on
# the same machine is not touched. The api (127.0.0.1:5080) and the simulator stay local; only the Vite UI listens on the
# LAN (port 5173) and proxies /api and /hubs to the api. Login: admin / hive-dev-admin. Never expose it beyond a trusted LAN.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
run="$root/.dev-lan"
export DEV_MQTT_BIND=127.0.0.1:51883 DEV_PG_BIND=127.0.0.1:55432
compose=(docker compose -p village-swarm-devlan -f "$root/deploy/compose.dev.yaml")

stop_pid() { [ -f "$run/$1.pid" ] && kill "$(cat "$run/$1.pid")" 2>/dev/null || true; rm -f "$run/$1.pid"; }

case "${1:-up}" in
  up)
    mkdir -p "$run"
    "${compose[@]}" up -d --wait
    export ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5080
    export ConnectionStrings__Hive="Host=127.0.0.1;Port=55432;Database=hive;Username=hive;Password=hive" Mqtt__Host=127.0.0.1 Mqtt__Port=51883
    ( cd "$root" && nohup dotnet run --project backend/src/Hive.Api >"$run/api.log" 2>&1 & echo $! >"$run/api.pid" )
    for _ in $(seq 1 90); do curl -fs http://127.0.0.1:5080/readyz >/dev/null && break; sleep 1; done
    curl -fs http://127.0.0.1:5080/readyz >/dev/null || { echo "api did not start, see $run/api.log" >&2; exit 1; }
    ( cd "$root" && nohup dotnet run --project backend/tools/Hive.Simulator -- backend/tools/Hive.Simulator/scenarios/default.yaml --host 127.0.0.1 --port 51883 >"$run/sim.log" 2>&1 & echo $! >"$run/sim.pid" )
    ( cd "$root/frontend" && nohup npx vite --host 0.0.0.0 --port 5173 --strictPort >"$run/vite.log" 2>&1 & echo $! >"$run/vite.pid" )
    sleep 3
    echo "UI: http://$(hostname -I | awk '{print $1}'):5173   (admin / hive-dev-admin)"
    ;;
  down)
    stop_pid vite; stop_pid sim; stop_pid api
    pkill -f "village-swarm/.*Hive.Api" 2>/dev/null || true
    "${compose[@]}" down
    ;;
  status)
    "${compose[@]}" ps; for p in api sim vite; do [ -f "$run/$p.pid" ] && echo "$p: pid $(cat "$run/$p.pid")"; done
    ;;
esac
