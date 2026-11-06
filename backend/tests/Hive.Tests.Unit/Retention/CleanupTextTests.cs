using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Notify;

namespace Hive.Tests.Unit.Retention;

/// <summary>What Telegram says after an emergency cleanup: how much went, where the disk ended up, and why it may still be full.</summary>
public sealed class CleanupTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 9, 49, TimeSpan.Zero);

    private static DeviceEvent Cleanup(object payload) => new()
    {
        Id = "01J00000000000000000000000", DeviceId = "hive-home", Type = "disk_cleanup", Severity = EventSeverity.Warn, Ts = Now,
        Payload = JsonSerializer.SerializeToDocument(payload),
    };

    [Fact]
    public void Says_how_full_the_disk_was_and_is_and_that_photos_are_not_the_cause()
    {
        var text = Texts.Event(Cleanup(new { photos = 120, bytes = 47_185_920L, level = 1, usedPct = 89.7, usedAfterPct = 89.5, stillFull = true }), "Улей", Now);
        Assert.Contains("удалено фото: 120, 45 МБ (диск был занят на 89,7%, стало 89,5%)", text);
        Assert.Contains("место занимают не фото", text);
    }

    [Fact]
    public void A_cleanup_that_helped_has_no_hint_and_old_payloads_still_read()
    {
        var helped = Texts.Event(Cleanup(new { photos = 3, bytes = 1000L, level = 1, usedPct = 86.0, usedAfterPct = 79.9, stillFull = false }), "Улей", Now);
        Assert.Contains("стало 79,9%", helped);
        Assert.DoesNotContain("не фото", helped);
        var old = Texts.Event(Cleanup(new { photos = 3, bytes = 0L, level = 1, usedPct = 86.0 }), "Улей", Now);
        Assert.Contains("удалено фото: 3 (диск был занят на 86%)", old);
    }
}
