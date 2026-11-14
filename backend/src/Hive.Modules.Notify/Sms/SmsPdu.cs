using System.Globalization;
using System.Text;

namespace Hive.Modules.Notify.Sms;

/// <summary>One SMS-SUBMIT part: the hex of the TPDU (with the default SMSC) and the length <c>AT+CMGS</c> wants.</summary>
public sealed record SmsPart(string Hex, int TpduLength);

/// <summary>
/// SMS-SUBMIT in PDU mode (3GPP TS 23.040), always UCS-2 so that Russian text needs no transliteration. A text over
/// 70 characters becomes a concatenated message (UDH, 67 characters per part) that phones show as one.
/// </summary>
public static class SmsPdu
{
    public const int SingleChars = 70;
    public const int PartChars = 67;

    public static IReadOnlyList<SmsPart> Encode(string number, string text, byte reference = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        var chunks = Split(text ?? "");
        var parts = new List<SmsPart>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
            parts.Add(Build(number, chunks[i], chunks.Count > 1, reference, chunks.Count, i + 1));
        return parts;
    }

    /// <summary>Splits so that a surrogate pair (an emoji) is never cut in the middle.</summary>
    public static List<string> Split(string text)
    {
        if (text.Length <= SingleChars)
            return [text];
        var parts = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var len = Math.Min(PartChars, text.Length - start);
            if (start + len < text.Length && char.IsHighSurrogate(text[start + len - 1]))
                len--;
            parts.Add(text.Substring(start, len));
            start += len;
        }
        return parts;
    }

    private static SmsPart Build(string number, string text, bool concatenated, byte reference, int total, int sequence)
    {
        var digits = new string(number.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
            throw new ArgumentException("Phone number has no digits", nameof(number));
        var international = number.TrimStart().StartsWith('+');

        var tpdu = new List<byte>
        {
            (byte)(concatenated ? 0x41 : 0x01),   // SUBMIT, UDH present when concatenated
            0x00,                                 // message reference: the modem assigns it
            (byte)digits.Length,
            (byte)(international ? 0x91 : 0x81),
        };
        tpdu.AddRange(SwappedNibbles(digits));
        tpdu.Add(0x00); // PID
        tpdu.Add(0x08); // DCS: UCS-2

        var body = Encoding.BigEndianUnicode.GetBytes(text);
        var udh = concatenated ? new byte[] { 0x05, 0x00, 0x03, reference, (byte)total, (byte)sequence } : [];
        tpdu.Add((byte)(udh.Length + body.Length)); // UDL in octets for UCS-2
        tpdu.AddRange(udh);
        tpdu.AddRange(body);

        var hex = "00" + Convert.ToHexString(tpdu.ToArray()); // 00: use the SMSC stored on the SIM
        return new SmsPart(hex, tpdu.Count);
    }

    private static IEnumerable<byte> SwappedNibbles(string digits)
    {
        if (digits.Length % 2 == 1)
            digits += "F";
        for (var i = 0; i < digits.Length; i += 2)
            yield return byte.Parse($"{digits[i + 1]}{digits[i]}", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
