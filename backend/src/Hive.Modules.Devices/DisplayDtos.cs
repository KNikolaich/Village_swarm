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

/// <summary>
/// One item of a screen: a reading of this hornet (<see cref="Device"/> absent) or of another device, a caption the person
/// typed (<see cref="Text"/>, then <see cref="Metric"/> is empty), or the hive's clock (<see cref="Clock"/> time or date, written
/// as <see cref="Format"/> says, then <see cref="Metric"/> is empty too). With <see cref="Size"/> the item is placed: <see cref="X"/>,
/// <see cref="Y"/> in pixels, <see cref="Align"/> left (absent), center or right. On screens the hive draws, <see cref="Font"/>
/// (a family id), <see cref="Px"/> (6-120), <see cref="Bold"/> and <see cref="Italic"/> say how it is written.
/// <see cref="State"/>: <see cref="Metric"/> is a state of the device (a relay, a switch) and not a reading; it is shown as
/// <see cref="OnText"/> / <see cref="OffText"/> ("вкл" / "выкл" when absent), or, on a screen the hive draws and with the items
/// placed, as the picture <see cref="OnImage"/> / <see cref="OffImage"/> (ids from display/images, at most 100x100 pixels).
/// </summary>
public sealed record DisplayItemDto(string? Device, string Metric, string? Label, int? X = null, int? Y = null, int? Size = null, string? Align = null,
    string? Text = null, string? Font = null, int? Px = null, bool? Bold = null, bool? Italic = null, string? Clock = null, string? Format = null,
    bool? State = null, string? OnText = null, string? OffText = null, string? OnImage = null, string? OffImage = null);

/// <summary>A picture kept for a state item (display/images): its id and size.</summary>
public sealed record DisplayImageDto(string Id, int Width, int Height);

/// <summary>
/// Size of a screen in pixels as the person sees it (turned by the rotation): where items can be placed and what size a background
/// image must be. <see cref="Rendered"/>: the hive draws the whole frame (e-ink panels), so a preview from the hive is exactly
/// what the screen shows. <see cref="Background"/>: an image lies behind the items. <see cref="Tabs"/>: screens the panel flips
/// through with its buttons (contracts/monitor-http.md, tabs); the items are the last one, above the tab bar.
/// </summary>
public sealed record DisplayScreenDto(int Width, int Height, bool Rendered = false, bool Background = false, int Tabs = 0, bool Sleep = false);

/// <summary>
/// What the screen shows; <see cref="Screen"/> is filled by the hive for a board it knows (ignored when saving).
/// <see cref="Rotation"/> turns the picture of a screen the hive draws (0, 90, 180, 270); <see cref="Invert"/> swaps its black and
/// white (a dark screen); either absent when saving: unchanged.
/// </summary>
public sealed record DisplayItemsDto(
    IReadOnlyList<DisplayItemDto> Items, DisplayScreenDto? Screen = null, int? Rotation = null, DisplayMonitorDto? Monitor = null, bool? Invert = null,
    BatteryLayout? Battery = null);

/// <summary>
/// Monitoring of a panel far away: until <see cref="Until"/> it reads its screen every <see cref="EveryMin"/> minutes
/// (X-Frame-Refresh, contracts/monitor-http.md), then goes back to its own interval by itself.
/// </summary>
public sealed record DisplayMonitorDto(DateTimeOffset Until, int EveryMin);

/// <summary>How long and how often a panel is monitored (hours 0.5-12, every 1-10 minutes).</summary>
public sealed record DisplayMonitorRequest(double Hours, int EveryMin);
