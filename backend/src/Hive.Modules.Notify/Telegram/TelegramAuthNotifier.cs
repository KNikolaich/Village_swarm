using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Hive.Modules.Notify.Telegram;

/// <summary>Login codes and sign-in warnings straight to the user's chats: not through the outbox, no quiet hours or mute.</summary>
public sealed class TelegramAuthNotifier(TelegramGateway telegram, HiveDbContext db, ILogger<TelegramAuthNotifier> logger) : IAuthNotifier
{
    public async Task<bool> SendAsync(int userId, string html, CancellationToken ct)
    {
        if (!telegram.Enabled)
            return false;
        var chats = await db.TgLinks.AsNoTracking().Where(l => l.UserId == userId).Select(l => l.ChatId).ToListAsync(ct);
        var sent = false;
        foreach (var chat in chats)
        {
            try
            {
                await telegram.CallAsync(bot => bot.SendMessage(chat, html, ParseMode.Html, cancellationToken: ct), ct);
                sent = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sign-in message to chat {Chat} was not sent", chat);
            }
        }
        return sent;
    }
}
