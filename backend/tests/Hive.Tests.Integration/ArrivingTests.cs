using System.Net;
using System.Net.Http.Json;
using Hive.Domain;
using Hive.Modules.Devices;
using Hive.Modules.Notify;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>
/// "Едут хозяева" (contracts/ipcam.md): a guard-cam's motion, which its firmware reports as an alarm while armed, comes as a quiet
/// message with photos and the buttons to disarm or to say "it is not us"; the hive decides it by the guard mode.
/// </summary>
[Collection(HiveCollection.Name)]
public sealed class ArrivingTests(HiveFixture hive) : IAsyncLifetime
{
    private readonly List<IHornetLink> _links = [];
    private readonly CancellationTokenSource _stop = new();
    private FakeTelegram Tg => hive.Telegram;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        using var admin = await hive.LoginAsync();
        (await admin.PutAsJsonAsync("/api/modes/armed", new { armed = false })).Dispose();
        await _stop.CancelAsync();
        foreach (var link in _links)
            await link.DisposeAsync();
    }

    private async Task LinkAsync(long chat)
    {
        using var client = await hive.LoginAsync();
        var code = await (await client.PostAsync("/api/me/telegram/code", null)).Content.ReadFromJsonAsync<LinkCodeDto>();
        Tg.UserSays(chat, $"/link {code!.Code}");
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

    [Fact]
    public async Task A_guard_cams_motion_while_the_family_is_coming_is_quiet_with_photos_and_it_is_not_us_arms()
    {
        const long chat = 900_700;
        await LinkAsync(chat);
        var (hornet, role) = await CameraAsync("guard-arrive2");
        using var admin = await hive.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/modes/armed", new { armed = true, mode = "arriving" })).StatusCode);
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "guard-arrive2" && c.Name == "arm"), "the camera armed");

        hornet.Enqueue(now => role.TriggerMotion(now));
        var message = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmessage" && s.Text?.Contains("guard-arrive2") == true, "the quiet message");
        Assert.True(message.Silent); // no sound: it is most likely the family
        Assert.Contains("едут хозяева", message.Text);
        Assert.Contains("arr:disarm", message.Buttons);
        Assert.Contains("arr:arm", message.Buttons);
        var album = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmediagroup" && s.ReplyTo == message.MessageId, "its photos");
        Assert.Equal(3, album.Photos);
        Assert.True(album.Silent);
        var motion = await hive.QueryAsync(db => db.Events.SingleAsync(e => e.DeviceId == "guard-arrive2" && e.Type == "motion"));
        Assert.Equal(EventSeverity.Warn, motion.Severity); // the firmware said alarm; the hive knows the family is coming

        // "Это не мы": armed at once, and the message says so in place of its buttons.
        Tg.UserPresses(chat, message.MessageId, "arr:arm");
        await hive.WaitForAsync(db => db.Modes.AnyAsync(m => m.Key == Mode.Presence && m.Value == Mode.PresenceAway), "armed from the button");
        Assert.Equal("armed", (await admin.GetFromJsonAsync<ArmedDto>("/api/modes/armed"))!.Mode);
        var edited = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "editmessagereplymarkup" && s.MessageId == message.MessageId, "the buttons replaced");
        Assert.Contains("arr:done", edited.Buttons); // one button left saying what was done (its text is escaped in the JSON)
        Assert.DoesNotContain("arr:disarm", edited.Buttons);
    }

    [Fact]
    public async Task Disarming_from_the_quiet_message_asks_once_more()
    {
        const long chat = 900_710;
        await LinkAsync(chat);
        var (hornet, role) = await CameraAsync("guard-arrive3");
        using var admin = await hive.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/modes/armed", new { armed = true, mode = "arriving" })).StatusCode);
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "guard-arrive3" && c.Name == "arm"), "the camera armed");

        hornet.Enqueue(now => role.TriggerMotion(now));
        var message = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmessage" && s.Text?.Contains("guard-arrive3") == true, "the quiet message");

        Tg.UserPresses(chat, message.MessageId, "arr:disarm");
        var ask = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "editmessagereplymarkup" && s.MessageId == message.MessageId, "asked again");
        Assert.Contains("arr:disarm-yes", ask.Buttons);
        Assert.Equal("arriving", (await admin.GetFromJsonAsync<ArmedDto>("/api/modes/armed"))!.Mode); // not yet

        Tg.UserPresses(chat, message.MessageId, "arr:disarm-yes");
        // (the Telegram outbox is shared by the collection: the mode itself is what counts)
        await hive.WaitForAsync(db => db.Modes.AnyAsync(m => m.Key == Mode.Armed && m.Value == "false"), "disarmed");
    }

    [Fact]
    public async Task Armed_motion_is_still_an_alarm_with_sound()
    {
        const long chat = 900_720;
        await LinkAsync(chat);
        var (hornet, role) = await CameraAsync("guard-arrive4");
        using var admin = await hive.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/modes/armed", new { armed = true })).StatusCode);
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "guard-arrive4" && c.Name == "arm"), "the camera armed");

        hornet.Enqueue(now => role.TriggerMotion(now));
        var alarm = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmessage" && s.Text?.Contains("guard-arrive4") == true, "the alarm");
        Assert.False(alarm.Silent);
        Assert.Contains("ack:", alarm.Buttons);
        Assert.DoesNotContain("arr:", alarm.Buttons);
        Assert.Equal(EventSeverity.Alarm, (await hive.QueryAsync(db => db.Events.SingleAsync(e => e.DeviceId == "guard-arrive4" && e.Type == "motion"))).Severity);
    }
}
