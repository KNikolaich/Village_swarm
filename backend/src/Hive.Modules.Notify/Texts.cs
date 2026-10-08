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
        if (e.Type == "failsafe" && e.Payload.RootElement.TryGetProperty("rule", out var rule))
            text += $"\nправило: {H(rule.GetString())}";
        if (repeat > 1)
            text += $"\n<b>×{repeat}</b> подряд";
        return text;
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
        """;

    public const string NotLinked = "Этот чат не привязан к Улью. Получите код в веб-интерфейсе (Настройки → Telegram) и отправьте /link КОД.";
}
