using System.Globalization;
using Hive.Contracts.Messages;
using Hive.Modules.Weather;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>Thin line pictures of the weather, by the WMO code Open-Meteo gives: sun, clouds, rain, snow, thunder, fog.</summary>
public static class WeatherIcons
{
    public enum Kind { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Thunder }

    public static Kind Of(int code) => code switch
    {
        0 or 1 => Kind.Clear,
        2 => Kind.PartlyCloudy,
        3 => Kind.Cloudy,
        45 or 48 => Kind.Fog,
        >= 51 and <= 57 => Kind.Drizzle,
        >= 61 and <= 67 or >= 80 and <= 82 => Kind.Rain,
        >= 71 and <= 77 or 85 or 86 => Kind.Snow,
        >= 95 => Kind.Thunder,
        _ => Kind.Cloudy,
    };

    private static SKPaint Stroke(float size) => new() { Color = SKColors.Black, IsAntialias = false, StrokeWidth = Math.Max(1.5f, size / 22), Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };

    /// <summary>The picture for a code in a square of <paramref name="size"/> pixels at x, y.</summary>
    public static void Draw(SKCanvas canvas, int code, float x, float y, float size)
    {
        var kind = Of(code);
        switch (kind)
        {
            case Kind.Clear:
                Sun(canvas, x + size / 2, y + size / 2, size * 0.2f, size);
                return;
            case Kind.PartlyCloudy:
                Sun(canvas, x + size * 0.66f, y + size * 0.32f, size * 0.15f, size);
                break;
        }
        Cloud(canvas, x, y + (kind == Kind.Cloudy || kind == Kind.PartlyCloudy ? size * 0.15f : 0), size);
        var below = y + size * 0.72f;
        switch (kind)
        {
            case Kind.Fog:
                using (var line = Stroke(size))
                    for (var i = 0; i < 2; i++)
                        canvas.DrawLine(x + size * 0.15f, below + i * size * 0.12f, x + size * 0.85f, below + i * size * 0.12f, line);
                break;
            case Kind.Drizzle:
            case Kind.Rain:
                var count = kind == Kind.Rain ? 3 : 2;
                for (var i = 0; i < count; i++)
                    Drop(canvas, x + size * (0.3f + 0.2f * i + (count == 2 ? 0.1f : 0)), below + (i % 2) * size * 0.08f, size * 0.07f);
                break;
            case Kind.Snow:
                for (var i = 0; i < 3; i++)
                    Flake(canvas, x + size * (0.3f + 0.2f * i), below + (i % 2) * size * 0.1f + size * 0.05f, size * 0.07f);
                break;
            case Kind.Thunder:
                using (var bolt = new SKPath())
                {
                    bolt.MoveTo(x + size * 0.52f, below - size * 0.02f);
                    bolt.LineTo(x + size * 0.42f, below + size * 0.14f);
                    bolt.LineTo(x + size * 0.52f, below + size * 0.14f);
                    bolt.LineTo(x + size * 0.44f, below + size * 0.28f);
                    using var line = Stroke(size);
                    canvas.DrawPath(bolt, line);
                }
                break;
        }
    }

