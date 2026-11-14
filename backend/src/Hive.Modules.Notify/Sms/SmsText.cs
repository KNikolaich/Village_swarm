using Hive.Domain;

namespace Hive.Modules.Notify.Sms;

/// <summary>The SMS wording: short, plain text, no emoji (one UCS-2 part is 70 characters).</summary>
public static class SmsText
{
    public const int MaxChars = 140;

    /// <summary>"Улей КРИТИЧНО: Протечка, Кухня, 23:14".</summary>
    public static string Event(string type, string place, DateTimeOffset localTs)
    {
        var text = $"Улей КРИТИЧНО: {Texts.EventType(type)}, {place}, {localTs:HH:mm}";
        return text.Length <= MaxChars ? text : text[..(MaxChars - 1)] + "…";
    }
}
