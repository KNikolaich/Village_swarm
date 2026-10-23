using System.Globalization;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Hive.Modules.IpCams;

/// <summary>A channel to take into the swarm: its device gets the name and zone given here.</summary>
/// <summary>SnapshotSource: auto (snapshot.cgi, then the RTSP stream), cgi or rtsp; null keeps auto.</summary>
public sealed record IpCamChannelRequest(int Channel, string Name, int? ZoneId = null, bool RecordWhenDisarmed = false, string? SnapshotSource = null);

/// <summary>An empty Password on an update keeps the stored one.</summary>
public sealed record NvrRequest(string Name, string Host, int? Port, string Username, string? Password, IReadOnlyList<IpCamChannelRequest> Channels, int? RtspPort = null);

public sealed record NvrProbeRequest(string Host, int? Port, string Username, string Password);

/// <summary>SnapshotVia: how the last frame came (cgi or rtsp), null before the first one.</summary>
public sealed record IpCamChannelDto(
    int Channel, string DeviceId, string Name, int? ZoneId, string Status, bool RecordWhenDisarmed, string RtspUrl, string SnapshotSource, string? SnapshotVia);

/// <summary>An NVR as the settings show it: never its password, only whether one is stored.</summary>
/// <summary>Login: whether the hive may talk to the NVR now, or waits because it locked or refused the login (see NvrLogins).</summary>
public sealed record NvrDto(int Id, string Name, string Host, int Port, string Username, bool PasswordSet, IReadOnlyList<IpCamChannelDto> Channels, int RtspPort, NvrLoginDto Login);

public enum NvrChange
{
    Ok,
    NotFound,
    BadRequest,
}

/// <summary>NVRs and their channels (contracts/ipcam.md): each channel is a device of driver and type ipcam.</summary>
public sealed class IpCamService(HiveDbContext db, IDataProtectionProvider protection, IpCamWatch watch, NvrLogins logins, TimeProvider time)
{
    public const int MaxChannels = 64;
    private readonly IDataProtector _protector = protection.CreateProtector(IpCamWatch.ProtectorPurpose);

    public static string DeviceIdFor(int nvrId, int channel) =>
        string.Create(CultureInfo.InvariantCulture, $"ipcam{nvrId}-ch{channel}");

    public async Task<IReadOnlyList<NvrDto>> ListAsync(CancellationToken ct)
    {
        var nvrs = await db.Nvrs.AsNoTracking().Include(n => n.Channels).OrderBy(n => n.Id).ToListAsync(ct);
        var ids = nvrs.SelectMany(n => n.Channels).Select(c => c.DeviceId).ToList();
        var devices = await db.Devices.AsNoTracking().Where(d => ids.Contains(d.DeviceId)).ToDictionaryAsync(d => d.DeviceId, ct);
        return nvrs.Select(n => ToDto(n, devices, LoginState(n))).ToList();
    }

    public async Task<NvrDto?> GetAsync(int id, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(n => n.Id == id);

    private NvrLoginDto LoginState(Nvr n)
    {
        try
        {
            return logins.State(new NvrEndpoint(n.Host, n.Port, n.Username, _protector.Unprotect(n.PasswordProtected), n.RtspPort));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return new NvrLoginDto(NvrLogins.Paused, null, null); // the keys are gone: the password has to be entered again
        }
    }

    /// <summary>The stored login of an NVR, for "Проверить" and the live snapshot; null when there is no such NVR.</summary>
    public async Task<NvrEndpoint?> EndpointAsync(int id, CancellationToken ct) =>
        await db.Nvrs.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct) is { } n
            ? new NvrEndpoint(n.Host, n.Port, n.Username, _protector.Unprotect(n.PasswordProtected), n.RtspPort)
            : null;

    /// <summary>What the snapshot service needs of a channel: its device and settings when it is in the swarm, else auto.</summary>
    public async Task<SnapshotTarget> TargetAsync(int id, int channel, CancellationToken ct) =>
        await db.NvrChannels.AsNoTracking().Where(c => c.NvrId == id && c.Channel == channel)
            .Select(c => new SnapshotTarget(id, c.DeviceId, c.Channel, c.SnapshotSource, c.SnapshotVia)).FirstOrDefaultAsync(ct)
        ?? new SnapshotTarget(id, null, channel, SnapshotSources.Auto, null);

