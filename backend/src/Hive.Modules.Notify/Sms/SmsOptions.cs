using System.Text.RegularExpressions;

namespace Hive.Modules.Notify.Sms;

/// <summary>SMS about critical events through a USB LTE modem (Quectel EC25) on this node, docs/sms-keenetic.md.</summary>
public sealed class SmsOptions
{
    public const string Section = "Sms";

    /// <summary>The modem's AT port, e.g. /dev/ttyUSB2 (EC25) or COM5. Empty: SMS is off.</summary>
    public string? Port { get; set; }

    /// <summary>Phone numbers, comma or semicolon separated: +79161234567, 89161234567.</summary>
    public string? Recipients { get; set; }

    /// <summary>At most this many SMS per hour for the whole node: a guard for the balance and against a storm of events.</summary>
    public int MaxPerHour { get; set; } = 6;

    /// <summary>The same event of the same device is sent again no sooner than this.</summary>
    public int GroupWindowMin { get; set; } = 30;

    public IReadOnlyList<string> Numbers => SmsNumber.ParseList(Recipients);

    public bool Enabled => !string.IsNullOrWhiteSpace(Port) && Numbers.Count > 0;
}

/// <summary>Phone numbers as the user types them, in the form the modem gets: +digits.</summary>
public static partial class SmsNumber
{
    [GeneratedRegex(@"^\+\d{7,15}$")]
    private static partial Regex Valid();

    /// <summary>"8 (916) 123-45-67" gives "+79161234567"; null when it is not a phone number.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var cleaned = new string(text.Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());
        if (cleaned.Length == 11 && cleaned[0] == '8')
            cleaned = "+7" + cleaned[1..];
        else if (cleaned.Length > 0 && cleaned[0] != '+')
            cleaned = "+" + cleaned;
        return Valid().IsMatch(cleaned) ? cleaned : null;
    }

    public static IReadOnlyList<string> ParseList(string? text) =>
        (text ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize).OfType<string>().Distinct().ToList();
}
