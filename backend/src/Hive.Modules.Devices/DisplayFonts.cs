using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>A font family a person can pick for a screen the hive draws. <see cref="Styles"/>: regular, bold, italic, bolditalic that the family has.</summary>
public sealed record DisplayFontDto(string Id, string Name, bool Cyrillic, bool DigitsOnly, bool Builtin, IReadOnlyList<string> Styles);

/// <summary>A typeface to draw with; a face the family lacks is made from the one it has (emboldened, slanted).</summary>
public sealed record FontFace(SKTypeface Typeface, bool FakeBold, bool FakeItalic);

/// <summary>The families a screen's items use, loaded and ready for <see cref="DisplayRender"/>.</summary>
public sealed class FontSet(IReadOnlyDictionary<string, IReadOnlyDictionary<(bool Bold, bool Italic), SKTypeface>> families)
{
    public const string Standard = "standard";

    public static FontSet Empty { get; } = new(new Dictionary<string, IReadOnlyDictionary<(bool, bool), SKTypeface>>());

    /// <summary>Takes the face nearest to the style; an unknown family (deleted since) is the standard one.</summary>
    public FontFace Resolve(string? family, bool bold, bool italic)
    {
        var faces = family is not null && families.TryGetValue(family, out var f) ? f : DisplayFontCatalog.StandardFaces();
        return Pick(faces, bold, italic);
    }

    public FontFace Fallback => Pick(DisplayFontCatalog.StandardFaces(), true, false);

    public static FontFace Pick(IReadOnlyDictionary<(bool Bold, bool Italic), SKTypeface> faces, bool bold, bool italic)
    {
        if (faces.TryGetValue((bold, italic), out var exact))
            return new(exact, false, false);
        if (italic && faces.TryGetValue((bold, false), out var upright))
            return new(upright, false, true);
        if (bold && faces.TryGetValue((false, italic), out var regular))
            return new(regular, true, false);
        if (bold && italic && faces.TryGetValue((false, false), out var plain))
            return new(plain, true, true);
        // Only another style exists (a bold-only family asked for regular): the face it has.
        var any = faces.OrderByDescending(x => x.Key.Bold == bold).ThenByDescending(x => x.Key.Italic == italic).First();
        return new(any.Value, false, italic && !any.Key.Italic);
    }
}

/// <summary>
/// The fonts for screens the hive draws: a few families built into the hive (open licenses, Fonts/licenses) and the ones
/// people upload (TTF/OTF, kept in the database so the city's copy has them too).
/// </summary>
public sealed class DisplayFontCatalog(HiveDbContext db, TimeProvider time)
{
    public const int MaxUploads = 24;
    public const int MaxFontBytes = 4 * 1024 * 1024;

    private sealed record Builtin(string Id, string Name, bool Cyrillic, bool DigitsOnly, string? Regular, string? Bold, string? Italic, string? BoldItalic);

    // The fonts that were asked for and are not free to ship (Old English Text MT, Super Mario 3D World, Digital Numbers,
    // a_LCDNova) can be uploaded: they stay on this hive. Their free neighbours are here.
    private static readonly Builtin[] Builtins =
    [
        new(FontSet.Standard, "Стандартный (DejaVu Sans Condensed)", true, false, null, "DejaVuSansCondensed-Bold", null, null),
        new("anonymous-pro", "Anonymous Pro", true, false, "AnonymousPro-Regular", "AnonymousPro-Bold", "AnonymousPro-Italic", "AnonymousPro-BoldItalic"),
        new("cormorant-infant", "Cormorant Infant", true, false, "CormorantInfant-Regular", "CormorantInfant-Bold", "CormorantInfant-Italic", "CormorantInfant-BoldItalic"),
        new("dseg7", "DSEG7 Classic (цифры как на табло)", false, true, "DSEG7Classic-Regular", "DSEG7Classic-Bold", "DSEG7Classic-Italic", "DSEG7Classic-BoldItalic"),
        new("press-start", "Press Start 2P (пиксельный, как в играх)", true, false, "PressStart2P-Regular", null, null, null),
        new("unifraktur-cook", "UnifrakturCook (готический)", false, false, null, "UnifrakturCook-Bold", null, null),
    ];

    private static readonly ConcurrentDictionary<string, SKTypeface> Loaded = new();
    private static readonly Regex Slug = new("[^a-z0-9]+");

    private static SKTypeface Resource(string name) => Loaded.GetOrAdd("builtin:" + name, _ =>
    {
        using var stream = typeof(DisplayFontCatalog).Assembly.GetManifestResourceStream($"Hive.Modules.Devices.Fonts.{name}.ttf")
            ?? throw new InvalidOperationException($"the font {name} is not embedded");
        return SKTypeface.FromStream(stream) ?? throw new InvalidOperationException($"the font {name} cannot be read");
    });

