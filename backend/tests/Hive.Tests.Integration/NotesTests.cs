using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Infrastructure.Identity;
using Hive.Modules.Notes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests.Integration;

/// <summary>Notes: a tree of Markdown pages with an emoji, a bookmark color, pictures and files; branches move by drag and drop.</summary>
[Collection(HiveCollection.Name)]
public sealed class NotesTests(HiveFixture hive)
{
    private static async Task<int> CreateAsync(HttpClient client, string title, int? parentId = null, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["title"] = title, ["parentId"] = parentId };
        if (extra is not null)
            foreach (var p in JsonSerializer.SerializeToElement(extra).EnumerateObject())
                body[p.Name] = p.Value;
        using var response = await client.PostAsJsonAsync("/api/notes", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task<List<JsonElement>> ChildrenAsync(HttpClient client, int? parentId) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/notes")).EnumerateArray()
            .Where(n => parentId is { } p ? n.GetProperty("parentId").ValueKind == JsonValueKind.Number && n.GetProperty("parentId").GetInt32() == p
                                          : n.GetProperty("parentId").ValueKind == JsonValueKind.Null)
            .OrderBy(n => n.GetProperty("sort").GetInt32()).ToList();

    private static List<string> Titles(IEnumerable<JsonElement> notes) => notes.Select(n => n.GetProperty("title").GetString()!).ToList();

    [Fact]
    public async Task Notes_nest_without_limit_and_keep_their_header_and_text()
    {
        var admin = await hive.LoginAsync();
        var dev = await CreateAsync(admin, "Разработка", extra: new { icon = "🛠", color = "blue" });
        var ideas = await CreateAsync(admin, "Улучшения", dev);
        var deep = ideas;
        for (var i = 0; i < 6; i++)
            deep = await CreateAsync(admin, $"Уровень {i}", deep);

        using var patch = await admin.PatchAsJsonAsync($"/api/notes/{ideas}", new { body = "# OTA\n- починить откат", icon = "💡", color = "green" });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var note = await admin.GetFromJsonAsync<JsonElement>($"/api/notes/{ideas}");
        Assert.Equal("Улучшения", note.GetProperty("title").GetString());
        Assert.Equal("💡", note.GetProperty("icon").GetString());
        Assert.Equal("green", note.GetProperty("color").GetString());
        Assert.Equal("# OTA\n- починить откат", note.GetProperty("body").GetString());
        Assert.Equal(["Разработка"], note.GetProperty("path").EnumerateArray().Select(c => c.GetProperty("title").GetString()).ToList());

        var deepest = await admin.GetFromJsonAsync<JsonElement>($"/api/notes/{deep}");
        Assert.Equal(7, deepest.GetProperty("path").GetArrayLength());

        // An empty color clears it; a color outside the palette is refused.
        using var clear = await admin.PatchAsJsonAsync($"/api/notes/{ideas}", new { color = "" });
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await admin.GetFromJsonAsync<JsonElement>($"/api/notes/{ideas}")).GetProperty("color").ValueKind);
        using var badColor = await admin.PatchAsJsonAsync($"/api/notes/{ideas}", new { color = "#ff00ff" });
        Assert.Equal(HttpStatusCode.BadRequest, badColor.StatusCode);
        using var emptyTitle = await admin.PostAsJsonAsync("/api/notes", new { title = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, emptyTitle.StatusCode);
        using var noParent = await admin.PostAsJsonAsync("/api/notes", new { title = "x", parentId = 999999 });
        Assert.Equal(HttpStatusCode.NotFound, noParent.StatusCode);
    }

