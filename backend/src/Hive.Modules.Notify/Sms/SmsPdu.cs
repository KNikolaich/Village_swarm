using System.Globalization;
using System.Text;

namespace Hive.Modules.Notify.Sms;

/// <summary>
/// SMS-SUBMIT in PDU mode (3GPP TS 23.040), always UCS2: the texts are Russian. One message holds 70 characters,
/// a longer one is cut into parts of 67 (the rest of each part is the concatenation header) and joined by the phone.
/// </summary>
public static class SmsPdu
{
    public const int SingleMax = 70;
    public const int PartMax = 67;
    public const int MaxParts = 3;

    /// <summary><paramref name="Hex"/> is what goes after AT+CMGS; <paramref name="Length"/> is its argument (the TPDU without the SMSC byte).</summary>
    public sealed record Part(string Hex, int Length);

    public static IReadOnlyList<Part> Build(string number, string text, byte concatRef)
    {
        var chunks = Split(text);
        var parts = new List<Part>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var multi = chunks.Count > 1;
            var sb = new StringBuilder();
            sb.Append("00"); // the SMSC of the SIM
            sb.Append(multi ? "41" : "01"); // SMS-SUBMIT, no validity period, UDHI when concatenated
            sb.Append("00"); // the modem picks the message reference
            AppendAddress(sb, number);
            sb.Append("00"); // PID
            sb.Append("08"); // DCS: UCS2
            var body = Encoding.BigEndianUnicode.GetBytes(chunks[i]);
            var headerBytes = multi ? 6 : 0;
            sb.Append((headerBytes + body.Length).ToString("X2", CultureInfo.InvariantCulture));
            if (multi)
            {
                sb.Append("050003").Append(concatRef.ToString("X2", CultureInfo.InvariantCulture))
                    .Append(chunks.Count.ToString("X2", CultureInfo.InvariantCulture))
                    .Append((i + 1).ToString("X2", CultureInfo.InvariantCulture));
            }
            sb.Append(Convert.ToHexString(body));
            var hex = sb.ToString();
            parts.Add(new Part(hex, hex.Length / 2 - 1));
        }
        return parts;
    }

    /// <summary>Whole text in one message when it fits, otherwise up to three parts; the rest is cut with an ellipsis. A surrogate pair is never split.</summary>
    public static IReadOnlyList<string> Split(string text)
    {
        text = text.Trim();
        if (text.Length <= SingleMax)
            return [text];
        if (text.Length > MaxParts * PartMax)
            text = text[..(MaxParts * PartMax - 1)] + "…";
        var chunks = new List<string>();
        for (var pos = 0; pos < text.Length;)
        {
            var take = Math.Min(PartMax, text.Length - pos);
            if (take == PartMax && pos + take < text.Length && char.IsHighSurrogate(text[pos + take - 1]))
                take--;
            chunks.Add(text.Substring(pos, take));
            pos += take;
        }
        return chunks;
    }

    private static void AppendAddress(StringBuilder sb, string number)
    {
        var digits = number.TrimStart('+');
        sb.Append(digits.Length.ToString("X2", CultureInfo.InvariantCulture));
        sb.Append(number.StartsWith('+') ? "91" : "81");
        if (digits.Length % 2 == 1)
            digits += "F";
        for (var i = 0; i < digits.Length; i += 2)
            sb.Append(digits[i + 1]).Append(digits[i]);
    }
}
