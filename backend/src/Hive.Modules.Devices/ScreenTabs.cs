using System.Globalization;
using Hive.Contracts.Messages;
using Hive.Modules.Weather;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>
/// The tabs of a panel with buttons (contracts/monitor-http.md, tabs): Weather (now and the next days), Measure (the person's
/// own layout) and Calendar (the month and the hive's recent events). Each ends in the tab bar the panel finds by itself: the
/// bottom <see cref="BarHeight"/> pixels, three equal cells, the shown tab inverted. The panel may stand either way (the
/// person's rotation): the tabs lay themselves out in a landscape or a portrait screen.
/// </summary>
public static partial class ScreenTabs
{
    public const int Forecast = 0, Measure = 1, Calendar = 2;
    public const int BarHeight = 22;
    public static readonly string[] Names = ["Погода", "Измерения", "Календарь"];

    /// <summary>
    /// How wide the cells of the tab bar are, relative to each other: Weather is the shortest word and gets the least room.
    /// The panel learns it from X-Frame-Tab-Weights, since it inverts the cell under its cursor itself.
    /// </summary>
    public static readonly int[] TabWeights = [2, 3, 3];
    public static string TabWeightsHeader => string.Join(',', TabWeights);

    /// <summary>The left edges of the cells and the right edge of the last one, in a bar <paramref name="width"/> pixels wide.</summary>
    public static int[] CellEdges(int width)
    {
        var edges = new int[TabWeights.Length + 1];
        var total = TabWeights.Sum();
        var sum = 0;
        for (var i = 0; i < TabWeights.Length; i++)
        {
            edges[i] = width * sum / total;
            sum += TabWeights[i];
        }
        edges[^1] = width;
        return edges;
    }

    /// <summary>
    /// The families the tabs are written in: Roboto for text (bold for what stands out, light for scales), Roboto Serif for
    /// the bigger numbers, a pixel font for the 8 px small print.
    /// </summary>
    public const string TextFamily = "roboto";
    public const string LightFamily = "roboto-light";
    public const string SerifFamily = "roboto-serif";
    public const string PixelFamily = "public-pixel";
    /// <summary>The book face of the weather in words and its details: Noto Serif, the heir of Droid Serif with Cyrillic.</summary>
    public const string BookFamily = "noto-serif";
    public static readonly string[] Families = [TextFamily, LightFamily, SerifFamily, PixelFamily, BookFamily];

