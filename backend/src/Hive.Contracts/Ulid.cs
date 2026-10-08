using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Hive.Contracts;

/// <summary>
/// ULID generator (https://github.com/ulid/spec): 48-bit unix ms timestamp + 80 random bits,
/// Crockford base32, 26 chars. Message ids and command ids in the MQTT contract are ULIDs.
/// </summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New() => New(DateTimeOffset.UtcNow);

    public static string New(DateTimeOffset time)
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = time.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)(ms & 0xFF);
            ms >>= 8;
        }
        RandomNumberGenerator.Fill(bytes[6..]);
        return Encode(bytes);
    }

    /// <summary>Unix ms timestamp encoded in the first 10 chars.</summary>
    public static long Timestamp(string ulid)
    {
        long ms = 0;
        for (var i = 0; i < 10; i++)
            ms = (ms << 5) | (uint)Alphabet.IndexOf(ulid[i]);
        return ms;
    }

    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        // 128 bits -> 26 base32 chars; the first char carries only the top 3 bits.
        var value = BinaryPrimitives.ReadUInt128BigEndian(bytes);
        Span<char> chars = stackalloc char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 0x1F)];
            value >>= 5;
        }
        return new string(chars);
    }
}
