using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hive.Contracts.Messages;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Telemetry;
using Hive.Modules.Weather;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>Screens of the boards the hive knows, by board (info hw).</summary>
public static class DisplayScreens
{
    private static readonly Dictionary<string, DisplayScreenDto> Boards = new()
    {
        ["esp32c3-oled-042"] = new(72, 40),
        // Waveshare ESP32-S3-ePaper-1.54 (V2): 200x200 black and white, firmware monitor-epd154
        ["waveshare-s3-epaper-154"] = new(200, 200, Rendered: true),
        // Elecrow CrowPanel ESP32 3.7" (UC8253, 240x416, rocker with a press): three tabs, firmware monitor-crowpanel37
        ["crowpanel-epd37"] = new(240, 416, Rendered: true, Tabs: 3),
    };

    /// <summary>How a board stands when the person did not choose: the CrowPanel lies landscape.</summary>
    private static readonly Dictionary<string, int> DefaultRotations = new() { ["crowpanel-epd37"] = 270 };

    public static int DefaultRotation(string? board) => board is not null && DefaultRotations.TryGetValue(board, out var r) ? r : 0;

    /// <summary>The panel's own screen.</summary>
    public static DisplayScreenDto? For(string? board) => board is not null && Boards.TryGetValue(board, out var s) ? s : null;

    /// <summary>The screen as the person sees it: a panel turned a quarter is as wide as it is tall and as tall as it is wide.</summary>
    public static DisplayScreenDto? For(string? board, int rotation) =>
        For(board) is { } s && rotation is 90 or 270 ? s with { Width = s.Height, Height = s.Width } : For(board);

    public static readonly int[] Rotations = [0, 90, 180, 270];

    /// <summary>Boards whose frame the hive draws: what a monitor far away can be.</summary>
    public static IReadOnlyList<string> RenderedBoards => Boards.Where(b => b.Value.Rendered).Select(b => b.Key).ToList();
}