    // Written out: the hive's image may run without culture data.
    private static readonly string[] Weekdays = ["понедельник", "вторник", "среда", "четверг", "пятница", "суббота", "воскресенье"];
    private static readonly string[] Months = ["Январь", "Февраль", "Март", "Апрель", "Май", "Июнь", "Июль", "Август", "Сентябрь", "Октябрь", "Ноябрь", "Декабрь"];
    private static readonly string[] MonthsOf = ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];

    /// <summary>Monday first: 0 for Monday, 6 for Sunday.</summary>
    public static int DayIndex(DateTimeOffset day) => ((int)day.DayOfWeek + 6) % 7;

    /// <summary>
    /// A one-cell lithium battery from empty (3.30 V) to full, along its curve: most of the charge sits around 3.7-4.0 V. A cell on its charger
    /// reads 4.17-4.20 V (the charge ends at 4.20 V and the ADC is not exact), so 4.18 V and more is 100. The firmware's batteryPercent
    /// (RemoteMonitor.h) has the same curve.
    /// </summary>
    public static int BatteryPercent(double volts)
    {
        (double V, int Pct)[] curve = [(3.30, 0), (3.50, 10), (3.60, 25), (3.70, 50), (3.80, 65), (3.90, 77), (4.00, 85), (4.10, 90), (4.15, 97), (4.18, 100)];
        if (volts <= curve[0].V)
            return 0;
        for (var i = 1; i < curve.Length; i++)
            if (volts <= curve[i].V)
            {
                var (v0, p0) = curve[i - 1];
                var (v1, p1) = curve[i];
                return (int)Math.Round(p0 + (p1 - p0) * (volts - v0) / (v1 - v0));
            }
        return 100;
    }

    /// <summary>The scale of the temperature graph: lines every 5 degrees (2 when the days are even), at least two of them.</summary>
    public static (int Low, int High, int Step) Scale(double min, double max)
    {
        var step = max - min > 8 ? 5 : 2;
        var low = (int)Math.Floor(min / step) * step;
        var high = (int)Math.Ceiling(max / step) * step;
        if (high == low)
            high = low + step;
        return (low, high, step);
    }

    /// <summary>One tab as the person sees it (turned): the background and items for Measure, the hive's drawing for the others.</summary>
    public static SKBitmap Draw(int tab, DisplayScreenDto screen, TabContext context, FontSet fonts, DisplayMessage items, SKBitmap? background,
        IReadOnlyDictionary<int, SKBitmap>? images = null)
    {
        var bitmap = tab == Measure
            ? DisplayRender.Render(items, screen, fonts, background, images)
            : new SKBitmap(new SKImageInfo(screen.Width, screen.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        if (tab != Measure)
            canvas.Clear(SKColors.White);
        var pen = new Pen(canvas, fonts, screen.Width, screen.Height - BarHeight) { Asleep = context.Asleep };
        switch (tab)
        {
            case Measure: Stamp(pen, context.Local(context.Now)); break;
            case Forecast: DrawForecast(pen, context); break;
            case Calendar: DrawCalendar(pen, context); break;
        }
        DrawBattery(pen, context.Battery, context.Charging);
        DrawBar(pen, tab, screen.Height);
        return bitmap;
    }

    /// <summary>When the picture was drawn, in the pixel font at the bottom right (the same on Weather and Measure).</summary>
    private static void Stamp(Pen p, DateTimeOffset now) =>
        p.Text(p.Asleep ? "спит" : "обновлено " + now.ToString("HH:mm", CultureInfo.InvariantCulture), p.W - 4, p.H - 10, 8, "right", p.Pixel);

    /// <summary>Drawing tools of one tab: ink, white ink, the text faces and the area above the bar.</summary>
    private sealed class Pen
    {
        public Pen(SKCanvas canvas, FontSet fonts, int width, int height)
        {
            Canvas = canvas;
            W = width;
            H = height;
            Regular = fonts.Resolve(TextFamily, false, false);
            Bold = fonts.Resolve(TextFamily, true, false);
            Light = fonts.Resolve(LightFamily, false, false);
            LightBold = fonts.Resolve(LightFamily, true, false);
            Serif = fonts.Resolve(SerifFamily, false, false);
            Pixel = fonts.Resolve(PixelFamily, false, false);
            Book = fonts.Resolve(BookFamily, false, false);
            BookItalic = fonts.Resolve(BookFamily, false, true);
            Fallback = fonts.Fallback;
        }

        public SKCanvas Canvas { get; }
        public int W { get; }
        public int H { get; }
        /// <summary>The panel is put to sleep: the stamp says so instead of when the picture was drawn.</summary>
        public bool Asleep { get; init; }
        public bool Portrait => W < H;
        public FontFace Regular { get; }
        public FontFace Bold { get; }
        public FontFace Light { get; }
        /// <summary>Roboto Light has no bold of its own: this one is the light face made heavier.</summary>
        public FontFace LightBold { get; }
        public FontFace Serif { get; }
        public FontFace Pixel { get; }
        public FontFace Book { get; }
        public FontFace BookItalic { get; }
        public FontFace Fallback { get; }
        public SKPaint Ink { get; } = new() { Color = SKColors.Black, IsAntialias = false, StrokeWidth = 1, Style = SKPaintStyle.Fill };
        public SKPaint Paper { get; } = new() { Color = SKColors.White, IsAntialias = false, Style = SKPaintStyle.Fill };
        public SKPaint Line(float width = 1) => new() { Color = SKColors.Black, IsAntialias = false, StrokeWidth = width, Style = SKPaintStyle.Stroke };

        /// <summary>Text of <paramref name="px"/> pixels, its top at <paramref name="top"/>; returns the height of its capitals.</summary>
        public float Text(string text, float x, float top, float px, string? align = null, FontFace? face = null, bool white = false) =>
            DisplayRender.Text(Canvas, white ? Paper : Ink, text, x, top, face ?? Regular, Fallback, px, false, align);

        public float Width(string text, float px, FontFace? face = null)
        {
            using var font = DisplayRender.MakeFont(face ?? Regular, px);
            return font.MeasureText(text);
        }

        /// <summary>Grey on a black-and-white screen: every other pixel.</summary>
        public void Grey(SKRect r)
        {
            for (var y = (int)r.Top; y < (int)r.Bottom; y++)
                for (var x = (int)r.Left; x < (int)r.Right; x++)
                    if ((x + y) % 2 == 0)
                        Canvas.DrawPoint(x, y, Ink);
        }

        public void Dotted(float x0, float y0, float x1, float y1)
        {
            using var dots = Line();
            dots.PathEffect = SKPathEffect.CreateDash([1, 3], 0);
            Canvas.DrawLine(x0, y0, x1, y1, dots);
        }
    }

    /// <summary>The tab bar: the cells of <see cref="TabWeights"/>, the shown one black; a name that does not fit its cell is written smaller.</summary>
    private static void DrawBar(Pen p, int tab, int height)
    {
        var top = height - BarHeight;
        using var line = p.Line();
        p.Canvas.DrawRect(new SKRect(0, top, p.W, height), p.Paper);
        p.Canvas.DrawLine(0, top, p.W, top, line);
        var edges = CellEdges(p.W);
        var px = 14f;
        while (px > 10 && Enumerable.Range(0, Names.Length).Any(i => p.Width(Names[i], px, p.LightBold) > edges[i + 1] - edges[i] - 6))
            px -= 1;
        for (var i = 0; i < Names.Length; i++)
        {
            float left = edges[i], right = edges[i + 1];
            if (i == tab)
                p.Canvas.DrawRect(new SKRect(left, top, right, height), p.Ink);
            else if (i > 0 && i != tab + 1)
                p.Canvas.DrawLine(left, top + 4, left, height - 4, line);
            var cap = CapOf(p.LightBold, px);
            p.Text(Names[i], (left + right) / 2, top + (BarHeight - cap) / 2 + 1, px, "center", p.LightBold, white: i == tab);
        }
    }

    private static float CapOf(FontFace face, float px)
    {
        using var font = DisplayRender.MakeFont(face, px);
        return font.Metrics.CapHeight > 0 ? font.Metrics.CapHeight : px * 0.7f;
    }

    /// <summary>The week as seven squares, Monday first: weekdays black, the weekend grey; today circled.</summary>
    private static void DrawWeek(Pen p, float x, float y, float size, float gap, DateTimeOffset today)
    {
        var index = DayIndex(today);
        for (var i = 0; i < 7; i++)
        {
            var r = new SKRect(x + i * (size + gap), y, x + i * (size + gap) + size, y + size);
            if (i < 5)
                p.Canvas.DrawRect(r, p.Ink);
            else
            {
                p.Grey(r);
                using var edge = p.Line();
                p.Canvas.DrawRect(r, edge);
            }
            if (i != index)
                continue;
            using var ring = p.Line(2);
            p.Canvas.DrawCircle(r.MidX, r.MidY, size * 0.85f, ring);
        }
    }

    private static string Fit(Pen p, string text, float width, float px, FontFace? face = null)
    {
        if (p.Width(text, px, face) <= width)
            return text;
        while (text.Length > 1 && p.Width(text + "…", px, face) > width)
            text = text[..^1];
        return text + "…";
    }
}
