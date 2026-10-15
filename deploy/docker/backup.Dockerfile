# syntax=docker/dockerfile:1
# The backup container (spec 10.1): pg_dump, restic and a sleeping loop. Small on purpose (the RPi has 2 GB).
#   docker buildx build --platform linux/arm64 -f deploy/docker/backup.Dockerfile -t village-swarm-backup .
# bash: the scripts use arrays; coreutils: GNU date -d "tomorrow 03:00"; tzdata: nights are in house time.
FROM alpine:3.21
RUN apk add --no-cache bash coreutils tzdata restic postgresql17-client
COPY deploy/backup/lib.sh deploy/backup/backup.sh deploy/backup/verify.sh deploy/backup/loop.sh /usr/local/bin/
RUN chmod +x /usr/local/bin/*.sh && sed -i 's/\r$//' /usr/local/bin/*.sh
CMD ["/usr/local/bin/loop.sh"]
