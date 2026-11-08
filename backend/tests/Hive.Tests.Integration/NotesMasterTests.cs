using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Modules.Cluster;
using Hive.Modules.Notes;

namespace Hive.Tests.Integration;

/// <summary>The city is the master of the notes: it takes the dacha's notes once, and answers for the dacha's people by the shared key.</summary>
public sealed class NotesMasterTests(CityFixture city) : IClassFixture<CityFixture>
{
    private static readonly string Key = NotesMasterPolicy.Key(CityFixture.CityBridgeSecret);

    private HttpClient Dacha(string user = "kirill", string role = "member", string? key = null)
    {
        var client = city.App.CreateClient();
        client.DefaultRequestHeaders.Add("X-Replica-Key", key ?? Key);
        client.DefaultRequestHeaders.Add("X-Replica-User", Uri.EscapeDataString(user));
        client.DefaultRequestHeaders.Add("X-Replica-Role", role);
        return client;
    }

    private static ImportNote Note(int id, int? parent, string title, int sort = 0, string body = "") =>
        new(id, parent, sort, title, "📝", null, body, DateTimeOffset.UtcNow.AddDays(-3), "user:old", DateTimeOffset.UtcNow.AddDays(-2), "user:old");

    private static async Task<Dictionary<int, int>> ImportAsync(HttpClient client, params ImportNote[] notes)
    {
        using var response = await client.PostAsJsonAsync("/api/replica/notes/import", new NotesImportRequest(notes));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NotesImportResult>())!.Map.ToDictionary(p => p.Key, p => p.Value);
    }

    [Fact]
    public async Task Notes_of_the_dacha_come_over_with_their_tree_and_a_repeat_makes_no_copies()
    {
        using var dacha = Dacha();
        // The children come before the parent in the list: the city puts parents first.
        var notes = new[] { Note(502, 501, "Теплица", body: "полив в 6"), Note(501, null, "Дача"), Note(503, 501, "Погреб", sort: 1) };

        var first = await ImportAsync(dacha, notes);
        var second = await ImportAsync(dacha, notes);

        Assert.Equal(first, second);
        var admin = await city.LoginAsync();
        var tree = (await admin.GetFromJsonAsync<JsonElement>("/api/notes")).EnumerateArray().ToList();
        var root = tree.Single(n => n.GetProperty("title").GetString() == "Дача");
        Assert.Equal(first[501], root.GetProperty("id").GetInt32());
        Assert.Equal(["Теплица", "Погреб"], tree.Where(n => n.GetProperty("parentId").ValueKind == JsonValueKind.Number && n.GetProperty("parentId").GetInt32() == first[501])
            .OrderBy(n => n.GetProperty("sort").GetInt32()).Select(n => n.GetProperty("title").GetString()).ToList());
        var kept = await admin.GetFromJsonAsync<JsonElement>($"/api/notes/{first[502]}");
        Assert.Equal("полив в 6", kept.GetProperty("body").GetString());
        Assert.Equal("user:old", kept.GetProperty("createdBy").GetString());
    }

    [Fact]
    public async Task A_picture_keeps_its_name_so_the_text_that_shows_it_still_works()
    {
        using var dacha = Dacha();
        var map = await ImportAsync(dacha, Note(511, null, "С картинкой"));
        var id = "01JABCDEFGHJKMNPQRSTVWXYZ0";
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

        for (var i = 0; i < 2; i++)   // twice: the second is the same file
        {
            using var content = new ByteArrayContent(png);
            using var put = await dacha.PutAsync($"/api/replica/notes/attachments/{id}?note=511&name=photo.png&mime=image/png&created={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", content);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        var admin = await city.LoginAsync();
        using var file = await admin.GetAsync($"/api/notes/attachments/{id}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(png, await file.Content.ReadAsByteArrayAsync());
        var note = await admin.GetFromJsonAsync<JsonElement>($"/api/notes/{map[511]}");
        Assert.Equal(id, note.GetProperty("attachments")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Without_the_key_nothing_is_taken_and_nothing_is_read()
    {
        using var stranger = Dacha(key: "not-the-key");
        using var move = await stranger.PostAsJsonAsync("/api/replica/notes/import", new NotesImportRequest([Note(521, null, "Чужая")]));
        using var read = await stranger.GetAsync("/api/notes");
        using var anonymous = await city.App.CreateClient().GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.Unauthorized, move.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task The_dachas_people_write_notes_in_the_city_as_themselves_and_a_viewer_only_reads()
    {
        using var member = Dacha("kirill", "member");
        using var created = await member.PostAsJsonAsync("/api/notes", new { title = "Из Telegram", body = "купить гвозди" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var note = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("user:kirill", note.GetProperty("createdBy").GetString());

        using var viewer = Dacha("guest", "viewer");
        using var read = await viewer.GetAsync($"/api/notes/{note.GetProperty("id").GetInt32()}");
        using var write = await viewer.PostAsJsonAsync("/api/notes", new { title = "Нельзя" });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task The_assistant_asks_the_city_where_a_note_goes_and_puts_it_there()
    {
        using var dacha = Dacha("kirill", "member");
        var ensure = await dacha.PostAsJsonAsync("/api/notes/assistant/ensure", new NotePathRequest(["Усадьба", "Огород"]));
        ensure.EnsureSuccessStatusCode();
        var parent = (await ensure.Content.ReadFromJsonAsync<NoteEnsureDto>())!.ParentId;

        var match = await (await dacha.PostAsJsonAsync("/api/notes/assistant/match", new NotePathRequest(["усадьба", "огород", "Грядки"]))).Content.ReadFromJsonAsync<NotePathMatch>();
        var outline = await dacha.GetFromJsonAsync<List<string>>("/api/notes/assistant/outline?max=200");

        Assert.NotNull(parent);
        Assert.Equal(parent, match!.ParentId);
        Assert.Equal(["Грядки"], match.Missing);
        Assert.Contains("Усадьба / Огород", outline!);
    }
}
