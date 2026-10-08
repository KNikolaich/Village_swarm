using System.Globalization;
using System.Text;

namespace Hive.Infrastructure;

/// <summary>One page of a newest-first list; pass <see cref="NextCursor"/> back as <c>cursor</c> for the next page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Keyset cursor over (ts desc, id desc): stable while new rows keep arriving at the top.</summary>
public readonly record struct TsCursor(DateTimeOffset Ts, string Id)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public string Encode() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Ts.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{Id}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static TsCursor? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return null;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|', 2);
            return new TsCursor(new DateTimeOffset(long.Parse(parts[0], CultureInfo.InvariantCulture), TimeSpan.Zero), parts[1]);
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
}
