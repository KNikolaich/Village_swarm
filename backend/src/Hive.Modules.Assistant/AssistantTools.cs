using System.Text;
using System.Text.Json.Nodes;

namespace Hive.Modules.Assistant;

/// <summary>
/// What the LLM may do. It answers questions in words from the house data it is given; read tools send the hive's own
/// formatted answers (with photos); action tools only propose, and the hive asks for a button before acting.
/// </summary>
public static class AssistantTools
{
    public const string HiveStatus = "hive_status";
    public const string ZoneStatus = "zone_status";
    public const string Temperatures = "temperatures";
    public const string RecentEvents = "recent_events";
    public const string DeviceProblems = "device_problems";
    public const string SetGuard = "set_guard";
    public const string TakePhotos = "take_photos";
    public const string AddNote = "add_note";
    public const string Mute = "mute";
    public const string MarkViewed = "mark_viewed";
    public const string SwitchRelay = "switch_relay";
    public const string PressIr = "press_ir";

    public static readonly IReadOnlySet<string> Actions = new HashSet<string> { SetGuard, TakePhotos, AddNote, Mute, MarkViewed, SwitchRelay, PressIr };

    /// <summary>Actions about the chat itself: a viewer may do them too.</summary>
    public static readonly IReadOnlySet<string> Personal = new HashSet<string> { Mute };

    private const string Confirm = " Выполнится только после подтверждения кнопкой.";

    public static JsonArray Definitions() =>
    [
        Tool(HiveStatus, "Общая сводка дома в готовом виде: охрана, сколько шершней на связи, температуры, непросмотренные тревоги.", new JsonObject()),
        Tool(ZoneStatus, "Всё по одной зоне с фото: устройства, связь, показания, последние события и последние снимки камер зоны.",
            new JsonObject { ["zone"] = Str("Название зоны точно из списка зон.") }, "zone"),
        Tool(Temperatures, "Таблица текущих температур по всем датчикам.", new JsonObject()),
        Tool(RecentEvents, "Последние события (движение, тревоги, протечки) из журнала.",
            new JsonObject { ["count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20, ["description"] = "Сколько событий, по умолчанию 5." } }),
        Tool(DeviceProblems, "Какие шершни не на связи или со слабым сигналом.", new JsonObject()),
        Tool(SetGuard, "Сменить режим охраны: armed — поставить на охрану, disarmed — снять, arriving — подготовка к приезду." + Confirm,
            new JsonObject { ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("armed", "disarmed", "arriving") } }, "mode"),
        Tool(TakePhotos, "Сделать снимки камерами сейчас и прислать фото. place=indoor — камеры в помещениях (зоны «дом, внутри» и «техническая, внутри»), place=outdoor — на улице. Без параметров — все камеры." + Confirm,
            new JsonObject
            {
                ["place"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("indoor", "outdoor"), ["description"] = "«камеры дома», «внутри» — indoor; «на улице», «во дворе» — outdoor." },
                ["zone"] = Str("Зона из списка зон, если просят камеры одной зоны."),
                ["cameras"] = new JsonObject { ["type"] = "array", ["items"] = Str("Имя или id камеры из списка устройств."), ["description"] = "Конкретные камеры." },
            }),
        Tool(AddNote, "Добавить заметку. Путь — ветки от корня. «добавь заметку в улучшения разработки» значит путь [\"Разработка\", \"Улучшения\"]: родительный падеж читается справа налево. Если ветка уже есть в списке заметок, пиши её название точно как в списке." + Confirm,
            new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "array", ["items"] = Str("Название ветки."), ["description"] = "Ветки от корня до места заметки; пустой массив — в корень." },
                ["title"] = Str("Заголовок, если пользователь его назвал; иначе не указывай."),
                ["text"] = Str("Текст заметки как написал пользователь, без слов-команды и пути."),
            }, "path", "text"),
        Tool(Mute, "Режим тишины для этого чата, как команда /mute: предупреждения не приходят указанное число часов, тревоги приходят всё равно. hours=0 — выключить тишину." + Confirm,
            new JsonObject { ["hours"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 72, ["description"] = "Часы тишины, по умолчанию 1." } }, "hours"),
        Tool(MarkViewed, "Отметить непросмотренные события просмотренными, как /viewed. Без устройства — все." + Confirm,
            new JsonObject { ["device"] = Str("Имя или id устройства, если только его события.") }),
        Tool(SwitchRelay, "Включить или выключить реле устройства (свет, обогрев, розетка). Только устройства, у которых в списке есть «реле»." + Confirm,
            new JsonObject
            {
                ["device"] = Str("Имя или id устройства из списка."),
                ["channel"] = Str("Канал реле из списка у устройства; если канал один, можно не указывать."),
                ["on"] = new JsonObject { ["type"] = "boolean", ["description"] = "true — включить, false — выключить." },
                ["minutes"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1440, ["description"] = "Включить на время, если пользователь его назвал." },
            }, "device", "on"),
        Tool(PressIr, "Нажать выученную ИК-кнопку пульта (кондиционер, телевизор). Только кнопки из списка «ИК-кнопки» у устройства." + Confirm,
            new JsonObject { ["device"] = Str("Имя или id устройства."), ["button"] = Str("Название кнопки точно из списка.") }, "device", "button"),
    ];

    public static string SystemPrompt(string house)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты помощник «Улья» — системы автоматизации загородного дома. Пользователь пишет в Telegram по-русски.");
        sb.AppendLine("Ниже — актуальные данные дома: прогноз, зоны, все устройства с показаниями, реле и ИК-кнопками, ветки заметок.");
        sb.AppendLine("На вопросы отвечай сам по этим данным, коротко и по-человечески: например, «какая температура на улице» — прогноз плюс показания датчиков в уличных зонах, с указанием, где какой датчик. Ничего не выдумывай: если данных нет или они устарели, так и скажи.");
        sb.AppendLine("Когда пользователь просит сводку, статус зоны, события или проблемы со связью — вызови инструмент: Улей пришлёт готовый ответ (для зоны — с фото).");
        sb.AppendLine("Действия (охрана, снимки, заметки, тишина, просмотр событий, реле, ИК) ты только предлагаешь вызовом инструмента — Улей сам спросит подтверждение кнопкой. Никогда не пиши, что действие уже выполнено.");
        sb.AppendLine("Если в одном сообщении несколько просьб — вызови несколько инструментов. Если непонятно, о чём речь, задай один короткий уточняющий вопрос без инструментов.");
        sb.AppendLine("Ты не меняешь настройки, правила, прошивки, пользователей и Wi-Fi: на такие просьбы отвечай, что это делается на сайте. На вопросы не про дом и не про заметки отвечай одной фразой, что умеешь только это.");
        sb.AppendLine("Отвечай обычным текстом без Markdown.");
        sb.AppendLine();
        sb.Append(house);
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
