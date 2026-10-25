using System.Net.Http;
using System.Security.Cryptography.X509Certificates;

namespace ServiceLib.Tests.Services;

public class ObserverPairingTests
{
    [Test]
    public async Task CancellationDuringSecureStoreWritePreservesPreviousIdentity()
    {
        using var f = new Fixture(); var service = f.Service();
        var original = await service.PairAsync(f.Profile.Domain, f.Code, "original", CancellationToken.None);
        var metadata = await File.ReadAllTextAsync(f.Path);
        f.ResetEnrollment();
        using var cancelled = new CancellationTokenSource();
        f.Store.AfterSave = cancelled.Cancel;
        var rejected = false;
        try { await service.PairAsync(f.Profile.Domain, f.Code, "replacement", cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        await rejected.Should().BeTrue();
        await (await File.ReadAllTextAsync(f.Path)).Should().BeEqualTo(metadata);
        await service.Metadata()!.CredentialRef.Should().BeEqualTo(original.CredentialRef);
        await f.Store.Values.Count.Should().BeEqualTo(1);
        await f.Store.Values.ContainsKey(original.CredentialRef).Should().BeTrue();
        await File.Exists(f.Path + ".tmp").Should().BeFalse();
    }

    [Test]
    public async Task TemporarySwitchRemovesRetiredStoredCredential()
    {
        using var f = new Fixture(); var service = f.Service();
        await service.PairAsync(f.Profile.Domain, f.Code, "original", CancellationToken.None);
        f.ResetEnrollment(); f.Store.Locked = true;
        try { await service.PairAsync(f.Profile.Domain, f.Code, "replacement", CancellationToken.None); }
        catch (ObserverStorageException) { }
        f.Store.Locked = false;
        await service.UseTemporaryAsync();
        await service.IsTemporary.Should().BeTrue();
        await f.Store.Values.Count.Should().BeEqualTo(0);
        await File.Exists(f.Path).Should().BeFalse();
        using var identity = await service.OpenAsync(CancellationToken.None);
        await identity.Connection.Domain.Should().BeEqualTo(f.Profile.Domain);
    }

    [Test]
    public async Task PairingRepairsMalformedPublicMetadata()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f.Path)!);
        await File.WriteAllTextAsync(f.Path, "{broken");
        await f.Service().PairAsync(f.Profile.Domain, f.Code, "device", CancellationToken.None);
        using var restored = await f.Service().OpenAsync(CancellationToken.None);
        await restored.Connection.Domain.Should().BeEqualTo(f.Profile.Domain);
    }

    [Test]
    public async Task UnpairDoesNotRenewAndSerializesConcurrentPairing()
    {
        using var f = new Fixture(); f.ShortCertificate = true;
        var service = f.Service();
        await service.PairAsync(f.Profile.Domain, f.Code, "device", CancellationToken.None);
        f.DeleteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.DeleteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleting = service.UnpairAsync(false, CancellationToken.None);
        await f.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        f.ResetEnrollment();
        var pairing = service.PairAsync(f.Profile.Domain, f.Code, "replacement", CancellationToken.None);
        var waitingForDelete = !pairing.IsCompleted;
        f.DeleteRelease.TrySetResult();
        await deleting.WaitAsync(TimeSpan.FromSeconds(2));
        var replacement = await pairing.WaitAsync(TimeSpan.FromSeconds(2));
        await waitingForDelete.Should().BeTrue();
        await f.RenewalCsr.Should().BeEqualTo("");
        await File.Exists(f.Path).Should().BeTrue();
        await f.Store.Values.ContainsKey(replacement.CredentialRef).Should().BeTrue();
        await f.Store.Values.Count.Should().BeEqualTo(1);
    }

    [Test]
    public async Task PairingPersistsOnlyPublicMetadataAndRestoresSecureIdentity()
    {
        using var f = new Fixture(); var service = f.Service();
        var connection = await service.PairAsync(f.Profile.Domain, f.Code, "device", CancellationToken.None);
        var metadata = await File.ReadAllTextAsync(f.Path);
        await metadata.Contains("PRIVATE KEY", StringComparison.Ordinal).Should().BeFalse();
        await metadata.Contains(f.Secret, StringComparison.Ordinal).Should().BeFalse();
        using var restored = await f.Service().OpenAsync(CancellationToken.None);
        await restored.Connection.DeviceId.Should().BeEqualTo(connection.DeviceId);
        await service.UnpairAsync(false, CancellationToken.None);
        await f.Revoked.Should().BeTrue();
        await File.Exists(f.Path).Should().BeFalse();
        await f.Store.Values.Count.Should().BeEqualTo(0);
    }

    [Test]
    public async Task DiscoveryMustMatchPairingPinBeforeSendingSecret()
    {
        using var f = new Fixture(); f.WrongDiscovery = true;
        var rejected = false;
        try { await f.Service().PairAsync(f.Profile.Domain, f.Code, "device", CancellationToken.None); }
        catch (IOException) { rejected = true; }
        await rejected.Should().BeTrue(); await f.Redeems.Should().BeEqualTo(0);
        await f.Store.Values.Count.Should().BeEqualTo(0);
    }

    [Test]
    public async Task LostResponseReusesCsrAndSecureStorageFailureRequiresExplicitTemporaryUse()
    {
        using var f = new Fixture(); var service = f.Service(); f.DropResponse = true;
        try { await service.PairAsync(f.Profile.Domain, f.Code, "device", CancellationToken.None); } catch (HttpRequestException) { }
        var csr = f.FirstCsr;
        f.Store.Locked = true;
        var locked = false;
        try { await service.PairAsync(f.Profile.Domain, f.Code, "device", CancellationToken.None); } catch (ObserverStorageException) { locked = true; }
        await locked.Should().BeTrue(); await f.FirstCsr.Should().BeEqualTo(csr);
        await service.HasCandidate.Should().BeTrue(); await service.IsTemporary.Should().BeFalse();
        await File.Exists(f.Path).Should().BeFalse();
        await service.UseTemporaryAsync();
        using var identity = await service.OpenAsync(CancellationToken.None);
        await service.IsTemporary.Should().BeTrue(); await File.Exists(f.Path).Should().BeFalse();
        await f.Store.Values.Count.Should().BeEqualTo(0);
    }

    [Test]
    public async Task FailedRenewalStorageRetainsOldCredentialAndRetriesIdenticalCsr()
    {
        using var f = new Fixture(); f.ShortCertificate = true;
        var service = f.Service(); var original = await service.PairAsync(f.Profile.Domain, f.Code, "device", CancellationToken.None);
        f.Store.Locked = true; var locked = false;
        try { using var identity = await service.OpenAsync(CancellationToken.None); } catch (ObserverStorageException) { locked = true; }
        await locked.Should().BeTrue();
        var oldMetadata = JsonSerializer.Deserialize<ObserverConnection>(await File.ReadAllTextAsync(f.Path), ProxyCheckService.Json)!;
        await oldMetadata.CredentialRef.Should().BeEqualTo(original.CredentialRef);
        var csr = f.RenewalCsr; f.Store.Locked = false;
        using var renewed = await service.OpenAsync(CancellationToken.None);
        await f.RenewalCsr.Should().BeEqualTo(csr);
        await (renewed.Expires > DateTimeOffset.UtcNow.AddDays(30)).Should().BeTrue();
    }

    private sealed class MemoryStore : IObserverSecretStore
    {
        public bool Locked;
        public Action? AfterSave;
        public Dictionary<string, string> Values = new();
        public Task SaveAsync(string id, string value) { if (Locked) throw new ObserverStorageException("locked"); Values[id] = value; AfterSave?.Invoke(); return Task.CompletedTask; }
        public Task<string?> ReadAsync(string id) => Task.FromResult(Values.GetValueOrDefault(id));
        public Task DeleteAsync(string id) { Values.Remove(id); return Task.CompletedTask; }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly MemoryStore Store = new();
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "observer-pairing-" + Guid.NewGuid().ToString("N"), "device.json");
        public readonly string Secret = new('b', 64);
        public readonly ObserverProfile Profile;
        public readonly string Code;
        public bool WrongDiscovery, DropResponse, Revoked, ShortCertificate;
        public int Redeems;
        public string FirstCsr = "", RenewalCsr = "";
        public TaskCompletionSource? DeleteEntered, DeleteRelease;
        public void ResetEnrollment() { FirstCsr = ""; _certificate = ""; ShortCertificate = false; }
        private readonly ECDsa _rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly X509Certificate2 _root;
        private readonly string _device = new('a', 32);
        private string _certificate = "", _renewed = "";
        public Fixture()
        {
            var request = new CertificateRequest("CN=fixture CA", _rootKey, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            _root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
            Profile = ObserverTests.Profile(_signingKey);
            Code = "EP1." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, domain = Profile.Domain, serverId = Profile.ServerId, id = new string('c', 32), secret = Secret, caFingerprint = Convert.ToHexString(SHA256.HashData(_root.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant(), expires = DateTimeOffset.UtcNow.AddMinutes(10) })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        public ObserverPairingService Service() => new(Store, Path, Client, (_, _) => Client(), _ => Client());
        private HttpClient Client() => new(new Handler(this)) { BaseAddress = new Uri("https://control." + Profile.Domain) };
        private string Issue(string csr, int days)
        {
            var parsed = CertificateRequest.LoadSigningRequestPem(csr, HashAlgorithmName.SHA256);
            var request = new CertificateRequest(new X500DistinguishedName("CN=" + _device), parsed.PublicKey, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
            using var cert = request.Create(_root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(days), RandomNumberGenerator.GetBytes(16));
            return cert.ExportCertificatePem();
        }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var f = fixture; object body;
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/.well-known/egress-probe") body = new { version = 2, domain = f.Profile.Domain, serverId = f.WrongDiscovery ? "forged" : f.Profile.ServerId, controlUrl = "https://control." + f.Profile.Domain, caPem = f._root.ExportCertificatePem() };
                else if (path == "/v2/capabilities") body = new { profile = f.Profile };
                else if (path == "/v2/devices/self")
                {
                    f.DeleteEntered?.TrySetResult();
                    if (f.DeleteRelease != null) await f.DeleteRelease.Task.WaitAsync(ct);
                    f.Revoked = true; return new(HttpStatusCode.NoContent);
                }
                else
                {
                    using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    var csr = input.RootElement.GetProperty("csr").GetString()!;
                    if (path.EndsWith("redeem", StringComparison.Ordinal))
                    {
                        f.Redeems++;
                        if (f.FirstCsr != "" && f.FirstCsr != csr) throw new InvalidOperationException("CSR changed during retry");
                        f.FirstCsr = csr; if (f._certificate == "") f._certificate = f.Issue(csr, f.ShortCertificate ? 20 : 90);
                        if (f.DropResponse) { f.DropResponse = false; throw new HttpRequestException("lost response"); }
                    }
                    else
                    {
                        if (f.RenewalCsr != "" && f.RenewalCsr != csr) throw new InvalidOperationException("renewal CSR changed");
                        f.RenewalCsr = csr; if (f._renewed == "") f._renewed = f.Issue(csr, 90);
                    }
                    body = new { deviceId = f._device, certificate = path.EndsWith("redeem", StringComparison.Ordinal) ? f._certificate : f._renewed, caPem = f._root.ExportCertificatePem(), controlUrl = "https://control." + f.Profile.Domain, profile = f.Profile };
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, ProxyCheckService.Json), Encoding.UTF8, "application/json") };
            }
        }
        public void Dispose() { if (Directory.Exists(System.IO.Path.GetDirectoryName(Path))) Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, true); _root.Dispose(); _rootKey.Dispose(); _signingKey.Dispose(); }
    }
}
