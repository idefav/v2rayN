using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;

namespace ServiceLib.Services;

internal sealed class ObserverPairingService
{
    internal static ObserverPairingService Instance { get; } = new(new ObserverSecretStore(), Utils.GetConfigPath("observer-device.json"));
    private readonly IObserverSecretStore _store;
    private readonly string _path;
    private readonly Func<HttpClient> _discoveryClient;
    private readonly Func<string, string, HttpClient> _pairingClient;
    private readonly Func<ObserverIdentity, HttpClient> _deviceClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Credential? _active;
    private Credential? _candidate;
    private Pending? _pending;
    private Pending? _renewal;
    public bool IsTemporary { get; private set; }
    public bool HasCandidate => _candidate != null;
    private sealed class Credential
    {
        public ObserverConnection Connection { get; set; } = new();
        public string Certificate { get; set; } = "";
        public string PrivateKey { get; set; } = "";
        public ObserverIdentity Identity() => new(Connection, Certificate, PrivateKey);
    }
    private sealed record Pending(string CodeHash, string PrivateKey, string Csr);
    private sealed class Code
    {
        public int Version { get; set; }
        public string Domain { get; set; } = "";
        public string ServerId { get; set; } = "";
        public string Id { get; set; } = "";
        public string Secret { get; set; } = "";
        public string CaFingerprint { get; set; } = "";
        public DateTimeOffset Expires { get; set; }
    }
    private sealed class Discovery
    {
        public int Version { get; set; }
        public string Domain { get; set; } = "";
        public string ServerId { get; set; } = "";
        public string ControlUrl { get; set; } = "";
        public string CaPem { get; set; } = "";
    }
    private sealed class Enrollment
    {
        public string DeviceId { get; set; } = "";
        public string Certificate { get; set; } = "";
        public string CaPem { get; set; } = "";
        public string ControlUrl { get; set; } = "";
        public ObserverProfile Profile { get; set; } = new();
    }
    internal ObserverPairingService(IObserverSecretStore store, string path, Func<HttpClient>? discoveryClient = null,
        Func<string, string, HttpClient>? pairingClient = null, Func<ObserverIdentity, HttpClient>? deviceClient = null)
    {
        _store = store; _path = path;
        _discoveryClient = discoveryClient ?? (() => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 65536 });
        _pairingClient = pairingClient ?? ((domain, ca) => ObserverIdentity.CreateClient(domain, ca));
        _deviceClient = deviceClient ?? (identity => identity.CreateClient());
    }
    internal static string NormalizeDomain(string domain)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.Length is < 3 or > 180 || !domain.Contains('.') || Uri.CheckHostName(domain) != UriHostNameType.Dns ||
            domain.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
            throw new ArgumentException("Enter a domain without scheme, path or port");
        return domain;
    }
    public ObserverConnection? Metadata()
    {
        if (_active != null) return _active.Connection;
        return File.Exists(_path) ? JsonSerializer.Deserialize<ObserverConnection>(File.ReadAllText(_path), ProxyCheckService.Json) : null;
    }
    private static Pending NewKey(string hash)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=v2rayN", key, HashAlgorithmName.SHA256);
        return new(hash, key.ExportPkcs8PrivateKeyPem(), request.CreateSigningRequestPem());
    }
    public async Task<ObserverConnection> PairAsync(string domain, string pairingCode, string name, CancellationToken ct)
    {
        domain = NormalizeDomain(domain);
        pairingCode = pairingCode.Trim();
        if (pairingCode.Length > 16384 || !pairingCode.StartsWith("EP1.", StringComparison.Ordinal)) throw new ArgumentException("Invalid pairing string");
        var code = JsonSerializer.Deserialize<Code>(ObserverSession.Decode(pairingCode[4..]), ProxyCheckService.Json) ?? throw new ArgumentException("Invalid pairing string");
        if (code.Version != 1 || code.Domain != domain || code.Expires <= DateTimeOffset.UtcNow || code.Expires > DateTimeOffset.UtcNow.AddMinutes(31) ||
            code.Id.Length != 32 || code.Secret.Length != 64 || code.CaFingerprint.Length != 64 || string.IsNullOrWhiteSpace(code.ServerId))
            throw new ArgumentException("Pairing expired or does not match this domain");
        if (string.IsNullOrWhiteSpace(name) || Encoding.UTF8.GetByteCount(name) > 128 || name.Any(char.IsControl)) throw new ArgumentException("Invalid device name");
        await _gate.WaitAsync(ct);
        try
        {
            _candidate = null;
            using var discoveryClient = _discoveryClient();
            var discovery = await discoveryClient.GetFromJsonAsync<Discovery>("https://api." + domain + "/.well-known/egress-probe", ProxyCheckService.Json, ct)
                ?? throw new IOException("Upgrade the observer service to pairing version 2");
            using var ca = X509Certificate2.CreateFromPem(discovery.CaPem);
            var fingerprint = Convert.ToHexString(SHA256.HashData(ca.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
            if (discovery.Version != 2 || discovery.Domain != domain || discovery.ServerId != code.ServerId ||
                discovery.ControlUrl != "https://control." + domain || fingerprint != code.CaFingerprint)
                throw new ObserverAuthenticationException("identity-changed", "Server identity differs from the trusted pairing string");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pairingCode)));
            if (_pending?.CodeHash != hash) _pending = NewKey(hash);
            using var client = _pairingClient(domain, discovery.CaPem);
            using var response = await client.PostAsJsonAsync("/v2/pairings/redeem", new { id = code.Id, secret = code.Secret, csr = _pending.Csr, name }, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new ArgumentException("Pairing expired, consumed, revoked or incorrect");
            CheckStatus(response);
            var enrollment = await response.Content.ReadFromJsonAsync<Enrollment>(ProxyCheckService.Json, ct) ?? throw new IOException("Missing enrollment");
            var credential = ValidateEnrollment(enrollment, domain, code.ServerId, discovery.CaPem, _pending.PrivateKey);
            await VerifyAsync(credential, ct);
            _candidate = credential;
            await SaveAsync(credential, ct);
            _active = credential; _candidate = null; _pending = null; _renewal = null; IsTemporary = false;
            return credential.Connection;
        }
        finally { _gate.Release(); }
    }
    private static Credential ValidateEnrollment(Enrollment enrollment, string domain, string serverId, string caPem, string key)
    {
        ObserverSession.Validate(enrollment.Profile);
        using var expected = X509Certificate2.CreateFromPem(caPem);
        using var received = X509Certificate2.CreateFromPem(enrollment.CaPem);
        if (!expected.RawData.AsSpan().SequenceEqual(received.RawData) || enrollment.Profile.Domain != domain || enrollment.Profile.ServerId != serverId ||
            enrollment.ControlUrl != "https://control." + domain || enrollment.DeviceId.Length != 32 || !enrollment.DeviceId.All(Uri.IsHexDigit))
            throw new IOException("Enrollment identity changed");
        var credential = new Credential { Certificate = enrollment.Certificate, PrivateKey = key,
            Connection = new() { Domain = domain, DeviceId = enrollment.DeviceId, CaPem = caPem, Profile = enrollment.Profile, CredentialRef = Guid.NewGuid().ToString("N") } };
        using var identity = credential.Identity();
        return credential;
    }
    internal static void CheckStatus(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new ObserverAuthenticationException("revoked", "Device authentication rejected or revoked; pair again");
        if ((int)response.StatusCode == 426) throw new IOException("Upgrade the observer service and pair again");
        response.EnsureSuccessStatusCode();
    }
    private async Task VerifyAsync(Credential credential, CancellationToken ct)
    {
        using var identity = credential.Identity(); using var client = _deviceClient(identity);
        using var response = await client.GetAsync("/v2/capabilities", ct); CheckStatus(response);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var profile = document.RootElement.GetProperty("profile").Deserialize<ObserverProfile>(ProxyCheckService.Json);
        if (JsonSerializer.Serialize(profile, ProxyCheckService.Json) != JsonSerializer.Serialize(credential.Connection.Profile, ProxyCheckService.Json))
            throw new ObserverAuthenticationException("identity-changed", "Server signing identity or endpoints changed; pair again");
    }
    private async Task SaveAsync(Credential credential, CancellationToken ct)
    {
        // Write a new secure-store item before replacing the public reference. Failed writes retain the old identity.
        string? previous = null;
        try { previous = File.Exists(_path) ? JsonSerializer.Deserialize<ObserverConnection>(await File.ReadAllTextAsync(_path), ProxyCheckService.Json)?.CredentialRef : null; }
        catch (JsonException) { /* Re-pairing can repair malformed public metadata. */ }
        ct.ThrowIfCancellationRequested();
        await _store.SaveAsync(credential.Connection.CredentialRef, JsonSerializer.Serialize(credential, ProxyCheckService.Json));
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            await File.WriteAllTextAsync(_path + ".tmp", JsonSerializer.Serialize(credential.Connection, ProxyCheckService.Json), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(_path + ".tmp", _path, true);
        }
        catch
        {
            try { File.Delete(_path + ".tmp"); } catch (IOException) { }
            try { await _store.DeleteAsync(credential.Connection.CredentialRef); } catch (ObserverStorageException) { }
            throw;
        }
        if (previous is { Length: 32 } && previous.All(Uri.IsHexDigit) && previous != credential.Connection.CredentialRef)
        {
            try { await _store.DeleteAsync(previous); }
            catch (ObserverStorageException) { /* The new identity is committed; a locked old item cannot undo it. */ }
        }
    }
    public async Task<ObserverConnection> UseTemporaryAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var candidate = _candidate ?? throw new InvalidOperationException("No validated temporary identity");
            string? previous = null;
            try { previous = Metadata()?.CredentialRef; }
            catch (JsonException) { /* Malformed public metadata must not block temporary use. */ }
            // An explicit temporary switch must not silently restore the previous service after restart.
            if (File.Exists(_path)) File.Delete(_path);
            if (previous is { Length: 32 } && previous.All(Uri.IsHexDigit))
            {
                // Storage may still be locked; that must not prevent explicit temporary use.
                try { await _store.DeleteAsync(previous); } catch (ObserverStorageException) { }
            }
            _active = candidate;
            _candidate = null; _pending = null; _renewal = null; IsTemporary = true; return _active.Connection;
        }
        finally { _gate.Release(); }
    }
    public async Task<ObserverIdentity> OpenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await LoadActiveAsync();
            using var current = _active!.Identity();
            if (current.Expires <= DateTimeOffset.UtcNow.AddDays(30))
            {
                _renewal ??= NewKey(_active.Connection.DeviceId);
                using var client = _deviceClient(current);
                using var response = await client.PostAsJsonAsync("/v2/devices/self/renew", new { csr = _renewal.Csr }, ct); CheckStatus(response);
                var enrollment = await response.Content.ReadFromJsonAsync<Enrollment>(ProxyCheckService.Json, ct) ?? throw new IOException("Missing renewal");
                var renewed = ValidateEnrollment(enrollment, _active.Connection.Domain, _active.Connection.Profile.ServerId, _active.Connection.CaPem, _renewal.PrivateKey);
                if (renewed.Connection.DeviceId != _active.Connection.DeviceId || JsonSerializer.Serialize(renewed.Connection.Profile, ProxyCheckService.Json) != JsonSerializer.Serialize(_active.Connection.Profile, ProxyCheckService.Json)) throw new IOException("Renewal changed device or server identity");
                await VerifyAsync(renewed, ct);
                if (!IsTemporary) await SaveAsync(renewed, ct);
                _active = renewed; _renewal = null;
            }
            return _active.Identity();
        }
        finally { _gate.Release(); }
    }
    // Caller holds _gate, including across remote revocation and local removal.
    private async Task LoadActiveAsync()
    {
        if (_active != null) return;
        var metadata = Metadata() ?? throw new IOException("Pair the observer service first");
        var value = await _store.ReadAsync(metadata.CredentialRef) ?? throw new ObserverStorageException("Device credentials unavailable; pair again");
        var loaded = JsonSerializer.Deserialize<Credential>(value, ProxyCheckService.Json) ?? throw new ObserverStorageException("Invalid stored identity");
        if (JsonSerializer.Serialize(loaded.Connection, ProxyCheckService.Json) != JsonSerializer.Serialize(metadata, ProxyCheckService.Json))
            throw new ObserverAuthenticationException("identity-changed", "Stored server identity changed; pair again");
        using var validated = loaded.Identity(); _active = loaded;
    }
    public async Task UnpairAsync(bool localOnly, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!localOnly)
            {
                await LoadActiveAsync();
                using var identity = _active!.Identity(); using var client = _deviceClient(identity);
                using var response = await client.DeleteAsync("/v2/devices/self", ct); CheckStatus(response);
            }
            var metadata = Metadata();
            if (metadata != null && !IsTemporary) await _store.DeleteAsync(metadata.CredentialRef);
            if (File.Exists(_path)) File.Delete(_path);
            _active = null; _candidate = null; _pending = null; _renewal = null; IsTemporary = false;
        }
        finally { _gate.Release(); }
    }
}
