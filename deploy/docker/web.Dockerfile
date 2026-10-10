# syntax=docker/dockerfile:1
# Caddy with the built frontend inside (spec 13.2: the UI does not depend on the api image).
#   docker buildx build --platform linux/arm64 -f deploy/docker/web.Dockerfile -t village-swarm-web .

FROM --platform=$BUILDPLATFORM node:24-alpine AS build
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build

FROM caddy:2-alpine
COPY deploy/caddy/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /src/frontend/dist /srv/web