    private static IReadOnlyDictionary<(bool Bold, bool Italic), SKTypeface> FacesOf(Builtin b)
    {
        var faces = new Dictionary<(bool, bool), SKTypeface>();
        if (b.Regular is not null) faces[(false, false)] = Resource(b.Regular);
        if (b.Bold is not null) faces[(true, false)] = Resource(b.Bold);
        if (b.Italic is not null) faces[(false, true)] = Resource(b.Italic);
        if (b.BoldItalic is not null) faces[(true, true)] = Resource(b.BoldItalic);
        return faces;
    }

    private static IEnumerable<(bool Bold, bool Italic)> AvailableOf(Builtin b)
    {
        if (b.Regular is not null) yield return (false, false);
        if (b.Bold is not null) yield return (true, false);
        if (b.Italic is not null) yield return (false, true);
        if (b.BoldItalic is not null) yield return (true, true);
    }

    public static IReadOnlyDictionary<(bool Bold, bool Italic), SKTypeface> StandardFaces() => FacesOf(Builtins[0]);

    private static string[] StylesOf(IEnumerable<(bool Bold, bool Italic)> faces) =>
        faces.OrderBy(f => f.Bold).ThenBy(f => f.Italic).Select(f => (f.Bold, f.Italic) switch { (false, false) => "regular", (true, false) => "bold", (false, true) => "italic", _ => "bolditalic" }).ToArray();

    public async Task<IReadOnlyList<DisplayFontDto>> ListAsync(CancellationToken ct)
    {
        var list = Builtins.Select(b => new DisplayFontDto(b.Id, b.Name, b.Cyrillic, b.DigitsOnly, true, StylesOf(AvailableOf(b)))).ToList();
        var custom = await db.DisplayFonts.AsNoTracking().Select(f => new { f.FamilyId, f.Family, f.Bold, f.Italic, f.Cyrillic, f.DigitsOnly }).ToListAsync(ct);
        list.AddRange(custom.GroupBy(f => f.FamilyId).OrderBy(g => g.First().Family, StringComparer.CurrentCultureIgnoreCase).Select(g =>
            new DisplayFontDto(g.Key, g.First().Family, g.All(f => f.Cyrillic), g.All(f => f.DigitsOnly), false, StylesOf(g.Select(f => (f.Bold, f.Italic))))));
        return list;
    }

    /// <summary>Changes when a font is added or removed: part of the picture's tag, so a panel redraws.</summary>
    public async Task<string> VersionAsync(CancellationToken ct)
    {
        var shas = await db.DisplayFonts.AsNoTracking().OrderBy(f => f.Id).Select(f => f.Id + f.Sha).ToListAsync(ct);
        return shas.Count == 0 ? "0" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', shas))))[..8];
    }

    /// <summary>The families these items use (and the standard one), ready to draw with.</summary>
    public async Task<FontSet> LoadAsync(IEnumerable<string?> families, CancellationToken ct)
    {
        var wanted = families.Where(f => !string.IsNullOrEmpty(f) && f != FontSet.Standard).Select(f => f!).Distinct().ToList();
        var set = new Dictionary<string, IReadOnlyDictionary<(bool, bool), SKTypeface>>();
        foreach (var id in wanted)
        {
            if (Builtins.FirstOrDefault(b => b.Id == id) is { } builtin)
            {
                set[id] = FacesOf(builtin);
                continue;
            }
            var rows = await db.DisplayFonts.AsNoTracking().Where(f => f.FamilyId == id).Select(f => new { f.Id, f.Bold, f.Italic, f.Sha }).ToListAsync(ct);
            var faces = new Dictionary<(bool, bool), SKTypeface>();
            foreach (var row in rows)
            {
                var key = $"custom:{row.Id}:{row.Sha}";
                if (!Loaded.TryGetValue(key, out var typeface))
                {
                    var data = await db.DisplayFonts.AsNoTracking().Where(f => f.Id == row.Id).Select(f => f.Data).FirstAsync(ct);
                    typeface = SKTypeface.FromData(SKData.CreateCopy(data));
                    if (typeface is null)
                        continue;
                    Loaded[key] = typeface;
                }
                faces[(row.Bold, row.Italic)] = typeface;
            }
            if (faces.Count > 0)
                set[id] = faces;
        }
        return new FontSet(set);
    }

    public enum AddStatus { Ok, Invalid, TooMany }

