using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests.Integration;

/// <summary>
/// go2rtc's stream API as the real one answers it (checked against alexxit/go2rtc:1.9.4): GET /api/streams lists
/// {name: {producers: [{url}], consumers: [{format_name}]}}, PUT ?name=&amp;src= adds, DELETE ?src= removes.
/// <see cref="Restart"/> forgets everything, as go2rtc does when its container restarts. <see cref="Consumers"/> are the
/// connections reading a stream (format_name as go2rtc gives it: mse/fmp4, mjpeg, rtsp, jpeg).
/// </summary>
public sealed class FakeGo2Rtc : IAsyncDisposable
{
    private readonly WebApplication _app;

    public ConcurrentDictionary<string, string> Streams { get; } = new();

    public ConcurrentDictionary<string, string[]> Consumers { get; } = new();

    public string BaseUrl { get; private set; } = "";

    private FakeGo2Rtc(WebApplication app) => _app = app;

    public static async Task<FakeGo2Rtc> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeGo2Rtc(app);
        app.MapGet("/api/streams", () =>
        {
            var json = new JsonObject();
            foreach (var (name, src) in fake.Streams)
            {
                JsonArray? consumers = null;
                if (fake.Consumers.TryGetValue(name, out var formats))
                    consumers = new JsonArray(formats.Select(f => (JsonNode)new JsonObject { ["format_name"] = f }).ToArray());
                json[name] = new JsonObject { ["producers"] = new JsonArray(new JsonObject { ["url"] = src }), ["consumers"] = consumers };
            }
            return Results.Text(json.ToJsonString(), "application/json");
        });
        app.MapPut("/api/streams", (string name, string src) =>
        {
            fake.Streams[name] = src;
            return Results.Ok();
        });
        app.MapDelete("/api/streams", (string src) =>
        {
            fake.Streams.TryRemove(src, out _);
            return Results.Ok();
        });
        await app.StartAsync();
        fake.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return fake;
    }

    public void Restart()
    {
        Streams.Clear();
        Consumers.Clear();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
