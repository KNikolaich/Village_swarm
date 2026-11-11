using System.Globalization;
using Hive.Contracts.Messages;
using Hive.Modules.Weather;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>
/// Where the battery icon stands on a screen without tabs (config display.battery): <see cref="X"/> and <see cref="Y"/> are its top left corner,
/// <see cref="Scale"/> a whole number of times the base 10 by 18 points; all optional (the top right corner at the base size), and
/// <see cref="Enabled"/> false takes it away.
/// </summary>
public sealed record BatteryLayout(bool? Enabled = null, int? X = null, int? Y = null, int? Scale = null)
{
    public const int BaseWidth = 10, BaseHeight = 18, MaxScale = 4;
}