    [Fact]
    public async Task Notes_move_between_branches_to_the_root_and_never_into_their_own_branch()
    {
        var admin = await hive.LoginAsync();
        var a = await CreateAsync(admin, "Ветка A");
        var b = await CreateAsync(admin, "Ветка B");
        var a1 = await CreateAsync(admin, "A1", a);
        var a2 = await CreateAsync(admin, "A2", a);
        var a3 = await CreateAsync(admin, "A3", a);
        var a2child = await CreateAsync(admin, "A2.1", a2);

        // Reorder within the branch.
        using (var r = await admin.PostAsJsonAsync($"/api/notes/{a3}/move", new { parentId = a, index = 0 }))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(["A3", "A1", "A2"], Titles(await ChildrenAsync(admin, a)));

        // Into another branch: the moved note brings its children, the old branch closes the gap.
        using (var r = await admin.PostAsJsonAsync($"/api/notes/{a2}/move", new { parentId = b, index = 5 }))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(["A3", "A1"], Titles(await ChildrenAsync(admin, a)));
        Assert.Equal([0, 1], (await ChildrenAsync(admin, a)).Select(n => n.GetProperty("sort").GetInt32()).ToList());
        Assert.Equal(["A2"], Titles(await ChildrenAsync(admin, b)));
        Assert.Equal(["A2.1"], Titles(await ChildrenAsync(admin, a2)));

        // A branch becomes a root of its own.
        using (var r = await admin.PostAsJsonAsync($"/api/notes/{a1}/move", new { parentId = (int?)null, index = 0 }))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("A1", Titles(await ChildrenAsync(admin, null))[0]);

        // Not into itself or its own child.
        using (var r = await admin.PostAsJsonAsync($"/api/notes/{b}/move", new { parentId = a2child, index = 0 }))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await admin.PostAsJsonAsync($"/api/notes/{b}/move", new { parentId = b, index = 0 }))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task A_deleted_branch_waits_in_the_trash_and_comes_back_whole()
    {
        var admin = await hive.LoginAsync();
        var root = await CreateAsync(admin, "Корзина: дом");
        var child = await CreateAsync(admin, "Корзина: кухня", root);
        await CreateAsync(admin, "Корзина: плита", child);

        using (var r = await admin.DeleteAsync($"/api/notes/{child}"))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Empty(await ChildrenAsync(admin, root));
        using (var r = await admin.GetAsync($"/api/notes/{child}"))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        var trashed = (await admin.GetFromJsonAsync<JsonElement>("/api/notes/trash")).EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == child);
        Assert.Equal(2, trashed.GetProperty("notes").GetInt32());

        using (var r = await admin.PostAsync($"/api/notes/{child}/restore", null))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(["Корзина: кухня"], Titles(await ChildrenAsync(admin, root)));
        Assert.Equal(["Корзина: плита"], Titles(await ChildrenAsync(admin, child)));