    /// <summary>
    /// A font file a person uploads: one face of a family. It must be readable and have digits; whether it has Russian
    /// letters is noted so the picker can say so. The same family and style again replaces the old face.
    /// </summary>
    public async Task<(AddStatus Status, DisplayFontDto? Font, string? Error)> AddAsync(byte[] data, string by, CancellationToken ct)
    {
        if (data.Length is 0 or > MaxFontBytes)
            return (AddStatus.Invalid, null, $"файл шрифта: до {MaxFontBytes / 1024 / 1024} МБ");
        using var typeface = SKTypeface.FromData(SKData.CreateCopy(data));
        if (typeface is null)
            return (AddStatus.Invalid, null, "это не шрифт: нужен файл TTF или OTF");
        if (!"0123456789".All(c => typeface.ContainsGlyph(c)))
            return (AddStatus.Invalid, null, "в шрифте нет цифр: на экране он ничего не покажет");
        var family = typeface.FamilyName?.Trim() is { Length: > 0 } name ? name : "Шрифт";
        family = family.Length > 64 ? family[..64] : family;
        var familyId = Slug.Replace(family.ToLowerInvariant(), "-").Trim('-');
        if (familyId.Length == 0)
            familyId = "font";
        if (Builtins.Any(b => b.Id == familyId))
            familyId = "my-" + familyId;
        familyId = familyId.Length > 40 ? familyId[..40].TrimEnd('-') : familyId;
        var bold = typeface.FontWeight >= 600;
        var italic = typeface.FontSlant != SKFontStyleSlant.Upright;
        var id = familyId + (bold ? (italic ? "-bi" : "-b") : italic ? "-i" : "");
        var sha = Convert.ToHexStringLower(SHA256.HashData(data))[..16];
        var cyrillic = typeface.ContainsGlyph('Ж') && typeface.ContainsGlyph('ж');
        var digitsOnly = !typeface.ContainsGlyph('A') && !typeface.ContainsGlyph('a');

        var row = await db.DisplayFonts.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (row is null)
        {
            if (await db.DisplayFonts.CountAsync(ct) >= MaxUploads)
                return (AddStatus.TooMany, null, $"своих шрифтов не больше {MaxUploads}: удалите ненужные");
            row = new DisplayFont { Id = id, FamilyId = familyId, Family = family, Data = data, Sha = sha };
            db.DisplayFonts.Add(row);
        }
        row.Family = family;
        row.Bold = bold;
        row.Italic = italic;
        row.Cyrillic = cyrillic;
        row.DigitsOnly = digitsOnly;
        row.Data = data;
        row.Sha = sha;
        row.CreatedAt = time.GetUtcNow();
        Audit.Record(db, time.GetUtcNow(), by, "display_font_added", id);
        await db.SaveChangesAsync(ct);
        return (AddStatus.Ok, (await ListAsync(ct)).First(f => f.Id == familyId), null);
    }

    /// <summary>Removes an uploaded family; the screens that used it draw with the standard font. The built-in ones stay.</summary>
    public async Task<bool> RemoveAsync(string familyId, string by, CancellationToken ct)
    {
        var rows = await db.DisplayFonts.Where(f => f.FamilyId == familyId).ToListAsync(ct);
        if (rows.Count == 0)
            return false;
        db.DisplayFonts.RemoveRange(rows);
        Audit.Record(db, time.GetUtcNow(), by, "display_font_removed", familyId);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

public static class DisplayFontEndpoints
{
    public static IEndpointRouteBuilder MapDisplayFontEndpoints(this IEndpointRouteBuilder app)
    {
        var fonts = app.MapGroup("/api/display/fonts").WithTags("display");
        fonts.MapGet("", async (DisplayFontCatalog catalog, CancellationToken ct) => await catalog.ListAsync(ct));
        // The file itself in the body (TTF or OTF, up to 4 MB): the page sends it as it is.
        fonts.MapPost("", async Task<Results<Ok<DisplayFontDto>, ValidationProblem>> (HttpRequest request, ClaimsPrincipal user, DisplayFontCatalog catalog, CancellationToken ct) =>
            {
                var data = await ReadBodyAsync(request, DisplayFontCatalog.MaxFontBytes, ct);
                if (data is null)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [$"файл шрифта: до {DisplayFontCatalog.MaxFontBytes / 1024 / 1024} МБ"] });
                var (status, font, error) = await catalog.AddAsync(data, $"user:{user.Identity?.Name}", ct);
                return status == DisplayFontCatalog.AddStatus.Ok
                    ? TypedResults.Ok(font!)
                    : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [error!] });
            })
            .WithBinaryBody()
            .RequireAuthorization(HiveRoles.AdminOnly);
        fonts.MapDelete("/{familyId}", async Task<Results<NoContent, NotFound>> (string familyId, ClaimsPrincipal user, DisplayFontCatalog catalog, CancellationToken ct) =>
                await catalog.RemoveAsync(familyId, $"user:{user.Identity?.Name}", ct) ? TypedResults.NoContent() : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }

    /// <summary>
    /// The endpoint reads the raw body itself (no Accepts metadata, which would answer 415); the document still says the body
    /// is a file, so the generated client takes one.
    /// </summary>
    public static RouteHandlerBuilder WithBinaryBody(this RouteHandlerBuilder builder) =>
        builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/octet-stream"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } },
                },
            };
            return Task.CompletedTask;
        });

    /// <summary>The body as bytes, or null when it is longer than the limit.</summary>
    public static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int limit, CancellationToken ct)
    {
        if (request.ContentLength > limit)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
