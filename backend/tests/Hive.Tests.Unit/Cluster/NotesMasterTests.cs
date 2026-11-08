using System.Net;
using System.Security.Claims;
using System.Text;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Identity;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Cluster;
using Hive.Modules.Notes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hive.Tests.Unit.Cluster;

/// <summary>The dacha side of the city being the master of the notes: when it applies, what it passes on and how.</summary>
public class NotesMasterTests
{
    private const string Secret = "shared-secret";

    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, Dictionary<string, string> Headers, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            Seen.Add((request.Method, request.RequestUri!.ToString(), headers, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return answer(request);
        }
    }

    private static (NotesMasterMiddleware Middleware, NotesMasterClient Client, bool[] NextCalled) Make(string node, bool moved, Recorder handler, bool enabled = true)
    {
        var cluster = Options.Create(new ClusterOptions { Enabled = enabled, NotesInCity = true });
        var hive = Options.Create(new HiveOptions { Node = node });
        var secret = Options.Create(new CityBrokerOptions { BridgePassword = Secret });
        var client = new NotesMasterClient(secret, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, handler);
        client.UseUrl("https://city.test");
        var called = new[] { false };
        var middleware = new NotesMasterMiddleware(_ => { called[0] = true; return Task.CompletedTask; }, cluster, hive, secret,
            new NotesMasterState(cluster, hive, () => moved), client);
        return (middleware, client, called);
    }

    private static DefaultHttpContext Context(string method, string path, string? body = null, string? user = null, string? role = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }
        if (user is not null)
            context.User = NotesMasterPolicy.Principal(user, role ?? HiveRoles.Member);
        return context;
    }

    private static string Answer(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    [Fact]
    public async Task With_the_notes_in_the_city_a_request_goes_there_as_the_person_who_made_it()
    {
        var handler = new Recorder(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"id\":7}", Encoding.UTF8, "application/json") });
        var (middleware, _, next) = Make("home", moved: true, handler);
        var context = Context("POST", "/api/notes", "{\"title\":\"Гвозди\"}", user: "kirill", role: HiveRoles.Admin);
        context.Request.QueryString = new QueryString("?x=1");

        await middleware.InvokeAsync(context);

        var sent = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://city.test/api/notes?x=1", sent.Url);
        Assert.Equal("{\"title\":\"Гвозди\"}", sent.Body);
        Assert.Equal("kirill", Uri.UnescapeDataString(sent.Headers["X-Replica-User"]));
        Assert.Equal("admin", sent.Headers["X-Replica-Role"]);
        Assert.Equal(NotesMasterPolicy.Key(Secret), sent.Headers["X-Replica-Key"]);
        Assert.Equal(StatusCodes.Status201Created, context.Response.StatusCode);
        Assert.Equal("{\"id\":7}", Answer(context));
        Assert.False(next[0]);
    }

    [Fact]
    public async Task Before_the_move_or_with_the_old_way_switched_on_the_notes_stay_here()
    {
        var handler = new Recorder(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var (notMoved, _, nextA) = Make("home", moved: false, handler);
        var (city, _, nextB) = Make("city", moved: true, handler);

        await notMoved.InvokeAsync(Context("GET", "/api/notes", user: "kirill"));
        await city.InvokeAsync(Context("GET", "/api/notes", user: "kirill"));
        await notMoved.InvokeAsync(Context("GET", "/api/devices", user: "kirill"));

        Assert.Empty(handler.Seen);
        Assert.True(nextA[0] && nextB[0]);
    }

    [Fact]
    public async Task A_city_that_does_not_answer_is_a_clear_refusal_not_a_silent_local_save()
    {
        var handler = new Recorder(_ => throw new HttpRequestException("no route"));
        var (middleware, _, next) = Make("home", moved: true, handler);
        var context = Context("POST", "/api/notes", "{}", user: "kirill");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Contains("в городе", Answer(context));
        Assert.False(next[0]);
    }

    [Fact]
    public async Task Nobody_signed_in_is_not_passed_on()
    {
        var handler = new Recorder(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var (middleware, _, _) = Make("home", moved: true, handler);
        var context = Context("GET", "/api/notes");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task The_city_takes_the_dachas_word_about_who_asks_only_with_the_key()
    {
        var (middleware, _, next) = Make("city", moved: false, new Recorder(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var good = Context("GET", "/api/notes");
        good.Request.Headers["X-Replica-Key"] = NotesMasterPolicy.Key(Secret);
        good.Request.Headers["X-Replica-User"] = Uri.EscapeDataString("Кирилл");
        good.Request.Headers["X-Replica-Role"] = "member";
        var bad = Context("GET", "/api/notes");
        bad.Request.Headers["X-Replica-Key"] = "guess";

        await middleware.InvokeAsync(good);
        var passed = next[0];
        await middleware.InvokeAsync(bad);

        Assert.True(passed);
        Assert.Equal("Кирилл", good.User.Identity!.Name);
        Assert.True(good.User.IsInRole(HiveRoles.Member));
        Assert.Equal(StatusCodes.Status401Unauthorized, bad.Response.StatusCode);
    }

    [Fact]
    public void A_role_the_city_does_not_know_is_a_viewer_and_the_strongest_one_is_passed()
    {
        Assert.True(NotesMasterPolicy.Principal("x", "root").IsInRole(HiveRoles.Viewer));
        var both = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "viewer"), new Claim(ClaimTypes.Role, "member")], "test"));
        Assert.Equal("member", NotesMasterPolicy.StrongestRole(both));
        Assert.Equal("viewer", NotesMasterPolicy.StrongestRole(new ClaimsPrincipal(new ClaimsIdentity("test"))));
    }

    [Fact]
    public async Task The_assistant_reads_the_city_and_is_told_when_it_cannot()
    {
        var down = false;
        var handler = new Recorder(r => down
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : r.RequestUri!.AbsolutePath.EndsWith("outline", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[\"Дача\",\"Дача / Огород\"]", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"parentId\":3}", Encoding.UTF8, "application/json") });
        var (_, client, _) = Make("home", moved: true, handler);
        var cluster = Options.Create(new ClusterOptions { Enabled = true });
        var master = new RemoteNoteMaster(new NotesMasterState(cluster, Options.Create(new HiveOptions { Node = "home" }), () => true), client);

        Assert.Equal(["Дача", "Дача / Огород"], await master.OutlineAsync(200, default));
        Assert.Equal(3, await master.EnsurePathAsync(["Дача"], "user:kirill", default));
        Assert.Equal("kirill", Uri.UnescapeDataString(handler.Seen[1].Headers["X-Replica-User"]));

        down = true;
        await Assert.ThrowsAsync<NoteMasterUnavailableException>(() => master.OutlineAsync(200, default));
        await Assert.ThrowsAsync<NoteMasterUnavailableException>(() => master.CreateAsync(new CreateNoteRequest("x"), "user:kirill", default));
    }

    [Fact]
    public void Parents_go_before_their_children_whatever_the_order_of_ids()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, ParentId = 3, Title = "c" },
            new() { Id = 2, ParentId = null, Title = "a" },
            new() { Id = 3, ParentId = 2, Title = "b" },
            new() { Id = 4, ParentId = 99, Title = "orphan" },   // its parent is in the trash: it becomes a root
        };

        var depth = NotesMigrator.Depths(notes);

        Assert.Equal((2, 0, 1, 0), (depth[1], depth[2], depth[3], depth[4]));
    }
}
