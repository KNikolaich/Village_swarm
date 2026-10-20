using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Assistant;
using Hive.Modules.Notify;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>
/// The Telegram assistant against a fake LLM and a fake Bot API: reading answers at once, every action waits for
/// its button, and notes land in the right branch.
/// </summary>
[Collection(HiveCollection.Name)]
public sealed class AssistantTests(HiveFixture hive) : IAsyncLifetime
{
    private readonly List<IHornetLink> _links = [];
    private readonly CancellationTokenSource _stop = new();
    private FakeTelegram Tg => hive.Telegram;
    private FakeLlm Llm => hive.Llm;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var link in _links)
            await link.DisposeAsync();
    }

    private async Task LinkAsync(long chat, string login = "admin", string? password = null)
    {
        var client = await hive.LoginAsync(login, password ?? HiveFixture.AdminPassword);
        var code = await (await client.PostAsync("/api/me/telegram/code", null)).Content.ReadFromJsonAsync<LinkCodeDto>();
        Tg.UserSays(chat, $"/link {code!.Code.ToLowerInvariant()}");
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.Contains("Чат привязан") == true, "link confirmation");
    }

    private async Task<(VirtualHornet Hornet, GuardCamRole Role)> CameraAsync(string id)
    {
        var (hornet, role) = await HornetAsync(new HornetSpec { Id = id, Type = "guard-cam", Motion = new MotionSpec { EverySeconds = 0, UploadUrl = "http://localhost" } });
        return (hornet, (GuardCamRole)role);
    }

    /// <summary>A hornet that really runs (HornetRunner): publishes over MQTT, answers commands, uploads photos to the api.</summary>
    private async Task<(VirtualHornet Hornet, HornetRole Role)> HornetAsync(HornetSpec spec)
    {
        var id = spec.Id;
        var role = HornetRole.Create(spec, Path.Combine(AppContext.BaseDirectory, "scenarios", "photos"));
        var hornet = new VirtualHornet(spec, role);
        var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        _links.Add(link);
        var runner = new HornetRunner(hornet, link, hive.App.CreateClient(), TimeProvider.System);
        _ = Task.Run(() => runner.RunAsync(_stop.Token));
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), $"{id} online");
        return (hornet, role);
    }

    private async Task<int> ZoneAsync(string name, string kind)
    {
        using var created = await (await hive.LoginAsync()).PostAsJsonAsync("/api/zones", new { name, kind });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task PlaceAsync(string deviceId, int zoneId) =>
        (await (await hive.LoginAsync()).PutAsJsonAsync($"/api/devices/{deviceId}/zone", new { zoneId })).EnsureSuccessStatusCode();

    private static string Data(FakeTelegram.Sent card, string verb) =>
        JsonDocument.Parse(card.Buttons!).RootElement.GetProperty("inline_keyboard").EnumerateArray()
            .SelectMany(row => row.EnumerateArray()).Select(b => b.GetProperty("callback_data").GetString()!)
            .Single(d => d.StartsWith($"as:{verb}:", StringComparison.Ordinal));

    private async Task SetGuardAsync(bool armed)
    {
        using var response = await (await hive.LoginAsync()).PutAsJsonAsync("/api/modes/armed", new { armed });
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> GuardModeAsync() =>
        (await (await hive.LoginAsync()).GetFromJsonAsync<JsonElement>("/api/modes/armed")).GetProperty("mode").GetString()!;

    [Fact]
    public async Task Status_question_is_answered_with_the_hives_own_data()
    {
        const long chat = 910_001;
        await LinkAsync(chat);
        Llm.OnText("как там дома?", (AssistantTools.HiveStatus, new { }));

        Tg.UserSays(chat, "как там дома?");
        var status = await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.Contains("Охрана") == true, "status reply");
        Assert.NotNull(status.ReplyTo);

        // The model saw the tools, the house's zones and guard mode, and the user's words as the last message.
        var request = Llm.RequestFor("как там дома?")!;
        Assert.Contains("set_guard", request["tools"]!.ToJsonString());
        var prompt = request["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("Охрана:", prompt);
        Assert.Contains("Устройства (", prompt);
        Assert.Contains("Прогноз погоды", prompt);
        Assert.True(await hive.QueryAsync(db => db.Audit.AnyAsync(a => a.Action == "assistant_request" && a.Actor == "tg:kirill")));
    }

    [Fact]
    public async Task Zone_status_lists_devices_and_sends_the_zone_cameras_photos()
    {
        const long chat = 910_002;
        await LinkAsync(chat);
        var admin = await hive.LoginAsync();
        using var created = await admin.PostAsJsonAsync("/api/zones", new { name = "Плантация 7", kind = "garden" });
        var zoneId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var (hornet, role) = await CameraAsync("guard-as1");
        (await admin.PutAsJsonAsync("/api/devices/guard-as1/zone", new { zoneId })).EnsureSuccessStatusCode();
        hornet.Enqueue(now => role.TriggerMotion(now));
        await hive.WaitForAsync(db => db.Media.AnyAsync(m => m.DeviceId == "guard-as1"), "a photo from the camera");

        // The model says "плантации 7"; the hive still finds "Плантация 7".
        Llm.OnText("статус плантации 7", (AssistantTools.ZoneStatus, new { zone = "плантации 7" }));
        Tg.UserSays(chat, "статус плантации 7");
        var card = await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.Contains("📍 <b>Плантация 7</b>") == true, "zone status");
        Assert.Contains("guard-as1", card.Text);
        Assert.Contains("на связи", card.Text);
        Assert.Contains($"as:snapzone:{zoneId}", card.Buttons);
        var album = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmediagroup" && s.ReplyTo == card.MessageId, "zone photos");
        Assert.Equal(1, album.Photos);

        // "Снять свежие" is the confirmation itself: the camera takes a new frame.
        Tg.UserPresses(chat, card.MessageId, $"as:snapzone:{zoneId}");
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "guard-as1" && c.Name == "snapshot" && c.Status == CommandStatus.Ok), "fresh snapshot");
    }

    [Fact]
    public async Task Guard_change_waits_for_the_button_and_works_once()
    {
        const long chat = 910_003;
        await LinkAsync(chat);
        await SetGuardAsync(false);
        Llm.OnText("поставь на охрану", (AssistantTools.SetGuard, new { mode = "armed" }));

        Tg.UserSays(chat, "поставь на охрану");
        var card = await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.StartsWith("🛡 Поставить на охрану?") == true, "confirmation card");
        await Task.Delay(500);
        Assert.Equal("disarmed", await GuardModeAsync()); // nothing happens before the press

        Tg.UserPresses(chat, card.MessageId, Data(card, "ok"));
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "editmessagetext" && s.MessageId == card.MessageId && s.Text?.Contains("✅") == true, "done edit");
        Assert.Equal("armed", await GuardModeAsync());
        Assert.True(await hive.QueryAsync(db => db.Audit.AnyAsync(a => a.Actor == "tg:kirill via assistant")));

        // The same button again finds nothing to do.
        Tg.UserPresses(chat, card.MessageId, Data(card, "ok"));
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "editmessagetext" && s.MessageId == card.MessageId && s.Text?.Contains("устарел") == true, "stale press");

        // Disarming through the assistant, cancelled: the guard stays on.
        Llm.OnText("сними охрану", (AssistantTools.SetGuard, new { mode = "disarmed" }));
        Tg.UserSays(chat, "сними охрану");
        var disarm = await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.StartsWith("🔓 Снять охрану?") == true, "disarm card");
        Tg.UserPresses(chat, disarm.MessageId, Data(disarm, "no"));
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "editmessagetext" && s.MessageId == disarm.MessageId && s.Text?.Contains("Отменено") == true, "cancelled edit");
        Assert.Equal("armed", await GuardModeAsync());
        await SetGuardAsync(false);
    }

    [Fact]
    public async Task Photos_of_all_cameras_are_taken_after_the_press()
    {
        const long chat = 910_004;
        await LinkAsync(chat);
        await CameraAsync("guard-as2");
        Llm.OnText("сделай фото всех камер", (AssistantTools.TakePhotos, new { }));

        Tg.UserSays(chat, "сделай фото всех камер");
        var card = await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.StartsWith("📷 Сделать снимки сейчас?") == true, "photos card");
        Assert.Contains("guard-as2", card.Text);
        Assert.False(await hive.QueryAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "guard-as2" && c.Name == "snapshot")));

        Tg.UserPresses(chat, card.MessageId, Data(card, "ok"));
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "guard-as2" && c.Name == "snapshot" && c.Status == CommandStatus.Ok), "snapshot done");
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmediagroup", "photo album");
    }

    [Fact]
    public async Task Note_goes_into_the_named_branch_creating_what_is_missing()
    {
        const long chat = 910_005;
        await LinkAsync(chat);
        var admin = await hive.LoginAsync();
        using var root = await admin.PostAsJsonAsync("/api/notes", new { title = "Мастерская" });
        var rootId = (await root.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        const string text = "добавь заметку в задумки мастерской сделать тёмную тему";
        Llm.OnText(text, (AssistantTools.AddNote, new { path = new[] { "мастерской", "Задумки" }, text = "сделать тёмную тему" }));
        Tg.UserSays(chat, text);
        var card = await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.StartsWith("📝 Добавить заметку") == true, "note card");
        Assert.Contains("Мастерская → Задумки", card.Text);
        Assert.Contains("Новые ветки: Задумки", card.Text);
        Assert.Contains("Мастерская", Llm.RequestFor(text)!["messages"]![0]!["content"]!.GetValue<string>()); // the outline reached the model

        Tg.UserPresses(chat, card.MessageId, Data(card, "ok"));
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "editmessagetext" && s.MessageId == card.MessageId && s.Text?.Contains("Заметка сохранена") == true, "saved edit");

        var branch = await hive.QueryAsync(db => db.Notes.SingleAsync(n => n.ParentId == rootId && n.Title == "Задумки" && n.DeletedAt == null));
        var note = await hive.QueryAsync(db => db.Notes.SingleAsync(n => n.ParentId == branch.Id && n.DeletedAt == null));
        Assert.Equal("сделать тёмную тему", note.Title);
        Assert.Equal("сделать тёмную тему", note.Body);
        Assert.Equal("tg:kirill via assistant", note.CreatedBy);
    }

    [Fact]
    public async Task Viewer_gets_answers_but_no_actions()
    {
        await hive.CreateUserAsync("as-viewer", "as-viewer-password", Hive.Infrastructure.Identity.HiveRoles.Viewer);
        const long chat = 910_006;
        await LinkAsync(chat, "as-viewer", "as-viewer-password");
        Llm.OnText("включи подготовку к приезду", (AssistantTools.SetGuard, new { mode = "arriving" }));

        Tg.UserSays(chat, "включи подготовку к приезду");
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.Contains("Недостаточно прав") == true, "refusal");
        Assert.DoesNotContain(Tg.Outbox, s => s.ChatId == chat && s.Buttons?.Contains("as:ok:") == true);
    }

    [Fact]
    public async Task Llm_failure_and_plain_answers_reach_the_chat_and_commands_still_work()
    {
        const long chat = 910_007;
        await LinkAsync(chat);
        Llm.Answer("какой сегодня курс доллара?", "Я умею только про дом и заметки.");
        Tg.UserSays(chat, "какой сегодня курс доллара?");
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Text == "Я умею только про дом и заметки.", "plain answer");

        Llm.Fail = true;
        try
        {
            Tg.UserSays(chat, "что с охраной?");
            await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.Contains("помощник сейчас недоступен") == true, "fallback");

            // Slash commands never go to the model.
            var before = Llm.Requests.Count;
            Tg.UserSays(chat, "/status");
            await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.Contains("Охрана") == true, "status command");
            Assert.Equal(before, Llm.Requests.Count);
        }
        finally
        {
            Llm.Fail = false;
        }
    }

    [Fact]
    public async Task Outdoor_temperature_question_gets_the_outdoor_sensors_and_the_forecast_in_its_context()
    {
        const long chat = 910_008;
        await LinkAsync(chat);
        var garden = await ZoneAsync("Огород-ИИ", "garden");
        await HornetAsync(new HornetSpec { Id = "meteo-as1", Type = "meteo", TeleIntervalS = 1 });
        await PlaceAsync("meteo-as1", garden);
        await hive.WaitForAsync(db => db.Telemetry.AnyAsync(t => t.DeviceId == "meteo-as1"), "meteo readings");

        Llm.Answer("какая температура на улице?", "На огороде 4,5 градуса, по прогнозу до 8.");
        Tg.UserSays(chat, "какая температура на улице?");
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Text == "На огороде 4,5 градуса, по прогнозу до 8.", "the model's own answer");

        var prompt = Llm.RequestFor("какая температура на улице?")!["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("- Огород-ИИ (сад, улица)", prompt);
        var line = prompt.Split('\n').Single(l => l.Contains("[meteo-as1]"));
        Assert.Contains("зона Огород-ИИ (сад, улица)", line);
        Assert.Contains("🌡", line);
    }

    [Fact]
    public async Task Photos_of_the_house_cameras_leave_the_outdoor_ones_out()
    {
        const long chat = 910_009;
        await LinkAsync(chat);
        var room = await ZoneAsync("Холл-ИИ", "house");
        var yard = await ZoneAsync("Двор-ИИ", "perimeter");
        await CameraAsync("guard-as3");
        await CameraAsync("guard-as4");
        await PlaceAsync("guard-as3", room);
        await PlaceAsync("guard-as4", yard);

        Llm.OnText("фото с камер дома", (AssistantTools.TakePhotos, new { place = "indoor" }));
        Tg.UserSays(chat, "фото с камер дома");
        var card = await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.StartsWith("📷 Сделать снимки сейчас?") == true, "photos card");
        Assert.Contains("guard-as3", card.Text);
        Assert.DoesNotContain("guard-as4", card.Text);

        Tg.UserPresses(chat, card.MessageId, Data(card, "ok"));
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "guard-as3" && c.Name == "snapshot" && c.Status == CommandStatus.Ok), "indoor snapshot");
        Assert.False(await hive.QueryAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "guard-as4" && c.Name == "snapshot")));
    }

    [Fact]
    public async Task Quiet_hours_are_the_same_as_mute_and_a_viewer_may_set_them()
    {
        await hive.CreateUserAsync("as-quiet", "as-quiet-password", Hive.Infrastructure.Identity.HiveRoles.Viewer);
        const long chat = 910_010;
        await LinkAsync(chat, "as-quiet", "as-quiet-password");

        Llm.OnText("режим тишины на 8 часов", (AssistantTools.Mute, new { hours = 8 }));
        Tg.UserSays(chat, "режим тишины на 8 часов");
        var card = await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.StartsWith("🔕 Включить тишину на 8 ч?") == true, "mute card");
        Assert.Null(await hive.QueryAsync(db => db.TgLinks.Where(l => l.ChatId == chat).Select(l => l.MutedUntil).SingleAsync()));

        Tg.UserPresses(chat, card.MessageId, Data(card, "ok"));
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "editmessagetext" && s.MessageId == card.MessageId && s.Text?.Contains("не придут 8 ч") == true, "muted edit");
        var until = await hive.QueryAsync(db => db.TgLinks.Where(l => l.ChatId == chat).Select(l => l.MutedUntil).SingleAsync());
        Assert.InRange(until!.Value, DateTimeOffset.UtcNow.AddHours(7.9), DateTimeOffset.UtcNow.AddHours(8.1));
    }

    [Fact]
    public async Task A_heater_is_switched_on_for_a_while_after_the_press()
    {
        const long chat = 910_011;
        await LinkAsync(chat);
        await HornetAsync(new HornetSpec { Id = "heat-as1", Type = "heat" });
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "heat-as1" && d.State != null), "heater state");

        Llm.OnText("включи обогрев на полчаса", (AssistantTools.SwitchRelay, new { device = "heat-as1", on = true, minutes = 30 }));
        Tg.UserSays(chat, "включи обогрев на полчаса");
        var card = await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.StartsWith("💡 Включить «heat-as1» на 30 мин?") == true, "relay card");

        Tg.UserPresses(chat, card.MessageId, Data(card, "ok"));
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "heat-as1" && c.Name == "relay" && c.Status == CommandStatus.Ok), "relay command done");
        var command = await hive.QueryAsync(db => db.Commands.FirstAsync(c => c.DeviceId == "heat-as1" && c.Name == "relay"));
        Assert.Equal(1800, command.Payload.RootElement.GetProperty("for_s").GetInt32());
    }
}
