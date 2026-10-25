using System.Security.Cryptography.X509Certificates;

namespace ServiceLib.Tests.Services;

public class ObserverIdentityTests
{
    private static X509Certificate2 Root(ECDsa key)
    {
        var request = new CertificateRequest("CN=test CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(100));
    }
    private static X509Certificate2 Leaf(X509Certificate2 root, ECDsa key, string purpose, string name, bool expired = false)
    {
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(purpose) }, true));
        return request.Create(root, DateTimeOffset.UtcNow.AddHours(-2), expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddDays(60), RandomNumberGenerator.GetBytes(16));
    }

    [Test]
    public async Task CustomRootAndCertificatePurposeAreRequired()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var root = Root(rootKey);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var other = Root(otherKey);
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = Leaf(root, deviceKey, "1.3.6.1.5.5.7.3.2", new string('a', 32));
        await ObserverIdentity.ValidateChain(leaf, root, "1.3.6.1.5.5.7.3.2").Should().BeTrue();
        await ObserverIdentity.ValidateChain(leaf, other, "1.3.6.1.5.5.7.3.2").Should().BeFalse();
        await ObserverIdentity.ValidateChain(leaf, root, "1.3.6.1.5.5.7.3.1").Should().BeFalse();
        using var expired = Leaf(root, deviceKey, "1.3.6.1.5.5.7.3.2", new string('a', 32), true);
        await ObserverIdentity.ValidateChain(expired, root, "1.3.6.1.5.5.7.3.2").Should().BeFalse();
    }

    [Test]
    public async Task EnrollmentMustBindIssuedDeviceAndLocalPrivateKey()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var root = Root(rootKey);
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = new string('a', 32);
        using var leaf = Leaf(root, deviceKey, "1.3.6.1.5.5.7.3.2", id);
        var metadata = new ObserverConnection { Domain = "check.example.com", DeviceId = id, CaPem = root.ExportCertificatePem() };
        using var valid = new ObserverIdentity(metadata, leaf.ExportCertificatePem(), deviceKey.ExportPkcs8PrivateKeyPem());
        var rejected = false;
        try { using var invalid = new ObserverIdentity(metadata, leaf.ExportCertificatePem(), otherKey.ExportPkcs8PrivateKeyPem()); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { rejected = true; }
        await rejected.Should().BeTrue();
        metadata.DeviceId = new string('b', 32); rejected = false;
        try { using var invalid = new ObserverIdentity(metadata, leaf.ExportCertificatePem(), deviceKey.ExportPkcs8PrivateKeyPem()); }
        catch (IOException) { rejected = true; }
        await rejected.Should().BeTrue();
    }

    [Test]
    public async Task PairingRejectsDomainAndExpiredCodeBeforeNetworkOrStorage()
    {
        var store = new RefusingStore();
        var service = new ObserverPairingService(store, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "metadata.json"));
        var code = "EP1." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, domain = "check.example.com", serverId = "server", id = new string('a', 32), secret = new string('b', 64), caFingerprint = new string('c', 64), expires = DateTimeOffset.UtcNow.AddMinutes(-1) })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        foreach (var domain in new[] { "https://check.example.com", "other.example.com", "check.example.com" })
        {
            var rejected = false;
            try { await service.PairAsync(domain, code, "test", CancellationToken.None); }
            catch (ArgumentException) { rejected = true; }
            await rejected.Should().BeTrue();
        }
        await store.Calls.Should().BeEqualTo(0);
    }

    // Explicit opt-in: touches only a random isolated test item and always deletes it.
    [Test]
    public async Task PlatformSecureStoreRoundTripWhenRequested()
    {
        if (Environment.GetEnvironmentVariable("V2RAYN_TEST_SECRET_STORE") != "1") return;
        var store = new ObserverSecretStore(); var id = Guid.NewGuid().ToString("N"); var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        try
        {
            await store.SaveAsync(id, value);
            await (await new ObserverSecretStore().ReadAsync(id)).Should().BeEqualTo(value);
            await store.SaveAsync(id, value + "-updated");
            await (await store.ReadAsync(id)).Should().BeEqualTo(value + "-updated");
        }
        finally { await store.DeleteAsync(id); }
        await (await store.ReadAsync(id)).Should().BeNull();
    }

    private sealed class RefusingStore : IObserverSecretStore
    {
        public int Calls;
        public Task SaveAsync(string id, string secret) { Calls++; throw new ObserverStorageException("locked"); }
        public Task<string?> ReadAsync(string id) { Calls++; throw new ObserverStorageException("locked"); }
        public Task DeleteAsync(string id) { Calls++; throw new ObserverStorageException("locked"); }
    }
}
