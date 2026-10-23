using System.Globalization;
using System.Text;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Devices;
using Hive.Modules.Notes;
using Hive.Modules.Notify.Telegram;
using Hive.Modules.Telemetry;
using Hive.Modules.Weather;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Hive.Modules.Notify.Texts;

namespace Hive.Modules.Assistant;

/// <summary>
/// Everything the model may answer from, as compact text: guard and quiet mode, the forecast, the zones, every device
/// with its link, latest readings, relays and IR buttons, and the note branches. Built fresh for every request.
/// </summary>
public sealed class AssistantContext(
    HiveDbContext db,
    ArmService arm,
    BotCommands bot,
    TelemetryQueries telemetry,
    WeatherService weather,
    NoteService notes,
    IOptions<HiveOptions> hive,
    TimeProvider time)
{
    private const int MaxDevices = 150;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public async Task<string> BuildAsync(long chat, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var sb = new StringBuilder();
        var mode = await arm.GetAsync(ct);
        var muted = await bot.MutedUntilAsync(chat, ct);
        var unacked = await db.Events.AsNoTracking().CountAsync(e => e.AcknowledgedAt == null && e.Severity >= EventSeverity.Alarm && e.Ts >= now.AddDays(-1), ct);
        sb.AppendLine($"Сейчас: {Local(now):dd.MM.yyyy HH:mm}, {Local(now).ToString("dddd", Ru)}.");
        sb.AppendLine($"Охрана: {mode.Mode switch { ArmService.Arriving => "подготовка к приезду", ArmService.ArmedMode => "включена", _ => "снята" }}.");
        sb.AppendLine($"Тишина в этом чате: {(muted is { } m ? $"до {Local(m):dd.MM HH:mm}" : "выключена")}.");
        sb.AppendLine($"Непросмотренных тревог за сутки: {unacked}.");

        sb.AppendLine();
        AppendWeather(sb, await weather.GetAsync(ct), now);

        var zones = await db.Zones.AsNoTracking().OrderBy(z => z.Sort).ThenBy(z => z.Id).ToListAsync(ct);
        sb.AppendLine();
        sb.AppendLine("Зоны (вид):");
        foreach (var z in zones)
            sb.AppendLine($"- {z.Name} ({KindText(z.Kind)})");

        var devices = await db.Devices.AsNoTracking().Include(d => d.Zone).Where(d => d.Status != DeviceStatus.Disabled)
            .OrderBy(d => d.Zone == null ? int.MaxValue : d.Zone.Sort).ThenBy(d => d.Sort).ThenBy(d => d.Name).Take(MaxDevices).ToListAsync(ct);
        var latest = (await telemetry.LatestAsync(null, ct)).ToLookup(v => v.DeviceId);
        sb.AppendLine();
        sb.AppendLine("Устройства (имя [id] · тип · зона · связь · показания · управление):");
        foreach (var d in devices)
            sb.AppendLine("- " + DeviceLine(d, latest[d.DeviceId], now));

        sb.AppendLine();
        sb.AppendLine("Ветки заметок:");
        var outline = await notes.OutlineAsync(200, ct);
        if (outline.Count == 0)
            sb.AppendLine("(заметок пока нет)");
        foreach (var line in outline)
            sb.AppendLine("- " + line);
        return sb.ToString();
    }

    private string DeviceLine(Device d, IEnumerable<LatestValueDto> values, DateTimeOffset now)
    {
        var parts = new List<string> { $"{d.Name} [{d.DeviceId}]", d.TypeCode };
        parts.Add(d.Zone is { } z ? $"зона {z.Name} ({KindText(z.Kind)})" : "без зоны");
        parts.Add(d.Status == DeviceStatus.Online
            ? "на связи"
            : $"нет связи{(d.LastSeenAt is { } seen ? " с " + Local(seen).ToString("dd.MM HH:mm", Ru) : "")}");
        var readings = values.OrderBy(v => v.Metric).Select(v => TelegramAssistant.Reading(v.Metric, v.Value) + Age(v.Ts, now)).ToList();
        if (readings.Count > 0)
            parts.Add(string.Join(", ", readings));
        var relays = RelayChannels(d);
        if (relays.Length > 0)
            parts.Add("реле: " + string.Join(", ", relays.Select(r => $"{r.Channel}={r.State switch { "on" => "вкл", "off" => "выкл", _ => "?" }}")));
        var buttons = IrButtonService.Read(d.Config).Keys.ToList();
        if (buttons.Count > 0)
            parts.Add("ИК-кнопки: " + string.Join(", ", buttons));
        if (Cameras.Types.Contains(d.TypeCode))
            parts.Add("камера");
        return string.Join(" · ", parts);
    }

    private void AppendWeather(StringBuilder sb, WeatherDto w, DateTimeOffset now)
    {
        if (!w.Configured || w.Current is null)
        {
            sb.AppendLine("Прогноз погоды: не настроен.");
            return;
        }
        var c = w.Current;
        sb.AppendLine($"Прогноз погоды (Open-Meteo{(w.Stale ? ", устарел" : "")}): сейчас {Temperature(c.Temperature)}"
            + (c.Apparent is { } a ? $", ощущается {Temperature(a)}" : "")
            + $", {WeatherText(c.WeatherCode)}"
            + (c.Humidity is { } h ? $", влажность {h.ToString("0", Ru)}%" : "")
            + (c.WindSpeed is { } ws ? $", ветер {ws.ToString("0.#", Ru)} м/с" : "") + ".");
        foreach (var day in w.Daily.Where(x => x.Date >= DateOnly.FromDateTime(now.UtcDateTime.AddDays(-1))).Take(3))
            sb.AppendLine($"  {day.Date:dd.MM}: {Temperature(day.TemperatureMin)}…{Temperature(day.TemperatureMax)}, {WeatherText(day.WeatherCode)}, осадки {day.PrecipitationSum.ToString("0.#", Ru)} мм"
                + (day.PrecipitationProbabilityMax is { } p ? $" ({p}%)" : ""));
        var hours = w.Hourly.Where(x => x.Time > now).Where((_, i) => i % 3 == 0).Take(8)
            .Select(x => $"{Local(x.Time):HH:mm} {Temperature(x.Temperature)}{(x.PrecipitationProbability is { } p ? $" {p}%" : "")}");
        sb.AppendLine("  По часам: " + string.Join("; ", hours));
    }

    internal static (string Channel, string? State)[] RelayChannelsOf(string typeCode, JsonDocument? info, JsonDocument? state)
    {
        var channels = new List<(string, string?)>();
        if (state?.RootElement is { ValueKind: JsonValueKind.Object } root)
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is "on" or "off")
                    channels.Add((p.Name, p.Value.GetString()));
        var announced = info?.RootElement is { ValueKind: JsonValueKind.Object } i && i.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array
            && cmds.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "relay");
        if (channels.Count == 0 && announced)
            channels.Add((typeCode == "heat" ? "heater" : "relay", null));
        return channels.ToArray();
    }

    internal static (string Channel, string? State)[] RelayChannels(Device d) => RelayChannelsOf(d.TypeCode, d.Info, d.State);

    /// <summary>Indoor zones are the house and technical rooms; the perimeter and the garden are outside.</summary>
    internal static bool Indoor(ZoneKind kind) => kind is ZoneKind.House or ZoneKind.Tech;

    internal static string KindText(ZoneKind kind) => kind switch
    {
        ZoneKind.House => "дом, внутри",
        ZoneKind.Tech => "техническая, внутри",
        ZoneKind.Garden => "сад, улица",
        _ => "периметр, улица",
    };

    /// <summary>WMO weather codes as Open-Meteo reports them.</summary>
    internal static string WeatherText(int code) => code switch
    {
        0 => "ясно",
        1 => "преимущественно ясно",
        2 => "переменная облачность",
        3 => "пасмурно",
        45 or 48 => "туман",
        51 or 53 or 55 => "морось",
        56 or 57 => "ледяная морось",
        61 => "небольшой дождь",
        63 => "дождь",
        65 => "сильный дождь",
        66 or 67 => "ледяной дождь",
        71 => "небольшой снег",
        73 => "снег",
        75 => "сильный снег",
        77 => "снежная крупа",
        80 or 81 or 82 => "ливень",
        85 or 86 => "снегопад",
        95 => "гроза",
        96 or 99 => "гроза с градом",
        _ => $"код погоды {code}",
    };

    private static string Age(DateTimeOffset ts, DateTimeOffset now)
    {
        var age = now - ts;
        return age < TimeSpan.FromMinutes(30) ? ""
            : age < TimeSpan.FromHours(1) ? $" ({age.Minutes} мин назад)"
            : age < TimeSpan.FromHours(24) ? $" ({(int)age.TotalHours} ч назад)"
            : $" ({(int)age.TotalDays} дн назад)";
    }

    private DateTimeOffset Local(DateTimeOffset utc) => hive.Value.ToLocal(utc);
}
