using System.Globalization;
using System.Net;
using Hive.Domain;

namespace Hive.Modules.Notify;

/// <summary>Russian message texts for Telegram (HTML parse mode).</summary>
public static class Texts
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string EventType(string type) => type switch
    {
        "motion" => "Движение",
        "door_open" => "Дверь открыта",
        "leak" => "Протечка",
        "failsafe" => "Защита обогрева",
        "photo_failed" => "Фото не загрузилось",
        "tamper" => "Камера закрыта или сдвинута",
        "snapshot" => "Снимок",
        "ota" => "Обновление прошивки",
        "device_offline" => "Шершень не на связи",
        "device_online" => "Шершень снова на связи",
        "backup" => "Проблема с бэкапом",
        "disk_cleanup" => "Диск заполнен: удалены старые фото",
        "backup_cleared" => "Бэкапы снова в порядке",
        "rule" => "Правило",
        "overheat" => "Улей перегревается",
        "overheat_cleared" => "Температура улья в норме",
        "disk_full" => "Диск улья почти заполнен",
        "disk_full_cleared" => "На диске улья снова есть место",
        _ => type,
    };

    public static string Icon(EventSeverity severity) => severity switch
    {
        EventSeverity.Critical => "🆘",
        EventSeverity.Alarm => "🚨",
        EventSeverity.Warn => "⚠️",
        _ => "ℹ️",
    };

    public static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>"🚨 <b>Движение</b>: Ворота · 23:14:25", the place being the zone or the device name.</summary>
    public static string Event(DeviceEvent e, string place, DateTimeOffset localTs, int repeat = 1)
    {
        var text = $"{Icon(e.Severity)} <b>{H(EventType(e.Type))}</b>: {H(place)}\n{H(e.DeviceId)} · {localTs.ToString("HH:mm:ss", Ru)}";
        if (e.Type == "ota")
            text += OtaDetail(e.Payload.RootElement);
        if (e.Type == "device_offline" && e.Payload.RootElement.TryGetProperty("minutes", out var gone))
            text += $"\nне отвечает уже {gone.GetInt32()} мин";
        if (e.Type == "disk_cleanup")
            text += CleanupDetail(e.Payload.RootElement);
        if (e.Type == "backup")
            text += BackupDetail(e.Payload.RootElement);
        if (e.Type == "rule" && e.Payload.RootElement.TryGetProperty("text", out var said))
            text += $"\n{H(said.GetString())}";
        if (e.Type == "failsafe" && e.Payload.RootElement.TryGetProperty("rule", out var rule))
            text += $"\nправило: {H(rule.GetString())}";
        if (e.Type.StartsWith("overheat", StringComparison.Ordinal) || e.Type.StartsWith("disk_full", StringComparison.Ordinal))
            text += HiveReading(e.Payload.RootElement);
        if (repeat > 1)
            text += $"\n<b>×{repeat}</b> подряд";
        return text;
    }

    /// <summary>"\nудалено фото: 120 (диск был занят на 88%)" after an emergency cleanup (Hive.Modules.Retention).</summary>
    private static string CleanupDetail(System.Text.Json.JsonElement p)
    {
        var photos = p.TryGetProperty("photos", out var n) ? n.GetInt32() : 0;
        var used = p.TryGetProperty("usedPct", out var u) ? $" (диск был занят на {u.GetDouble().ToString("0.#", Ru)}%)" : "";
        var oldAlarms = p.TryGetProperty("level", out var l) && l.GetInt32() >= 2 ? "\nПришлось удалить и часть старых фото тревог: проверьте диск." : "";
        return $"\nудалено фото: {photos}{used}{oldAlarms}";
    }

    /// <summary>One line per problem the backup watch found (Hive.Modules.Backup), e.g. "нет свежего дампа базы".</summary>
    private static string BackupDetail(System.Text.Json.JsonElement p)
    {
        if (!p.TryGetProperty("problems", out var list) || list.ValueKind != System.Text.Json.JsonValueKind.Array)
            return "";
        var lines = list.EnumerateArray().Select(x => x.GetString() switch
        {
            "dump_failed" => "дамп базы не удался",
            "dump_stale" => "нет свежего дампа базы",
            "offsite_failed" => "копия вне дома не удалась",
            "verify_failed" => "проверка восстановления не прошла",
            var other => other ?? "",
        });
        return string.Concat(lines.Select(l => $"\n{H(l)}"));
    }

    /// <summary>"\nвоздух у платы: 47,5° (порог 45°)" for the hive's own sensor events.</summary>
    private static string HiveReading(System.Text.Json.JsonElement p)
    {
        if (!p.TryGetProperty("metric", out var m) || !p.TryGetProperty("value", out var v) || !p.TryGetProperty("limit", out var l))
            return "";
        var (label, unit) = m.GetString() switch
        {
            "temperature" => ("воздух у платы", "°"),
            "cpu_temperature" => ("процессор", "°"),
            "disk_used_pct" => ("занято", "%"),
            var other => (other ?? "", ""),
        };
        return $"\n{H(label)}: {v.GetDouble().ToString("0.#", Ru)}{unit} (порог {l.GetDouble().ToString("0.#", Ru)}{unit})";
    }

    /// <summary>One line on what an update did, e.g. "0.2.0+abc: установлена и работает" (contracts event-ota).</summary>
    private static string OtaDetail(System.Text.Json.JsonElement p)
    {
        var phase = p.TryGetProperty("phase", out var ph) ? ph.GetString() : null;
        var version = p.TryGetProperty("version", out var v) ? v.GetString() : "";
        var error = p.TryGetProperty("error", out var er) ? er.GetString() : null;
        var what = phase switch
        {
            "started" => "началась",
            "applied" => "установлена и работает",
            "failed" => "не удалась",
            "rolled_back" => "не вышла на связь, возвращена прежняя версия",
            _ => phase ?? "",
        };
        return $"\n{H(version)}: {what}" + (string.IsNullOrEmpty(error) ? "" : $" ({H(error)})");
    }

    public static string Temperature(double value) => $"{value.ToString("0.#", Ru)}°";

    public const string Help = """
        <b>Улей</b> — команды:
        /status — охрана, рой, температура, тревоги
        /arm — включить охрану
        /disarm — снять охрану (с подтверждением)
        /photo [шершень] — снимок сейчас
        /events [N] — последние события
        /temp — температура
        /devices — шершни не на связи
        /mute [часы] — тишина для предупреждений
        /viewed [шершень] — отметить события просмотренными
        """;

    public const string NotLinked = "Этот чат не привязан к Улью. Получите код в веб-интерфейсе (Настройки → Telegram) и отправьте /link КОД.";
}
