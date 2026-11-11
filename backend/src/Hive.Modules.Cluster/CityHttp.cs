namespace Hive.Modules.Cluster;

/// <summary>
/// How one node speaks HTTP to the other. The city's certificate is self-signed and the way there is the router's tunnel, so the certificate is
/// not checked; what proves who is asking is the key made from the secret the two nodes share (see PhotoReplicaPolicy, NotesMasterPolicy).
/// </summary>
public static class CityHttp
{
    public static HttpMessageHandler Handler(TimeSpan connectTimeout) => new SocketsHttpHandler
    {
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        ConnectTimeout = connectTimeout,
    };
}
