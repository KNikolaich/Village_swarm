namespace Hive.Simulator.Roles;

/// <summary>Test JPEGs a simulated camera "shoots", cycled in order.</summary>
public sealed class PhotoLibrary(IReadOnlyList<byte[]> photos)
{
    private int _next;

    public int Count => photos.Count;

    public static PhotoLibrary Load(string dir) =>
        new(Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.jpg").Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList()
            : []);

    public byte[] Next() => photos.Count == 0 ? [] : photos[_next++ % photos.Count];
}
