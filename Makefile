# Village Swarm. `make check` = build + tests + lint of every part (CLAUDE.md).
# Requires: .NET 10 SDK, Node.js 22+, PlatformIO Core (pio), Docker for `make dev-up`.

COMPOSE_DEV = docker compose -f deploy/compose.dev.yaml

.PHONY: check backend frontend firmware firmware-test dev-up dev-down images ship

check: backend frontend firmware

backend:
	dotnet build backend/Hive.slnx -c Release
	dotnet test backend/Hive.slnx -c Release --no-build

frontend:
	cd frontend && npm ci && npm run lint && npm run build

firmware: firmware-test
	cd firmware && pio run -e guard-cam

firmware-test:
	cd firmware && pio test -e native

dev-up:
	$(COMPOSE_DEV) up -d --wait

dev-down:
	$(COMPOSE_DEV) down

# Hive images for the RPi, packed into deploy/dist (docs/deploy.md). `make ship PI=kirill@192.168.10.4 ARGS="--host 192.168.10.4"`.
images:
	bash deploy/build-images.sh linux/arm64

ship:
	bash deploy/ship.sh $(PI) $(ARGS)
