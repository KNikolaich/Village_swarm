using System.Globalization;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Media;

public sealed class MediaOptions
{
    public const string Section = "Media";

    /// <summary>Media root on the NVMe (spec 7.2). Relative paths resolve against the app content root.</summary>
    public string Root { get; set; } = "/srv/hive/media";

    public long MaxPhotoBytes { get; set; } = 2 * 1024 * 1024;
    public int ThumbWidth { get; set; } = 320;

    /// <summary>
    /// Development only: accept uploads without a bearer token, taking the device from X-Device-Id.
    /// Real hornets get an upload token at provisioning (build step 10).
    /// </summary>
    public bool AllowUploadsWithoutToken { get; set; }
}

/// <summary>File layout under the media root (spec 7.2). All stored paths are relative to the root.</summary>
public sealed class MediaStore
{
    private readonly string _root;

    public MediaStore(IOptions<MediaOptions> options, Microsoft.Extensions.Hosting.IHostEnvironment env)
    {
        _root = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.Root));
        Directory.CreateDirectory(Path.Combine(_root, "incoming"));
    }

    public string Root => _root;

    /// <summary>photos/{device}/{yyyy}/{MM}/{dd}/{HHmmss}_{eventType}_{photoId}.jpg, date in house local time.</summary>
    public static string PhotoPath(string deviceId, DateTimeOffset localTs, string eventType, string photoId) =>
        string.Join('/', "photos", deviceId, localTs.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
            $"{localTs.ToString("HHmmss", CultureInfo.InvariantCulture)}_{eventType}_{photoId}.jpg");

    public static string ThumbPath(string deviceId, DateTimeOffset localTs, string photoId) =>
        string.Join('/', "thumbs", deviceId, localTs.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), $"{photoId}.webp");

    public string FullPath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Path escapes media root: {relative}");
        return full;
    }

    /// <summary>Writes to incoming/, fsyncs, then renames into place, so a crash never leaves a half file (spec 7.2).</summary>
    public async Task WriteAtomicAsync(string relative, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var target = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = Path.Combine(_root, "incoming", $"{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await file.WriteAsync(data, ct);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
