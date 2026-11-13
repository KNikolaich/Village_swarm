using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Hive.Modules.Cluster;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests.Unit.Cluster;

/// <summary>The certificate of the city is checked only against a fingerprint typed in Settings; the city's own certificates rotate, so none is learned.</summary>
public class CityPinTests
{
    private const string City = """{"host":null,"port":1883,"replicaUrl":"https://city.test"}""";

    private static CityPin Pin() => new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

    private static string Setting(string? sha) => sha is null ? City : $$"""{"host":null,"port":1883,"replicaUrl":"https://city.test","certSha256":"{{sha}}"}""";

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=city.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }

    private static string Sha(X509Certificate2 certificate) => CityPin.Fingerprint(certificate);

    /// <summary>A TLS server on loopback that answers every request "ok" with the given certificate.</summary>
    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();

        public TlsServer(X509Certificate2 certificate)
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        var client = await listener.AcceptTcpClientAsync(stop.Token);
                        _ = Task.Run(async () =>
                        {
                            using var _ = client;
                            try
                            {
                                await using var tls = new SslStream(client.GetStream());
                                await tls.AuthenticateAsServerAsync(certificate);
                                var buffer = new byte[4096];
                                var seen = "";
                                while (!seen.Contains("\r\n\r\n"))
                                    seen += Encoding.ASCII.GetString(buffer, 0, await tls.ReadAsync(buffer));
                                await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                                await tls.FlushAsync();
                            }
                            catch (Exception)
                            {
                            }
                        });
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            });
        }

        public string Url => $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }

    private static async Task<string> GetAsync(CityPin pin, string url)
    {
        using var http = new HttpClient(CityHttp.Handler(TimeSpan.FromSeconds(5), pin)) { Timeout = TimeSpan.FromSeconds(10) };
        return await http.GetStringAsync(url);
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99", "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899")]
    [InlineData("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899", "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899")]
    [InlineData("  ", null)]
    [InlineData("nothex", null)]
    [InlineData("aabb", null)]
    public void A_fingerprint_is_read_with_or_without_colons(string typed, string? expected) => Assert.Equal(expected, CityPin.Normalize(typed));

    [Fact]
    public async Task The_typed_fingerprint_lets_the_city_in_and_any_other_certificate_is_refused()
    {
        using var real = SelfSigned();
        using var other = SelfSigned();
        using var server = new TlsServer(real);
        var pin = Pin();

        pin.Load(Setting(Sha(real)), DateTimeOffset.UtcNow);
        Assert.Equal("ok", await GetAsync(pin, server.Url));
        Assert.Equal(("settings", Sha(real)), (pin.Describe().Source, pin.Describe().Pinned));

        pin.Load(Setting(Sha(other)), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(pin, server.Url));
        Assert.Equal(Sha(real), pin.Describe().Seen);   // for the card: what the city really showed
    }

    [Fact]
    public async Task Without_a_typed_fingerprint_every_certificate_of_the_city_is_let_in_so_a_renewed_one_does_not_cut_the_link()
    {
        using var first = SelfSigned();
        using var renewed = SelfSigned();   // the city issues a new certificate every few hours
        var pin = Pin();
        pin.Load(Setting(null), DateTimeOffset.UtcNow);

        using (var server = new TlsServer(first))
            Assert.Equal("ok", await GetAsync(pin, server.Url));
        using (var server = new TlsServer(renewed))
            Assert.Equal("ok", await GetAsync(pin, server.Url));

        var described = pin.Describe();
        Assert.Equal(("none", null, null), (described.Source, described.Pinned, described.Seen));
    }

    [Fact]
    public async Task Typing_a_fingerprint_later_turns_the_check_on_and_clearing_it_turns_it_off()
    {
        using var real = SelfSigned();
        using var other = SelfSigned();
        using var server = new TlsServer(real);
        var pin = Pin();

        pin.Load(Setting(Sha(other)), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(pin, server.Url));

        pin.Load(Setting(null), DateTimeOffset.UtcNow);
        Assert.Equal("ok", await GetAsync(pin, server.Url));
    }
}
