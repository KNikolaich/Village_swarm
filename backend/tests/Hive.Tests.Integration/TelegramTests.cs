using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Devices;
using Hive.Modules.Notify;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>Build step 8 acceptance: "motion in the simulator arrives in Telegram with photos", against a fake Bot API.</summary>
[Collection(HiveCollection.Name)]
public sealed class TelegramTests(HiveFixture hive) : IAsyncLifetime
{
    private readonly List<IHornetLink> _links = [];
    private readonly CancellationTokenSource _stop = new();
    private FakeTelegram Tg => hive.Telegram;

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
        Assert.Equal(FakeTelegram.BotUsername, code!.BotUsername);
        Tg.UserSays(chat, $"/link {code.Code.ToLowerInvariant()}");
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.Contains("Чат привязан") == true, "link confirmation");
    }

    /// <summary>A camera that really runs (HornetRunner): publishes over MQTT, answers commands, uploads photos to the api.</summary>
    private async Task<(VirtualHornet Hornet, GuardCamRole Role, IHornetLink Link)> CameraAsync(string id)
    {
        var spec = new HornetSpec { Id = id, Type = "guard-cam", Motion = new MotionSpec { EverySeconds = 0, UploadUrl = "http://localhost" } };
        var role = (GuardCamRole)HornetRole.Create(spec, Path.Combine(AppContext.BaseDirectory, "scenarios", "photos"));
        var hornet = new VirtualHornet(spec, role);
        var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        _links.Add(link);
        var runner = new HornetRunner(hornet, link, hive.App.CreateClient(), TimeProvider.System);
        _ = Task.Run(() => runner.RunAsync(_stop.Token));
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), $"{id} online");
        return (hornet, role, link);
    }

    [Fact]
    public async Task Unlinked_chats_are_ignored_and_codes_work_once()
    {
        const long stranger = 900_001;
        Tg.UserSays(stranger, "/status");
        Tg.UserSays(stranger, "/start");
        await Tg.WaitForAsync(s => s.ChatId == stranger && s.Text?.Contains("не привязан") == true, "not-linked hint");
        Assert.DoesNotContain(Tg.Outbox, s => s.ChatId == stranger && s.Text?.Contains("Рой") == true);

        Tg.UserSays(stranger, "/link WRONG123");
        await Tg.WaitForAsync(s => s.ChatId == stranger && s.Text?.Contains("Код не подошёл") == true, "bad code reply");
        Assert.True(await hive.QueryAsync(db => db.Audit.AnyAsync(a => a.Action == "tg_unlinked_command" && a.Target == "900001")));

        await LinkAsync(900_002);
        var me = await (await hive.LoginAsync()).GetFromJsonAsync<TelegramStatusDto>("/api/me/telegram");
        Assert.Contains(me!.Links, l => l.ChatId == 900_002);
    }

    [Fact]
    public async Task Motion_alarm_arrives_with_photos_and_repeats_are_grouped()
    {
        const long chat = 900_010;
        await LinkAsync(chat);
        var (hornet, role, _) = await CameraAsync("guard-tg1");

        hornet.Enqueue(now => role.TriggerMotion(now));
        var alarm = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmessage" && s.Text?.Contains("guard-tg1") == true, "alarm text");
        Assert.Contains("Движение", alarm.Text);
        Assert.False(alarm.Silent);
        Assert.Contains("ack:", alarm.Buttons);

        var album = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmediagroup" && s.ReplyTo == alarm.MessageId, "photos as a reply");
        Assert.Equal(3, album.Photos);

        // Same device, same type, inside the window: the first message is edited, not a new one.
        hornet.Enqueue(now => role.TriggerMotion(now.AddSeconds(30))); // past the camera cooldown
        var edit = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "editmessagetext" && s.MessageId == alarm.MessageId, "grouped edit");
        Assert.Contains("×2", edit.Text);
        Assert.Single(Tg.Outbox, s => s.ChatId == chat && s.Method == "sendmessage" && s.Text?.Contains("guard-tg1") == true);
    }

    [Fact]
    public async Task Ack_button_acknowledges_the_event()
    {
        const long chat = 900_020;
        await LinkAsync(chat);
        var (hornet, role, _) = await CameraAsync("guard-tg2");
        hornet.Enqueue(now => role.TriggerMotion(now));
        var alarm = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmessage" && s.Text?.Contains("guard-tg2") == true, "alarm");
        var eventId = JsonDocument.Parse(alarm.Buttons!).RootElement.GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString()!["ack:".Length..];

        Tg.UserPresses(chat, alarm.MessageId, $"ack:{eventId}");
        await Tg.WaitForAsync(s => s.Method == "answercallbackquery" && s.Text == "Принято", "callback answer");
        var acked = await hive.QueryAsync(db => db.Events.SingleAsync(e => e.Id == eventId));
        Assert.Equal("tg:kirill", acked.AcknowledgedBy);
    }

    [Fact]
    public async Task Photo_command_returns_a_fresh_snapshot()
    {
        const long chat = 900_030;
        await LinkAsync(chat);
        await CameraAsync("guard-tg3");

        Tg.UserSays(chat, "/photo guard-tg3");
        var started = await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.StartsWith("📷 Снимаю") == true, "snapshot started");
        var album = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmediagroup" && s.ReplyTo == started.MessageId, "snapshot photo");
        Assert.Equal(1, album.Photos);
        Assert.True(await hive.QueryAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "guard-tg3" && c.Name == "snapshot" && c.Status == CommandStatus.Ok)));
    }

    [Fact]
    public async Task Arm_and_confirmed_disarm_reach_the_cameras()
    {
        const long chat = 900_040;
        await LinkAsync(chat);
        await CameraAsync("guard-tg4");

        Tg.UserSays(chat, "/arm");
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.Contains("Охрана включена") == true, "armed");
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "guard-tg4" && c.Name == "arm" && c.Status == CommandStatus.Ok), "camera acked arm");

        Tg.UserSays(chat, "/disarm");
        var confirm = await Tg.WaitForAsync(s => s.ChatId == chat && s.Text == "Снять охрану?", "disarm confirmation");
        Assert.Equal("true", await hive.QueryAsync(db => db.Modes.Where(m => m.Key == Mode.Armed).Select(m => m.Value).SingleAsync()));
        Tg.UserPresses(chat, confirm.MessageId, "disarm:yes");
        await hive.WaitForAsync(db => db.Modes.AnyAsync(m => m.Key == Mode.Armed && m.Value == "false"), "disarmed");
        Assert.True(await hive.QueryAsync(db => db.Audit.AnyAsync(a => a.Action == "disarm" && a.Actor == "tg:kirill")));

        Tg.UserSays(chat, "/status");
        var status = await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.Contains("Рой") == true, "status");
        Assert.Contains("снята", status.Text);
    }

    [Fact]
    public async Task Viewer_cannot_arm_from_telegram()
    {
        await hive.CreateUserAsync("tg-viewer", "tg-viewer-password", Hive.Infrastructure.Identity.HiveRoles.Viewer);
        const long chat = 900_050;
        await LinkAsync(chat, "tg-viewer", "tg-viewer-password");
        Tg.UserSays(chat, "/arm");
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.Contains("Недостаточно прав") == true, "refusal");
    }

    [Fact]
    public async Task Web_api_sends_commands_and_tracks_acks()
    {
        await CameraAsync("guard-cmd1");
        var client = await hive.LoginAsync();
        var sent = await client.PostAsJsonAsync("/api/devices/guard-cmd1/commands", new { name = "arm", payload = new { armed = false } });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, sent.StatusCode);
        var command = await sent.Content.ReadFromJsonAsync<CommandDto>();
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.Cid == command!.Cid && c.Status == CommandStatus.Ok), "ack");
        var done = await client.GetFromJsonAsync<CommandDto>($"/api/devices/guard-cmd1/commands/{command!.Cid}");
        Assert.Equal("ok", done!.Status);

        var unknown = await client.PostAsJsonAsync("/api/devices/guard-cmd1/commands", new { name = "dance" });
        var unknownCmd = await unknown.Content.ReadFromJsonAsync<CommandDto>();
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.Cid == unknownCmd!.Cid && c.Status == CommandStatus.Unsupported), "unsupported");
    }
}
