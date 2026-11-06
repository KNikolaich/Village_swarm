# Бэкапы и восстановление

## Что делается само

Контейнер `backup` в стеке Улья:

- **Каждую ночь в 03:00** делает дамп базы (`pg_dump -Fc`) в `/srv/hive/backups/db/`. Последние 7 дампов остаются.
- **Если задан `RESTIC_REPOSITORY`**, после дампа копирует в зашифрованный репозиторий restic: дампы, фото (`media`), ключи входа (`keys`), учётки шершней (`mosquitto`), корневой сертификат Улья (`caddy`) и `.env`. Хранятся 7 суточных, 5 недельных и 12 месячных снимков.
- **Раз в неделю** (по воскресеньям после ночного бэкапа) проверяет восстановление: разворачивает последний дамп во временную базу, считает записи и удаляет её; с репозиторием ещё читает 5% его данных (`restic check`).
- Каждый запуск пишется в журнал (таблица `backup_runs`). В интерфейсе: **Настройки → Бэкапы** (только администратору). Если свежего дампа нет больше 36 часов или что-то не удалось, Улей пишет в Telegram.

Пока `RESTIC_REPOSITORY` пуст, копия лежит **на том же диске, что и сам Улей**: она спасёт от ошибки в базе и от неудачного обновления, но не от смерти диска или кражи RPi. Настройте копию вне Улья.

## Настроить копию вне Улья

В `/opt/hive/.env`:

| Куда | `RESTIC_REPOSITORY` | Ещё |
|---|---|---|
| Другой диск на RPi (USB) | `/repo` | `BACKUP_REPO_DIR=/mnt/backup/hive` (папка на хосте) |
| Городской узел (rest-server, шаг 16) | `rest:https://user:password@city.example:8000/hive` | |
| S3 (Backblaze B2, Yandex Object Storage) | `s3:https://s3.example.com/bucket/hive` | `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` |

И обязательно `RESTIC_PASSWORD=<длинный пароль>`. **Сохраните этот пароль вне Улья** (менеджер паролей, бумага): без него копию не прочитать, а лежит он на RPi, который как раз и может пропасть.

После правки `.env`: `cd /opt/hive && sudo docker compose up -d backup`. Проверить сразу, не дожидаясь ночи:

```bash
sudo docker compose exec backup backup.sh
sudo docker compose exec backup verify.sh
```

В Настройках → Бэкапы должны появиться строки «Дамп базы», «Копия вне Улья» и «Проверка восстановления» со статусом «удалось».

## Восстановление

### Только база (ошибка, неудачное обновление)

```bash
sudo /opt/hive/restore.sh                      # самый свежий ночной дамп
sudo /opt/hive/restore.sh /srv/hive/backups/db/hive-20261014-030000.dump
```

Скрипт спросит подтверждение, остановит api, заменит базу дампом и запустит api.

### Новый RPi вместо потерянного

1. Прошить Raspberry Pi OS Lite 64-bit, поставить Docker, настроить сеть (см. `docs/deploy.md`).
2. Поставить Улей обычным способом (`deploy\deploy.bat` с ПК): появится пустой Улей с новыми паролями.
3. Достать копию из репозитория. В `/opt/hive/.env` нового Улья пропишите те же `RESTIC_REPOSITORY` и `RESTIC_PASSWORD`, что были на старом (и `BACKUP_REPO_DIR`, если копия на диске). Затем:

   ```bash
   cd /opt/hive && sudo docker compose down
   sudo mkdir -p /srv/hive/restore
   sudo docker compose run --rm --no-deps -v /srv/hive/restore:/restore backup restic restore latest --target /restore
   ```

   Снимок разложит пути как были: `/srv/hive/restore/source/media`, `.../source/keys`, `.../source/mosquitto`, `.../source/caddy`, `.../source/config/.env` и дампы в `.../backups/db`.
4. Разложить восстановленное по местам в `/srv/hive`: `restore/source/media` в `media`, `keys`, `mosquitto`, `caddy` (с корневым сертификатом: установленный на устройствах сертификат продолжит работать) и дампы из `restore/backups/db` в `backups/db`. Из `restore/source/config/.env` возьмите пароли (`PG_PASSWORD`, `MQTT_ADMIN_PASSWORD`) и перенесите в новый `/opt/hive/.env`: база и брокер ждут именно их. Учётки шершней лежат в `mosquitto/dynamic-security.json`: пока он на месте, платы переподключатся сами.
5. Права: `sudo chown -R 1654:1654 /srv/hive/media /srv/hive/keys && sudo chown -R 1883:1883 /srv/hive/mosquitto`.
6. Запустить стек и восстановить базу: `sudo docker compose up -d`, затем `sudo /opt/hive/restore.sh`. Когда всё работает, `/srv/hive/restore` можно удалить.

### Проверка, что восстановление действительно работает

Раз в квартал полезно сделать учебное восстановление на другой машине (ВМ в Proxmox или обычный ПК с Docker). Недельная проверка `verify.sh` доказывает, что дамп открывается и в нём есть данные, но не заменяет полной репетиции с `.env`, сертификатом и шершнями.

## Для разработчика

`make backup-test` (или `bash deploy/backup/test.sh`) собирает образ и в Docker проверяет всё по-настоящему: дамп, ротацию, копию в локальный restic, проверку восстановления и сбои (нет пароля, повреждённый дамп).
