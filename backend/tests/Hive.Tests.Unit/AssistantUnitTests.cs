using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Domain;
using Hive.Modules.Assistant;
using Hive.Modules.Notes;

namespace Hive.Tests.Unit;

public class AssistantUnitTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Tool_calls_and_token_counts_are_read_from_the_reply()
    {
        var reply = LlmClient.Parse("""
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
              {"id":"call_1","type":"function","function":{"name":"set_guard","arguments":"{\"mode\":\"armed\"}"}},
              {"id":"call_2","type":"function","function":{"name":"hive_status","arguments":"{}"}}]}}],
             "usage":{"prompt_tokens":812,"completion_tokens":21}}
            """);
        Assert.Null(reply.Content);
        Assert.Equal(["set_guard", "hive_status"], reply.ToolCalls.Select(c => c.Name));
        Assert.Equal("{\"mode\":\"armed\"}", reply.ToolCalls[0].Arguments);
        Assert.Equal((812, 21), (reply.PromptTokens, reply.CompletionTokens));

        var text = LlmClient.Parse("""{"choices":[{"message":{"role":"assistant","content":"Уточните зону"}}]}""");
        Assert.Equal("Уточните зону", text.Content);
        Assert.Empty(text.ToolCalls);
    }

    [Fact]
    public void Zone_and_branch_names_match_despite_case_endings_and_yo()
    {
        string[] zones = ["Дом", "Плантация 1", "Плантация 2", "Тёплый двор"];
        Assert.Equal("Плантация 1", TelegramAssistant.Pick(zones, z => z, "плантации 1"));
        Assert.Equal("Плантация 2", TelegramAssistant.Pick(zones, z => z, "ПЛАНТАЦИЯ 2"));
        Assert.Equal("Тёплый двор", TelegramAssistant.Pick(zones, z => z, "теплый двор"));
        Assert.Equal("Дом", TelegramAssistant.Pick(zones, z => z, "доме"));
        Assert.Null(TelegramAssistant.Pick(zones, z => z, "гараж"));
        Assert.Null(TelegramAssistant.Pick(zones, z => z, " "));

        Assert.True(NoteService.SameWord("улучшения", "Улучшения"));
        Assert.True(NoteService.SameWord("разработки", "Разработка"));
        Assert.False(NoteService.SameWord("дом", "дым"));
    }

    [Fact]
    public void A_note_title_is_the_first_line_cut_to_length()
    {
        Assert.Equal("Купить шланг", TelegramAssistant.TitleFrom("Купить шланг\n15 метров, 3/4"));
        var title = TelegramAssistant.TitleFrom(new string('а', 120));
        Assert.Equal(78, title.Length);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void A_proposed_action_runs_once_in_its_own_chat_and_expires()
    {
        var clock = new Clock();
        var state = new AssistantState(clock);
        var args = new JsonObject { ["mode"] = "armed" };

        var a = state.Propose(1, AssistantTools.SetGuard, args, "?", TimeSpan.FromMinutes(5));
        Assert.Null(state.Take(a.Id, chat: 2));
        Assert.NotNull(state.Take(a.Id, chat: 1));
        Assert.Null(state.Take(a.Id, chat: 1));

        var b = state.Propose(1, AssistantTools.SetGuard, args, "?", TimeSpan.FromMinutes(5));
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Null(state.Take(b.Id, chat: 1));
    }

    [Fact]
    public void History_keeps_the_last_turns_and_the_day_has_a_limit()
    {
        var clock = new Clock();
        var state = new AssistantState(clock);
        for (var i = 0; i < 8; i++)
            state.Remember(7, $"q{i}", $"a{i}");
        var history = state.History(7);
        Assert.Equal(6, history.Count);
        Assert.Equal("q2", history[0].User);
        clock.Now += TimeSpan.FromHours(1);
        Assert.Empty(state.History(7));

        Assert.True(state.TryCount(2));
        Assert.True(state.TryCount(2));
        Assert.False(state.TryCount(2));
        clock.Now += TimeSpan.FromDays(1);
        Assert.True(state.TryCount(2));
    }

    [Fact]
    public void The_prompt_carries_the_house_data_and_every_tool_is_a_function()
    {
        var prompt = AssistantTools.SystemPrompt("Зоны (вид):\n- Огород (сад, улица)\n");
        Assert.Contains("какая температура на улице", prompt);
        Assert.EndsWith("- Огород (сад, улица)\n", prompt);
        var tools = AssistantTools.Definitions();
        Assert.All(tools, t => Assert.Equal("function", t!["type"]!.GetValue<string>()));
        Assert.Contains(tools, t => t!["function"]!["name"]!.GetValue<string>() == AssistantTools.Mute);
        Assert.Subset(AssistantTools.Actions.ToHashSet(), AssistantTools.Personal.ToHashSet());
    }

    [Fact]
    public void Relay_channels_come_from_the_state_or_from_the_announced_command()
    {
        var state = JsonDocument.Parse("""{"heater":"on","mode":"auto"}""");
        Assert.Equal([("heater", (string?)"on")], AssistantContext.RelayChannelsOf("heat", null, state));
        var info = JsonDocument.Parse("""{"commands":["relay","reboot"]}""");
        Assert.Equal([("relay", (string?)null)], AssistantContext.RelayChannelsOf("relay", info, null));
        Assert.Empty(AssistantContext.RelayChannelsOf("guard-cam", JsonDocument.Parse("""{"commands":["snapshot"]}"""), null));
    }

    [Fact]
    public void Indoor_zones_and_weather_codes_read_as_people_say_them()
    {
        Assert.True(AssistantContext.Indoor(ZoneKind.House));
        Assert.True(AssistantContext.Indoor(ZoneKind.Tech));
        Assert.False(AssistantContext.Indoor(ZoneKind.Garden));
        Assert.False(AssistantContext.Indoor(ZoneKind.Perimeter));
        Assert.Equal("пасмурно", AssistantContext.WeatherText(3));
        Assert.Equal("гроза", AssistantContext.WeatherText(95));
    }
}
