namespace Hive.Infrastructure;

/// <summary>
/// Reads deploy/.env (KEY=VALUE lines, # comments) for local development, so secrets such as the bot token
/// live in the same git-ignored file as on the hive. Docker passes the same file with env_file in production.
/// </summary>
public static class DotEnv
{
    /// <summary>Configuration pairs: TELEGRAM__TOKEN becomes Telegram:Token, like environment variables.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> Read(string path)
    {
        if (!File.Exists(path))
            yield break;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim().Replace("__", ":", StringComparison.Ordinal);
            var value = line[(eq + 1)..].Trim().Trim('"');
            yield return new(key, value);
        }
    }
}
