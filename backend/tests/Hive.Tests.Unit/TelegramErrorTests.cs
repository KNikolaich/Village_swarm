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
}
