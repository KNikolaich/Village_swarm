using System.Threading.Channels;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Media;

public sealed class ThumbnailQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(string mediaId) => _channel.Writer.TryWrite(mediaId);

    public ChannelReader<string> Reader => _channel.Reader;
}

/// <summary>Makes 320 px WebP previews (spec 6.3 MediaProcessor). One at a time to keep RPi memory flat.</summary>
public sealed class ThumbnailService(HiveDbContext db, MediaStore store, IOptions<MediaOptions> media, IOptions<HiveOptions> hive)
{
    /// <summary>Creates the preview if missing; returns its relative path, or null when the media item is unknown.</summary>
    public async Task<string?> EnsureAsync(string mediaId, CancellationToken ct)
    {
        var item = await db.Media.FirstOrDefaultAsync(m => m.Id == mediaId, ct);
        if (item is null)
            return null;
        if (item.ThumbPath is not null && File.Exists(store.FullPath(item.ThumbPath)))
            return item.ThumbPath;

        var thumbPath = MediaStore.ThumbPath(item.DeviceId, hive.Value.ToLocal(item.Ts), item.Id);
        var thumb = await Task.Run(() => ImageTools.Thumbnail(store.FullPath(item.Path), media.Value.ThumbWidth), ct);
        await store.WriteAtomicAsync(thumbPath, thumb, ct);

        item.ThumbPath = thumbPath;
        await db.SaveChangesAsync(ct);
        return thumbPath;
    }
}

public sealed class MediaProcessor(ThumbnailQueue queue, IServiceScopeFactory scopes, ILogger<MediaProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnqueueMissingAsync(stoppingToken);
        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ThumbnailService>().EnsureAsync(id, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Thumbnail for {MediaId} failed", id);
            }
        }
    }

    /// <summary>Photos that arrived while the api was stopping get their previews now.</summary>
    private async Task EnqueueMissingAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
            var missing = await db.Media.Where(m => m.ThumbPath == null).OrderByDescending(m => m.Ts).Select(m => m.Id).Take(1000).ToListAsync(ct);
            foreach (var id in missing)
                queue.Enqueue(id);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not scan for missing thumbnails");
        }
    }
}
