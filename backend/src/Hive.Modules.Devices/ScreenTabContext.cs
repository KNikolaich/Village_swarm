using System.Globalization;
using Hive.Contracts.Messages;
using Hive.Modules.Weather;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>A recent event of the hive for the calendar tab: when, what, where.</summary>
public sealed record TabEvent(DateTimeOffset At, string What, string? Where);

/// <summary>
/// What the tabs show besides the person's own items. <see cref="Now"/> and the weather times are turned into the hive's time
/// zone by <see cref="Local"/>; <see cref="Battery"/> is percent, null when the panel cannot measure it.
/// </summary>
public sealed record TabContext(
    DateTimeOffset Now, Func<DateTimeOffset, DateTimeOffset> Local, int? Battery, WeatherForecast? Weather, IReadOnlyList<TabEvent> Events, bool Asleep = false, bool Charging = false);
