using System.Text.Json;
using Hive.Modules.Notify.Telegram;
using Telegram.Bot.Exceptions;

namespace Hive.Tests.Unit;

public class TelegramErrorTests
{
    [Fact]
    public void Unreadable_reply_is_not_a_network_error()
    {
        // Switching endpoints cannot fix a reply the client cannot read; the poller skips that batch instead.
        var ex = new RequestException("There was an exception during deserialization of the response.", new JsonException("bad"));
        Assert.True(TelegramGateway.IsUnreadableReply(ex));
        Assert.False(TelegramGateway.IsNetworkError(ex));
    }

    [Fact]
    public void Connection_problems_still_switch_endpoints()
    {
        Assert.True(TelegramGateway.IsNetworkError(new HttpRequestException("reset")));
        Assert.True(TelegramGateway.IsNetworkError(new RequestException("no route", new HttpRequestException("reset"))));
    }

    [Fact]
    public void Api_errors_are_returned_to_the_caller()
    {
        var ex = new ApiRequestException("Forbidden", 403);
        Assert.False(TelegramGateway.IsNetworkError(ex));
        Assert.False(TelegramGateway.IsUnreadableReply(ex));
    }

    [Fact]
    public void Shape_lists_field_names_and_types_without_values()
    {
        using var doc = JsonDocument.Parse("""{"ok":true,"result":[{"update_id":7477,"message":{"text":"secret text","entities":[{"offset":0}]}}]}""");
        var shape = TelegramGateway.Shape(doc.RootElement);
        Assert.Equal("{ok:bool,result:[{update_id:num,message:{text:str,entities:[{offset:num}]}}]}", shape);
        Assert.DoesNotContain("secret", shape);
    }

    [Theory]
    [InlineData("https://proxy.example/secret-path")]
    [InlineData("https://proxy.example/secret-path/")]
    public async Task Requests_go_to_the_proxy_path(string baseUrl)
    {
        // Telegram.Bot drops the path of BaseUrl; the gateway puts it back through PathPrefixHandler.
        var (root, prefix) = TelegramGateway.SplitBaseUrl(baseUrl);
        Assert.Equal(("https://proxy.example", "/secret-path"), (root, prefix));
        var capture = new CapturingHandler();
        var client = new global::Telegram.Bot.TelegramBotClient(
            new global::Telegram.Bot.TelegramBotClientOptions("123:abc", root), new HttpClient(new PathPrefixHandler(prefix) { InnerHandler = capture }));
        await global::Telegram.Bot.TelegramBotClientExtensions.GetUpdates(client, timeout: 0);
        Assert.Equal("https://proxy.example/secret-path/bot123:abc/getUpdates", capture.Uri!.ToString());
    }

    [Fact]
    public void No_base_url_means_the_official_api()
    {
        Assert.Equal(("https://api.telegram.org", ""), TelegramGateway.SplitBaseUrl(null));
        Assert.Equal(("https://vps.example:8443", ""), TelegramGateway.SplitBaseUrl("https://vps.example:8443/"));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":[]}""", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