    public async Task<(NvrChange Result, string? Error, NvrDto? Nvr)> CreateAsync(NvrRequest r, string by, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(r.Password))
            return (NvrChange.BadRequest, "password: нужен пароль видеорегистратора", null);
        if (await ValidateAsync(r, ct) is { } error)
            return (NvrChange.BadRequest, error, null);
        var now = time.GetUtcNow();
        var nvr = new Nvr
        {
            Name = r.Name.Trim(),
            Host = Host(r.Host),
            Port = r.Port ?? 80,
            RtspPort = r.RtspPort ?? 554,
            Username = r.Username.Trim(),
            PasswordProtected = _protector.Protect(r.Password),
            CreatedAt = now,
        };
        db.Nvrs.Add(nvr);
        await db.SaveChangesAsync(ct); // the id names the devices
        await SetChannelsAsync(nvr, r.Channels, now, ct);
        Audit.Record(db, now, by, "nvr_add", nvr.Name, new { nvr.Host, nvr.Port, channels = r.Channels.Select(c => c.Channel) });
        await db.SaveChangesAsync(ct);
        logins.Forget(nvr.Host, nvr.Port);
        watch.Reload();
        return (NvrChange.Ok, null, await GetAsync(nvr.Id, ct));
    }

    public async Task<(NvrChange Result, string? Error, NvrDto? Nvr)> UpdateAsync(int id, NvrRequest r, string by, CancellationToken ct)
    {
        var nvr = await db.Nvrs.Include(n => n.Channels).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (nvr is null)
            return (NvrChange.NotFound, null, null);
        if (await ValidateAsync(r, ct) is { } error)
            return (NvrChange.BadRequest, error, null);
        var now = time.GetUtcNow();
        nvr.Name = r.Name.Trim();
        nvr.Host = Host(r.Host);
        nvr.Port = r.Port ?? 80;
        nvr.RtspPort = r.RtspPort ?? nvr.RtspPort;
        nvr.Username = r.Username.Trim();
        if (!string.IsNullOrEmpty(r.Password))
            nvr.PasswordProtected = _protector.Protect(r.Password);
        await SetChannelsAsync(nvr, r.Channels, now, ct);
        Audit.Record(db, now, by, "nvr_update", nvr.Name,
            new { nvr.Host, nvr.Port, channels = r.Channels.Select(c => c.Channel), passwordChanged = !string.IsNullOrEmpty(r.Password) });
        await db.SaveChangesAsync(ct);
        logins.Forget(nvr.Host, nvr.Port);
        watch.Reload();
        return (NvrChange.Ok, null, await GetAsync(nvr.Id, ct));
    }

    /// <summary>Forgets the NVR; its cameras leave the swarm, their events and photos stay.</summary>
    public async Task<bool> DeleteAsync(int id, string by, CancellationToken ct)
    {
        var nvr = await db.Nvrs.Include(n => n.Channels).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (nvr is null)
            return false;
        await SetChannelsAsync(nvr, [], time.GetUtcNow(), ct);
        db.Nvrs.Remove(nvr);
        Audit.Record(db, time.GetUtcNow(), by, "nvr_delete", nvr.Name);
        await db.SaveChangesAsync(ct);
        watch.Reload();
        return true;
    }

    /// <summary>Channels to the list asked for: new ones become devices, missing ones leave the swarm.</summary>
    private async Task SetChannelsAsync(Nvr nvr, IReadOnlyList<IpCamChannelRequest> wanted, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var gone in nvr.Channels.Where(c => wanted.All(w => w.Channel != c.Channel)).ToList())
        {
            if (await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == gone.DeviceId, ct) is { } device)
                device.Status = DeviceStatus.Disabled;
            nvr.Channels.Remove(gone);
            db.NvrChannels.Remove(gone);
        }
        foreach (var w in wanted)
        {
            var channel = nvr.Channels.FirstOrDefault(c => c.Channel == w.Channel);
            if (channel is null)
            {
                channel = new NvrChannel { NvrId = nvr.Id, Channel = w.Channel, DeviceId = DeviceIdFor(nvr.Id, w.Channel) };
                nvr.Channels.Add(channel);
            }
            channel.RecordWhenDisarmed = w.RecordWhenDisarmed;
            var source = w.SnapshotSource ?? SnapshotSources.Auto;
            if (channel.SnapshotSource != source)
                channel.SnapshotVia = null; // a new way to try: nothing known about it yet
            channel.SnapshotSource = source;

            // A channel taken out earlier and back again finds its old device, with its history.
            var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == channel.DeviceId, ct);
            if (device is null)
            {
                device = new Device { DeviceId = channel.DeviceId, TypeCode = Cameras.IpCam, Name = w.Name.Trim(), CreatedAt = now };
                db.Devices.Add(device);
            }
            if (device.Status == DeviceStatus.Disabled || device.Driver != Drivers.IpCam)
                device.Status = DeviceStatus.Pending; // online once the NVR's stream is up
            device.TypeCode = Cameras.IpCam;
            device.Driver = Drivers.IpCam;
            device.Name = w.Name.Trim();
            device.ZoneId = w.ZoneId;
            device.Hw = "dahua-nvr";
            device.Ip = nvr.Host;
            device.Info = JsonSerializer.SerializeToDocument(new
            {
                caps = new[] { "snapshot" },
                metrics = Array.Empty<string>(),
                commands = new[] { "snapshot" },
                nvr = nvr.Name,
                channel = w.Channel,
            });
        }
    }

    private async Task<string?> ValidateAsync(NvrRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 64)
            return "name: от 1 до 64 символов";
        if (HostError(r.Host, r.Port) is { } hostError)
            return hostError;
        if (string.IsNullOrWhiteSpace(r.Username) || r.Username.Trim().Length > 64)
            return "username: логин видеорегистратора";
        if (r.Channels.Count > MaxChannels)
            return $"channels: не больше {MaxChannels}";
        if (r.Channels.Any(c => c.Channel is < 1 or > 256))
            return "channels: номер канала от 1";
        if (r.Channels.GroupBy(c => c.Channel).Any(g => g.Count() > 1))
            return "channels: каналы повторяются";
        if (r.RtspPort is < 1 or > 65535)
            return "rtspPort: от 1 до 65535";
        if (r.Channels.Any(c => c.SnapshotSource is not null && !SnapshotSources.IsKnown(c.SnapshotSource)))
            return "channels: снимок берётся auto, cgi или rtsp";
        if (r.Channels.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Trim().Length > 128))
            return "channels: у каждой камеры нужно имя до 128 символов";
        var zones = r.Channels.Select(c => c.ZoneId).OfType<int>().Distinct().ToList();
        if (zones.Count > 0 && await db.Zones.CountAsync(z => zones.Contains(z.Id), ct) != zones.Count)
            return "channels: такой зоны нет";
        return null;
    }

    public static string? HostError(string? host, int? port)
    {
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(Host(host)) == UriHostNameType.Unknown)
            return "host: адрес видеорегистратора, например 192.168.10.245";
        if (port is < 1 or > 65535)
            return "port: от 1 до 65535";
        return null;
    }

    /// <summary>People paste "http://192.168.10.245/": the bare host is kept.</summary>
    public static string Host(string host)
    {
        var h = host.Trim();
        if (Uri.TryCreate(h, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri.Host;
        return h.TrimEnd('/');
    }

    private static IpCamChannelDto ToChannelDto(Nvr n, NvrChannel c, IReadOnlyDictionary<string, Device> devices)
    {
        var d = devices.GetValueOrDefault(c.DeviceId);
        return new IpCamChannelDto(c.Channel, c.DeviceId, d?.Name ?? c.DeviceId, d?.ZoneId,
            (d?.Status ?? DeviceStatus.Pending).ToString().ToLowerInvariant(), c.RecordWhenDisarmed,
            new NvrEndpoint(n.Host, n.Port, n.Username, "", n.RtspPort).RtspUrl(c.Channel), c.SnapshotSource, c.SnapshotVia);
    }

    private static NvrDto ToDto(Nvr n, IReadOnlyDictionary<string, Device> devices, NvrLoginDto login) =>
        new(n.Id, n.Name, n.Host, n.Port, n.Username, n.PasswordProtected.Length > 0,
            n.Channels.OrderBy(c => c.Channel).Select(c => ToChannelDto(n, c, devices)).ToList(), n.RtspPort, login);
}