        // When the parent is gone, the branch comes back as a root.
        using (var r = await admin.DeleteAsync($"/api/notes/{child}")) { }
        using (var r = await admin.DeleteAsync($"/api/notes/{root}")) { }
        using (var r = await admin.PostAsync($"/api/notes/{child}/restore", null))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Contains("Корзина: кухня", Titles(await ChildrenAsync(admin, null)));
        Assert.Equal(["Корзина: плита"], Titles(await ChildrenAsync(admin, child)));
    }

    [Fact]
    public async Task The_trash_is_emptied_after_thirty_days_with_the_files()
    {
        var admin = await hive.LoginAsync();
        var id = await CreateAsync(admin, "Старая заметка");
        using var content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        using var upload = await admin.PostAsync($"/api/notes/{id}/attachments?name=old.png", content);
        var path = (await hive.QueryAsync(db => db.NoteAttachments.SingleAsync(a => a.NoteId == id))).Path;
        var file = Path.Combine(hive.MediaRoot, path);
        Assert.True(File.Exists(file));

        using (var r = await admin.DeleteAsync($"/api/notes/{id}")) { }
        await hive.QueryAsync(db => db.Notes.Where(n => n.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.DeletedAt, DateTimeOffset.UtcNow.AddDays(-31))));

        using var scope = hive.App.Services.CreateScope();
        var files = await scope.ServiceProvider.GetRequiredService<NoteService>().PurgeAsync(CancellationToken.None);
        scope.ServiceProvider.GetRequiredService<NoteAttachmentService>().RemoveFiles(files);

        Assert.False(await hive.QueryAsync(db => db.Notes.AnyAsync(n => n.Id == id)));
        Assert.False(await hive.QueryAsync(db => db.NoteAttachments.AnyAsync(a => a.NoteId == id)));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Search_finds_words_in_titles_and_text_in_other_forms_too()
    {
        var admin = await hive.LoginAsync();
        var parent = await CreateAsync(admin, "Поиск: сад");
        var id = await CreateAsync(admin, "Поиск: полив", parent, new { body = "Нужно заменить шланги у теплицы до зимы" });

        var hits = (await admin.GetFromJsonAsync<JsonElement>("/api/notes/search?q=шланг")).EnumerateArray().ToList();
        var hit = Assert.Single(hits, h => h.GetProperty("id").GetInt32() == id);
        Assert.Contains("шланги", hit.GetProperty("snippet").GetString());
        Assert.Equal("Поиск: сад", hit.GetProperty("path")[0].GetProperty("title").GetString());

        // Another word form of "теплица".
        Assert.Contains((await admin.GetFromJsonAsync<JsonElement>("/api/notes/search?q=теплица")).EnumerateArray(), h => h.GetProperty("id").GetInt32() == id);
        Assert.DoesNotContain((await admin.GetFromJsonAsync<JsonElement>("/api/notes/search?q=%25")).EnumerateArray(), h => h.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public async Task Pictures_open_in_the_note_and_other_files_only_download()
    {
        var admin = await hive.LoginAsync();
        var id = await CreateAsync(admin, "С картинкой");

        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
        using var pasted = new ByteArrayContent(png);
        pasted.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var created = await admin.PostAsync($"/api/notes/{id}/attachments?name=image.png", pasted);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var picture = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(picture.GetProperty("isImage").GetBoolean());
        var url = picture.GetProperty("url").GetString()!;
        Assert.Equal($"![image.png]({url})", picture.GetProperty("markdown").GetString());

        using (var get = await admin.GetAsync(url))
        {
            Assert.Equal("image/png", get.Content.Headers.ContentType?.MediaType);
            Assert.Null(get.Content.Headers.ContentDisposition);
            Assert.Equal(png, await get.Content.ReadAsByteArrayAsync());
        }

        // An SVG claiming to be a picture is served as a download, never as a page on the hive's origin.
        using var svg = new ByteArrayContent("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray());
        svg.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var svgCreated = await admin.PostAsync($"/api/notes/{id}/attachments?name=evil.svg", svg);
        var file = await svgCreated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(file.GetProperty("isImage").GetBoolean());
        Assert.StartsWith("[evil.svg](", file.GetProperty("markdown").GetString());
        using (var get = await admin.GetAsync(file.GetProperty("url").GetString()))
        {
            Assert.Equal("application/octet-stream", get.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", get.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("nosniff", get.Headers.GetValues("X-Content-Type-Options").Single());
        }

        var note = await admin.GetFromJsonAsync<JsonElement>($"/api/notes/{id}");
        Assert.Equal(2, note.GetProperty("attachments").GetArrayLength());

        using (var del = await admin.DeleteAsync(url))
            Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        using (var gone = await admin.GetAsync(url))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        using var empty = await admin.PostAsync($"/api/notes/{id}/attachments?name=x", new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var tooBig = await admin.PostAsync($"/api/notes/{id}/attachments?name=big.bin", new ByteArrayContent(new byte[NoteAttachmentService.MaxBytes + 1]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
    }

    [Fact]
    public async Task A_viewer_reads_notes_but_cannot_change_them()
    {
        var admin = await hive.LoginAsync();
        var id = await CreateAsync(admin, "Для гостя");
        await hive.CreateUserAsync("notes-viewer", "notes-viewer-password", HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("notes-viewer", "notes-viewer-password");

        using (var r = await viewer.GetAsync($"/api/notes/{id}"))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await viewer.PostAsJsonAsync("/api/notes", new { title = "нельзя" }))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await viewer.PatchAsJsonAsync($"/api/notes/{id}", new { title = "нельзя" }))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await viewer.DeleteAsync($"/api/notes/{id}"))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}
