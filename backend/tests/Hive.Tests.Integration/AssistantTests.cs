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
        var spec = new HornetSpec { Id = id, Type = "guard-cam", Motion = new MotionSpec { EverySeconds = 0, UploadUrl = "http://localhost" } };
        var role = (GuardCamRole)HornetRole.Create(spec, Path.Combine(AppContext.BaseDirectory, "scenarios", "photos"));
        var hornet = new VirtualHornet(spec, role);
        var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        _links.Add(link);
        var runner = new HornetRunner(hornet, link, hive.App.CreateClient(), TimeProvider.System);
        _ = Task.Run(() => runner.RunAsync(_stop.Token));
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), $"{id} online");
        return (hornet, role);
    }

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
        Assert.Contains("Охрана:", request["messages"]![0]!["content"]!.GetValue<string>());
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
}
