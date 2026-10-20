using System.Net;
using Hive.Modules.Notify.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Assistant;

/// <summary>
/// The Telegram assistant: requests in plain words, understood by an LLM (DeepSeek by default). Without an API key
/// it stays silent and the bot keeps answering its slash commands and menu buttons only.
/// </summary>
public static class AssistantModule
{
    public const string Name = "assistant";

    public static IServiceCollection AddAssistantModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AssistantOptions>(configuration.GetSection(AssistantOptions.Section));
        services.AddSingleton<AssistantState>();
        services.AddHttpClient<LlmClient>((sp, c) =>
                c.Timeout = TimeSpan.FromSeconds(Math.Clamp(sp.GetRequiredService<IOptions<AssistantOptions>>().Value.TimeoutS, 5, 120)))
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var o = sp.GetRequiredService<IOptions<AssistantOptions>>().Value;
                var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
                if (!string.IsNullOrWhiteSpace(o.Proxy))
                {
                    handler.Proxy = new WebProxy(o.Proxy);
                    handler.UseProxy = true;
                }
                return handler;
            });
        // Registered always; it declines every update while no API key is set (TelegramAssistant.Handles).
        services.AddScoped<ITelegramAssistant, TelegramAssistant>();
        return services;
    }
}
