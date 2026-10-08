using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Hive.Modules.Notify.Telegram;

/// <summary>
/// Long polling (spec 9.1): no inbound connections, so a grey IP is fine. Only one poller per token
/// may run (409 Conflict otherwise), which is why home and city have separate bots (spec 9.2).
/// </summary>
public sealed class TelegramBotHost(
    TelegramGateway telegram,
    IServiceScopeFactory scopes,
    IOptions<TelegramOptions> options,
    TimeProvider time,
    ILogger<TelegramBotHost> logger) : BackgroundService
{
    /// <summary>@username of the bot, shown in the web UI next to the link code.</summary>
    public string? BotUsername { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!telegram.Enabled)
            return;

        int? offset = null;
        var backoff = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (BotUsername is null)
                {
                    var me = await telegram.CallAsync(bot => bot.GetMe(stoppingToken), stoppingToken);
                    BotUsername = me.Username;
                    logger.LogInformation("Telegram bot @{Bot} polling via {Endpoint}", me.Username, telegram.CurrentEndpoint);
                }

                var updates = await telegram.CallAsync(bot => bot.GetUpdates(offset, limit: 50, timeout: options.Value.PollTimeoutS,
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery], cancellationToken: stoppingToken), stoppingToken);
                backoff = TimeSpan.FromSeconds(2);
                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    await HandleAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 409)
            {
                logger.LogWarning("Another client polls this bot token (409 Conflict); is the bot running twice? Retrying in 30 s");
                await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Telegram polling failed via {Endpoint}: {Error}. Retry in {Delay}s", telegram.CurrentEndpoint, ex.Message, backoff.TotalSeconds);
                await Task.Delay(backoff, time, stoppingToken);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 120));
            }
        }
    }

    private async Task HandleAsync(Update update, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<BotCommands>().HandleAsync(update, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Handling Telegram update {Id} failed", update.Id);
        }
    }
}
