using System.Text;
using System.Text.Json.Nodes;

namespace Hive.Modules.Assistant;

/// <summary>
/// What the LLM may do: read tools answer at once, action tools only propose (the hive asks for a button before acting).
/// The model never answers with data of its own: everything the user sees about the house comes from the hive.
/// </summary>
public static class AssistantTools
{
    public const string HiveStatus = "hive_status";
    public const string ZoneStatus = "zone_status";
    public const string Temperatures = "temperatures";
    public const string RecentEvents = "recent_events";
    public const string SetGuard = "set_guard";
    public const string TakePhotos = "take_photos";
    public const string AddNote = "add_note";

    public static readonly IReadOnlySet<string> Actions = new HashSet<string> { SetGuard, TakePhotos, AddNote };

    public static JsonArray Definitions() =>
    [
        Tool(HiveStatus, "Общий статус дома: режим охраны, сколько шершней на связи, температуры, непросмотренные тревоги.", new JsonObject()),
        Tool(ZoneStatus, "Всё по одной зоне: устройства, их связь и последние показания, последние события и последние фото камер зоны.",
            new JsonObject { ["zone"] = Str("Название зоны точно из списка зон.") }, "zone"),
        Tool(Temperatures, "Текущие температуры по всем датчикам.", new JsonObject()),
        Tool(RecentEvents, "Последние события (движение, тревоги, протечки).",
            new JsonObject { ["count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20, ["description"] = "Сколько событий, по умолчанию 5." } }),
        Tool(SetGuard, "Предложить сменить режим охраны: armed — поставить на охрану, disarmed — снять, arriving — подготовка к приезду. Выполнится только после подтверждения кнопкой.",
            new JsonObject { ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("armed", "disarmed", "arriving") } }, "mode"),
        Tool(TakePhotos, "Предложить сделать снимки камерами сейчас. Без зоны и камер — все камеры. Выполнится только после подтверждения кнопкой.",
            new JsonObject
            {
                ["zone"] = Str("Зона из списка зон, если просят камеры одной зоны."),
                ["cameras"] = new JsonObject { ["type"] = "array", ["items"] = Str("Название камеры из списка."), ["description"] = "Конкретные камеры." },
            }),
        Tool(AddNote, "Предложить добавить заметку. Путь — ветки от корня. «добавь заметку в улучшения разработки» значит путь [\"Разработка\", \"Улучшения\"]: родительный падеж читается справа налево. Если ветка уже есть в списке заметок, пиши её название точно как в списке. Выполнится только после подтверждения кнопкой.",
            new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "array", ["items"] = Str("Название ветки."), ["description"] = "Ветки от корня до места заметки; пустой массив — в корень." },
                ["title"] = Str("Заголовок, если пользователь его назвал; иначе не указывай."),
                ["text"] = Str("Текст заметки как написал пользователь, без слов-команды и пути."),
            }, "path", "text"),
    ];

    public static string SystemPrompt(DateTimeOffset localNow, string guard, IReadOnlyList<string> zones, IReadOnlyList<string> cameras, IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты помощник «Улья» — системы автоматизации загородного дома. Пользователь пишет в Telegram по-русски.");
        sb.AppendLine("Твоя задача — понять, что нужно, и вызвать подходящий инструмент. Данные о доме ты не придумываешь: их показывает сам Улей после вызова инструмента.");
        sb.AppendLine("Действия (охрана, снимки, заметки) ты только предлагаешь — Улей сам спросит подтверждение кнопкой. Никогда не пиши, что действие уже выполнено.");
        sb.AppendLine("Если в одном сообщении несколько просьб — вызови несколько инструментов. Если непонятно, о чём речь, задай один короткий уточняющий вопрос без инструментов.");
        sb.AppendLine("На вопросы не про дом и не про заметки отвечай одной фразой, что умеешь только это.");
        sb.AppendLine();
        sb.AppendLine($"Сейчас: {localNow:dd.MM.yyyy HH:mm}. Охрана: {guard}.");
        sb.AppendLine("Зоны: " + (zones.Count > 0 ? string.Join("; ", zones) : "нет"));
        sb.AppendLine("Камеры: " + (cameras.Count > 0 ? string.Join("; ", cameras) : "нет"));
        sb.AppendLine("Ветки заметок:");
        if (notes.Count == 0)
            sb.AppendLine("(заметок пока нет)");
        foreach (var line in notes)
            sb.AppendLine("- " + line);
        return sb.ToString();
    }

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
            },
        },
    };
}
