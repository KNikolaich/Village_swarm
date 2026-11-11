using System.Globalization;
using Hive.Contracts.Messages;
using Hive.Modules.Weather;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>The battery icon: on the tabs and in the corner of a screen without tabs.</summary>
public static partial class ScreenTabs
{
    /// <summary>
    /// The battery icon of a screen that has no tabs (the small 1.54" panel), drawn with the same hand as on the tabs, over what the picture
    /// holds, where the person's layout says (<see cref="BatteryLayout"/>: the top right corner at the base size by default). Shown in steps of
    /// five percent, so a panel's e-ink is not redrawn for every percent that goes. Nothing when the layout turns it off.
    /// </summary>
    public static void DrawBatteryCorner(SKBitmap bitmap, FontSet fonts, int? percent, bool charging = false, BatteryLayout? layout = null)
    {
        if (percent is not { } pct || layout is { Enabled: false })
            return;
        var scale = Math.Clamp(layout?.Scale ?? 1, 1, BatteryLayout.MaxScale);
        var left = layout?.X ?? bitmap.Width - 6 * scale - BatteryLayout.BaseWidth * scale;
        var top = layout?.Y ?? 4;
        using var canvas = new SKCanvas(bitmap);
        DrawBatteryAt(new Pen(canvas, fonts, bitmap.Width, bitmap.Height), left, top, scale, CornerPercent(pct), charging, underneath: false);
    }

    /// <summary>The percent as the corner shows it: to the nearest five.</summary>
    public static int CornerPercent(int percent) => Math.Clamp((percent + 2) / 5 * 5, 0, 100);

    /// <summary>A small battery standing upright in the top right corner of a tab, the percent to its left. Nothing when unknown.</summary>
    private static void DrawBattery(Pen p, int? percent, bool charging = false)
    {
        if (percent is not { } pct)
            return;
        // standing, the date takes the whole top line: the percent goes under the battery
        DrawBatteryAt(p, p.W - 6 - BatteryLayout.BaseWidth, 4, 1, pct, charging, underneath: p.Portrait);
    }

    /// <summary>
    /// The icon with its top left at (<paramref name="left"/>, <paramref name="top"/>), every length <paramref name="scale"/> times the base ten
    /// columns by eighteen rows. Every part is a filled rectangle on whole pixels, so it is exactly symmetric left to right at every size: ten
    /// columns, a four-column nub over the middle ones, an empty column inside each edge. On its charger (<paramref name="charging"/>) a
    /// lightning bolt stands where the percent would, since the percent of a cell that is being charged says little.
    /// </summary>
    private static void DrawBatteryAt(Pen p, int left, int top, int scale, int pct, bool charging, bool underneath)
    {
        int s = scale, w = BatteryLayout.BaseWidth * s, h = BatteryLayout.BaseHeight * s;
        int right = left + w, bodyTop = top + 2 * s, bodyBottom = top + h;
        void Box(int x0, int y0, int x1, int y1) => p.Canvas.DrawRect(new SKRect(x0, y0, x1, y1), p.Ink);  // columns x0..x1-1, rows y0..y1-1
        Box(left + 3 * s, top, left + 7 * s, top + 2 * s);        // the nub: columns 3..6 of 0..9
        Box(left, bodyTop, right, bodyTop + s);                   // the shell: top, bottom, left, right
        Box(left, bodyBottom - s, right, bodyBottom);
        Box(left, bodyTop, left + s, bodyBottom);
        Box(right - s, bodyTop, right, bodyBottom);
        var room = bodyBottom - bodyTop - 4 * s;                  // between the empty rows next to the edges
        var fill = (int)Math.Round(room * Math.Clamp(pct, 0, 100) / 100.0);
        Box(left + 2 * s, bodyBottom - 2 * s - fill, right - 2 * s, bodyBottom - 2 * s);
        if (charging)
        {
            DrawBolt(p, left - 12 * s, top + 3 * s, s);
            return;
        }
        if (underneath)
            p.Text($"{pct}%", left + w / 2f, top + h + 3 * s, 9 * s, "center", p.Light);
        else
            p.Text($"{pct}%", left - 3 * s, top + 5 * s, 10 * s, "right", p.Light);
    }

    /// <summary>A lightning bolt 9 by 15 points (times <paramref name="scale"/>) with its top left at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private static void DrawBolt(Pen p, int x, int y, int scale)
    {
        using var path = new SKPath();
        (int X, int Y)[] points = [(5, 0), (0, 9), (4, 9), (3, 15), (9, 6), (5, 6)];
        path.MoveTo(x + points[0].X * scale, y + points[0].Y * scale);
        foreach (var (px, py) in points.Skip(1))
            path.LineTo(x + px * scale, y + py * scale);
        path.Close();
        p.Canvas.DrawPath(path, p.Ink);
    }
}
