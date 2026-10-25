using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ServiceLib.Services;

internal sealed class ObserverAuthenticationException(string state, string message) : IOException(message)
{
    public string State { get; } = state;
}

// Public metadata may be stored in configuration. Device private keys may not.
internal sealed class ObserverConnection
{
    public string Domain { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string CredentialRef { get; set; } = "";
    public string CaPem { get; set; } = "";
    public ObserverProfile Profile { get; set; } = new();
}

internal sealed class ObserverIdentity : IDisposable
{
    public ObserverConnection Connection { get; }
    private readonly X509Certificate2 _certificate;
    internal ObserverIdentity(ObserverConnection connection, string certificate, string privateKey)
    {
        Connection = connection;
        _certificate = X509Certificate2.CreateFromPem(certificate, privateKey);
        if (_certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        {
            _certificate.Dispose();
            throw new ObserverAuthenticationException("expired", "Device certificate expired; pair again");
        }
        using var ca = X509Certificate2.CreateFromPem(connection.CaPem);
        if (!ValidateChain(_certificate, ca, "1.3.6.1.5.5.7.3.2") ||
            _certificate.GetNameInfo(X509NameType.SimpleName, false) != connection.DeviceId)
        {
            _certificate.Dispose();
            throw new IOException("Invalid observer device certificate");
        }
    }

    public DateTimeOffset Expires => _certificate.NotAfter.ToUniversalTime();
    public HttpClient CreateClient() => CreateClient(Connection.Domain, Connection.CaPem, _certificate);

    internal static bool ValidateChain(X509Certificate2 leaf, X509Certificate2 ca, string purpose)
    {
        var usages = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (usages == null || !usages.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == purpose) ||
            leaf.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority)) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(purpose));
        return chain.Build(leaf) && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(ca.RawData);
    }

    internal static HttpClient CreateClient(string domain, string caPem, X509Certificate2? device = null)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = true, Proxy = HttpClient.DefaultProxy };
        handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
        {
            if (certificate == null || request.RequestUri?.Host != "control." + domain ||
                (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
                return false;
            using var ca = X509Certificate2.CreateFromPem(caPem);
            return ValidateChain(certificate, ca, "1.3.6.1.5.5.7.3.1");
        };
        if (device != null) handler.ClientCertificates.Add(device);
        return new HttpClient(handler) { BaseAddress = new Uri("https://control." + domain), Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1024 * 1024 };
    }

    public void Dispose() => _certificate.Dispose();
}
