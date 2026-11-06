using System.Globalization;

namespace Hive.Modules.IpCams;

/// <summary>
/// One part of a Dahua event stream (eventManager.cgi?action=attach): <c>Code=VideoMotion;action=Start;index=0</c>,
/// or a heartbeat (<see cref="IsHeartbeat"/>). <see cref="Index"/> is the 0-based channel as the NVR sends it.
/// </summary>
public sealed record DahuaEvent(string Code, string? Action, int? Index)
{
    public const string Heartbeat = "Heartbeat";
    public const string VideoMotion = "VideoMotion";
    public const string VideoLoss = "VideoLoss";

    public bool IsHeartbeat => Code == Heartbeat;
    public bool IsStart => string.Equals(Action, "Start", StringComparison.OrdinalIgnoreCase);
    public bool IsStop => string.Equals(Action, "Stop", StringComparison.OrdinalIgnoreCase);

    /// <summary>The 1-based channel of snapshot.cgi and the NVR's screen, or null when the event has none.</summary>
    public int? Channel => Index + 1;
}

/// <summary>
/// Reads a Dahua event stream line by line (contracts/ipcam.md). The stream is multipart (<c>--myboundary</c>, part
/// headers, a blank line, the body); only body lines that are an event or a heartbeat mean anything, the rest
/// (boundaries, headers, the JSON of <c>data=</c> on newer firmware) are skipped.
/// </summary>
public static class DahuaEventParser
{
    public static DahuaEvent? ParseLine(string? line)
    {
        var text = line?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text == DahuaEvent.Heartbeat)
            return new DahuaEvent(DahuaEvent.Heartbeat, null, null);
        if (!text.StartsWith("Code=", StringComparison.Ordinal))
            return null;

        string? code = null, action = null;
        int? index = null;
        foreach (var part in text.Split(';'))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                continue;
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            // data= carries JSON that may itself hold ';' and '=': nothing after it is ours.
            if (key.Equals("data", StringComparison.OrdinalIgnoreCase))
                break;
            if (key.Equals("Code", StringComparison.OrdinalIgnoreCase))
                code = value;
            else if (key.Equals("action", StringComparison.OrdinalIgnoreCase))
                action = value;
            else if (key.Equals("index", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0)
                index = i;
        }
        return string.IsNullOrEmpty(code) ? null : new DahuaEvent(code, action, index);
    }

    /// <summary>Every event and heartbeat of a whole stream text, in order.</summary>
    public static IReadOnlyList<DahuaEvent> ParseAll(string stream) =>
        stream.Split('\n').Select(ParseLine).OfType<DahuaEvent>().ToList();
}
