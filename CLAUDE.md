# Village Swarm — правила для ассистента
- Спецификация: docs/architecture.md. Контракт MQTT: contracts/. Контракт — источник правды:
  меняешь формат сообщения → сначала contracts/, потом прошивка, backend, симулятор, тесты.
- Язык UI — русский; код, комментарии, коммиты — английский.
- Backend: .NET 10, модульный монолит, EF Core + Npgsql, MQTTnet 5. Никакой логики в контроллерах —
  только в модулях. Каждая новая фича — с тестом (unit или Testcontainers).
- Frontend: React + TS + Vite + Tailwind + shadcn/ui + TanStack Query. API-клиент генерируется
  из OpenAPI (npm run gen:api), руками не писать.
- Firmware: PlatformIO, Arduino-ESP32 3.x, lib/hornet-core. Не использовать delay() в loop
  дольше 50 мс. Секреты не хардкодить. Логику безопасности (failsafe) покрывать native-тестами.
- Целевая платформа сервера — linux/arm64 (RPi 4, 4 ГБ). Следить за памятью.
- Перед завершением задачи: `make check` (build + tests + lint всех частей).
- Не менять публичный контракт MQTT/REST без явной просьбы.