    private static void Sun(SKCanvas canvas, float cx, float cy, float r, float size)
    {
        using var line = Stroke(size);
        canvas.DrawCircle(cx, cy, r, line);
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
            canvas.DrawLine(cx + dx * r * 1.45f, cy + dy * r * 1.45f, cx + dx * r * 1.95f, cy + dy * r * 1.95f, line);
        }
    }

    /// <summary>A cloud: three bumps on a flat bottom, white inside (it covers a sun behind it).</summary>
    public static void Cloud(SKCanvas canvas, float x, float y, float size)
    {
        using var a = new SKPath();
        a.AddCircle(x + size * 0.32f, y + size * 0.52f, size * 0.16f);
        using var b = new SKPath();
        b.AddCircle(x + size * 0.52f, y + size * 0.4f, size * 0.2f);
        using var d = new SKPath();
        d.AddCircle(x + size * 0.72f, y + size * 0.52f, size * 0.15f);
        using var e = new SKPath();
        e.AddRect(new SKRect(x + size * 0.32f, y + size * 0.45f, x + size * 0.72f, y + size * 0.67f));
        using var ab = a.Op(b, SKPathOp.Union)!;
        using var abd = ab.Op(d, SKPathOp.Union)!;
        using var cloud = abd.Op(e, SKPathOp.Union)!;
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = false, Style = SKPaintStyle.Fill };
        canvas.DrawPath(cloud, white);
        using var line = Stroke(size);
        canvas.DrawPath(cloud, line);
    }

    /// <summary>The weather in words (Russian), by its WMO code.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Ясно",
        1 => "Почти ясно",
        2 => "Переменная облачность",
        3 => "Пасмурно",
        45 or 48 => "Туман",
        51 or 53 or 55 => "Морось",
        56 or 57 => "Ледяная морось",
        61 => "Небольшой дождь",
        63 => "Дождь",
        65 => "Сильный дождь",
        66 or 67 => "Ледяной дождь",
        71 => "Небольшой снег",
        73 => "Снег",
        75 => "Сильный снег",
        77 => "Снежная крупа",
        80 => "Небольшие ливни",
        81 => "Ливни",
        82 => "Сильные ливни",
        85 or 86 => "Снегопад",
        95 => "Гроза",
        96 or 99 => "Гроза с градом",
        _ => "",
    };

    // The thermometer drawn on a 512 square (viewBox of the vector the person chose, the sun left out): the tube with its bulb, the
    // mercury in it, four marks to the right. Its ink spans x 171..420.5 and y 86.6..445.5.
    private const string ThermometerTube =
        "M317.38,279.33a6.83,6.83,0,0,1-2.94-5.53V137.61a51,51,0,1,0-102,0V273.8a6.46,6.46,0,0,1-2.6,5.28A91.92,91.92,0,0,0,262.07,445.5h1.37a91.84,91.84,0,0,0,91.83-91.85A92.18,92.18,0,0,0,317.38,279.33Z" +
        "m-2.12,126.93a73.85,73.85,0,1,1-94.92-112.57,24.53,24.53,0,0,0,10.08-19.89V137.61a33,33,0,1,1,66,0V273.8a25,25,0,0,0,10.36,20.09,74.15,74.15,0,0,1,30.47,59.77A73.29,73.29,0,0,1,315.26,406.26Z";
    private const string ThermometerMercury = "M275.43,307.31V202.62h-24V307.31a48.12,48.12,0,1,0,24,0Z";
    private static readonly (float X, float Y, float Width)[] ThermometerMarks = [(343.62f, 105.39f, 47.67f), (343.62f, 146.96f, 76.87f), (343.62f, 188.53f, 47.67f), (343.62f, 230.09f, 76.87f)];
    private const float ThermometerLeft = 171f, ThermometerTop = 86.6f, ThermometerHeight = 358.9f, ThermometerWidth = 249.5f;

    /// <summary>The width of <see cref="Thermometer"/> when it is <paramref name="height"/> tall.</summary>
    public static float ThermometerWidthAt(float height) => ThermometerWidth * height / ThermometerHeight;

    /// <summary>A thermometer <paramref name="height"/> tall at x, y: a tube with its bulb, the mercury and marks beside it.</summary>
    public static void Thermometer(SKCanvas canvas, float x, float y, float height)
    {
        var scale = height / ThermometerHeight;
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(scale);
        canvas.Translate(-ThermometerLeft, -ThermometerTop);
        foreach (var data in new[] { ThermometerTube, ThermometerMercury })
        {
            using var path = SKPath.ParseSvgPathData(data);
            canvas.DrawPath(path, ink);
        }
        foreach (var (mx, my, mw) in ThermometerMarks)
            canvas.DrawRect(new SKRect(mx, my, mx + mw, my + 16), ink);
        canvas.Restore();
    }

    /// <summary>
    /// Half a sun on the horizon (three lines under it), <paramref name="width"/> wide at x, y: rising drawn in outline,
    /// setting filled.
    /// </summary>
    public static void SunOnHorizon(SKCanvas canvas, float x, float y, float width, bool rising)
    {
        var size = width;
        using var line = new SKPaint
        {
            Color = SKColors.Black, IsAntialias = false, StrokeWidth = Math.Max(1.5f, size / 20), Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round,
        };
        float cx = x + size / 2, horizon = y + size * 0.5f, r = size * 0.2f;
        using (var half = new SKPath())
        {
            half.AddArc(new SKRect(cx - r, horizon - r, cx + r, horizon + r), 180, 180);
            if (rising)
                canvas.DrawPath(half, line);
            else
            {
                half.Close();
                using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
                canvas.DrawPath(half, ink);
            }
        }
        for (var i = 0; i < 5; i++)
        {
            var a = Math.PI + i * Math.PI / 4;
            float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
            canvas.DrawLine(cx + dx * r * 1.4f, horizon + dy * r * 1.4f, cx + dx * r * 1.9f, horizon + dy * r * 1.9f, line);
        }
        canvas.DrawLine(x, horizon + 1, x + size, horizon + 1, line);
        canvas.DrawLine(x + size * 0.15f, horizon + size * 0.13f, x + size * 0.85f, horizon + size * 0.13f, line);
        canvas.DrawLine(x + size * 0.3f, horizon + size * 0.25f, x + size * 0.7f, horizon + size * 0.25f, line);
    }

    /// <summary>A drop of radius <paramref name="r"/>, its round bottom at x, y.</summary>
    public static void Drop(SKCanvas canvas, float x, float y, float r) => Tear(canvas, x, y - r * 2.4f, x, y, r);

    /// <summary>A teardrop: its point at (tx, ty), its round end a circle of <paramref name="r"/> around (cx, cy).</summary>
    public static void Tear(SKCanvas canvas, float tx, float ty, float cx, float cy, float r)
    {
        // the two lines from the point touch the circle where they meet its radius at a right angle
        float dx = tx - cx, dy = ty - cy;
        var d = MathF.Sqrt(dx * dx + dy * dy);
        var toPoint = MathF.Atan2(dy, dx);
        var spread = MathF.Acos(Math.Clamp(r / d, -1, 1));
        var a1 = toPoint + spread;
        using var path = new SKPath();
        path.MoveTo(tx, ty);
        path.LineTo(cx + r * MathF.Cos(a1), cy + r * MathF.Sin(a1));
        // around the far side of the circle, from one touching point to the other
        var sweep = 360f - 2 * spread * 180 / MathF.PI;
        path.ArcTo(new SKRect(cx - r, cy - r, cx + r, cy + r), a1 * 180 / MathF.PI, sweep, false);
        path.LineTo(tx, ty);
        path.Close();
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
        canvas.DrawPath(path, ink);
    }

    /// <summary>Two upright drops apart from each other, the second lower (the rain row), in a box <paramref name="size"/> tall at x, y.</summary>
    public static void Drops(SKCanvas canvas, float x, float y, float size)
    {
        var r = size * 0.17f;
        Drop(canvas, x + size * 0.2f, y + size * 0.5f, r);
        Drop(canvas, x + size * 0.55f, y + size * 0.83f, r);
    }

    private static void Flake(SKCanvas canvas, float cx, float cy, float r)
    {
        using var line = new SKPaint { Color = SKColors.Black, IsAntialias = false, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };
        for (var i = 0; i < 3; i++)
        {
            var a = i * Math.PI / 3;
            float dx = (float)Math.Cos(a) * r, dy = (float)Math.Sin(a) * r;
            canvas.DrawLine(cx - dx, cy - dy, cx + dx, cy + dy, line);
        }
    }

    /// <summary>
    /// The wind: three strokes blowing to the right, the upper one curling up at its end, the lower one curling down, the
    /// middle one the longest; in a square of <paramref name="size"/> at x, y.
    /// </summary>
    public static void Wind(SKCanvas canvas, float x, float y, float size)
    {
        using var line = new SKPaint
        {
            Color = SKColors.Black, IsAntialias = false, StrokeWidth = Math.Max(1.5f, size / 9), Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round,
        };
        float S(float f) => f * size;
        var r = S(0.13f);
        // upper: a dash, then a stroke that curls up and back over itself
        canvas.DrawLine(x + S(0.04f), y + S(0.38f), x + S(0.14f), y + S(0.38f), line);
        using (var upper = new SKPath())
        {
            upper.MoveTo(x + S(0.26f), y + S(0.38f));
            upper.LineTo(x + S(0.66f), y + S(0.38f));
            upper.ArcTo(new SKRect(x + S(0.66f) - r, y + S(0.38f) - 2 * r, x + S(0.66f) + r, y + S(0.38f)), 90, -270, false);
            canvas.DrawPath(upper, line);
        }
        // middle: the longest
        canvas.DrawLine(x + S(0.04f), y + S(0.56f), x + S(0.86f), y + S(0.56f), line);
        // lower: a stroke that curls down
        using (var lower = new SKPath())
        {
            lower.MoveTo(x + S(0.30f), y + S(0.74f));
            lower.LineTo(x + S(0.60f), y + S(0.74f));
            lower.ArcTo(new SKRect(x + S(0.60f) - r, y + S(0.74f), x + S(0.60f) + r, y + S(0.74f) + 2 * r), -90, 270, false);
            canvas.DrawPath(lower, line);
        }
    }
}
