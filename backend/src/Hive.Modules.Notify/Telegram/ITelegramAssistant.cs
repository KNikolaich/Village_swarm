using Telegram.Bot.Types;

namespace Hive.Modules.Notify.Telegram;

/// <summary>
/// Understands requests written in plain words (Hive.Modules.Assistant). Registered only when an LLM is configured;
/// without it the bot answers commands and buttons as before.
/// </summary>
public interface ITelegramAssistant
{
    /// <summary>Plain text that is not a command or a menu button, and presses on the assistant's own buttons.</summary>
    bool Handles(Update update);

    Task HandleAsync(Update update, CancellationToken ct);
}
