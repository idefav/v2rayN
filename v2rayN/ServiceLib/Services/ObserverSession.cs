using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;

namespace ServiceLib.Services;

internal sealed class ObserverSession : IAsyncDisposable
{
    private readonly HttpClient _client;
    private string _id = "";
    public ObserverProfile Profile { get; }
    private ObserverSession(ObserverProfile profile, string credential)
    {
        Profile = profile;
        _client = new(new HttpClientHandler { UseProxy = true, Proxy = HttpClient.DefaultProxy, AllowAutoRedirect = false }) { BaseAddress = new Uri(new Uri(profile.HttpsUrl).GetLeftPart(UriPartial.Authority)), Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 * 1024 };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
    }

    public static void Validate(ObserverProfile profile)
    {
        if (profile.Version != 1 || string.IsNullOrWhiteSpace(profile.ServerId) || !IPAddress.TryParse(profile.Ipv4, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Invalid IPv4 observer profile");
        using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Decode(profile.PublicKey), out _);
        if (key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value) throw new ArgumentException("Observer requires P-256 key");
        if (profile.Domain.Length is < 3 or > 180 || !profile.Domain.Contains('.') || profile.Domain.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-'))) throw new ArgumentException("Invalid observer domain");
        foreach (var url in new[] { profile.HttpsUrl, profile.Http3Url, profile.HttpUrl })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != (url == profile.HttpUrl ? "http" : "https") || !u.Host.EndsWith("." + profile.Domain, StringComparison.OrdinalIgnoreCase) || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0)
                throw new ArgumentException("Invalid observer URL");
        }
        foreach (var target in new[] { profile.TcpTarget, profile.TlsTarget, profile.UdpTarget, profile.StunTarget, profile.DnsTarget })
            if (!Uri.TryCreate("udp://" + target, UriKind.Absolute, out var u) || u.Port is < 1 or > 65535 || u.UserInfo.Length > 0 || u.AbsolutePath != "/" || u.Query.Length > 0 || u.Fragment.Length > 0 || !(u.Host.Equals(profile.Ipv4, StringComparison.Ordinal) || u.Host.EndsWith("." + profile.Domain, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Invalid observer endpoint");
    }

    public static async Task<ObserverSession> OpenAsync(ProxyCheckOptions options, CancellationToken ct)
    {
        var profile = options.Observer ?? throw new ArgumentException("Observer is not configured");
        Validate(profile);
        if (options.ObserverCredential.Length < 32) throw new ArgumentException("Observer credential is required (memory only)");
        var exactSelf = options.TargetExpectations.Values.Append(options.ExpectedEgress)
            .SelectMany(value => value.Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => value.Split('/'))
            .Any(parts => IPAddress.TryParse(parts[0], out var expected) && expected.Equals(IPAddress.Parse(profile.Ipv4)) && (parts.Length == 1 || parts.Length == 2 && parts[1] == "32"));
        if (exactSelf)
            throw new ArgumentException("Observer matches an expected egress; use an independent server");
        var session = new ObserverSession(profile, options.ObserverCredential);
        try
        {
            using var response = await session._client.GetAsync("/v1/capabilities", ct); response.EnsureSuccessStatusCode();
            using var capabilities = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var advertised = capabilities.RootElement.GetProperty("profile").Deserialize<ObserverProfile>(ProxyCheckService.Json);
            if (JsonSerializer.Serialize(advertised, ProxyCheckService.Json) != JsonSerializer.Serialize(profile, ProxyCheckService.Json)) throw new IOException("Observer profile changed; import the updated profile over a trusted channel");
            using var created = await session._client.PostAsJsonAsync("/v1/sessions", new { }, ct); created.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)); session._id = body.RootElement.GetProperty("id").GetString() ?? throw new IOException("Missing session ID");
            if (!IsID(session._id)) throw new IOException("Invalid session ID");
            return session;
        }
        catch { session._client.Dispose(); throw; }
    }

    private static bool IsID(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);
    public async Task<ObserverTicket> IssueAsync(string kind, string query, CancellationToken ct)
    {
        using var r = await _client.PostAsJsonAsync($"/v1/sessions/{_id}/probes", new { kind, query }, ct); r.EnsureSuccessStatusCode();
        var t = await r.Content.ReadFromJsonAsync<ObserverTicket>(ProxyCheckService.Json, ct) ?? throw new IOException("Missing ticket");
        if (t.SessionId != _id || !IsID(t.Id) || !IsID(t.Nonce) || t.Kind != kind || t.Query != query || t.QueryName != $"{t.Id}.{t.Nonce}.q.{Profile.Domain}." || t.Expires < DateTimeOffset.UtcNow.AddMinutes(-5) || t.Expires > DateTimeOffset.UtcNow.AddMinutes(10)) throw new IOException("Invalid observer ticket");
        return t;
    }

    public async Task<List<ObserverReceipt>> FetchAsync(ObserverTicket ticket, CancellationToken ct)
    {
        using var response = await _client.GetAsync($"/v1/sessions/{_id}/observations?probeId={ticket.Id}", ct); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ObserverReceipt>>(ProxyCheckService.Json, ct) ?? [];
    }

    internal static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    public static EgressObservation Verify(ObserverProfile profile, ObserverTicket ticket, ObserverReceipt receipt)
    {
        var bytes = Decode(receipt.Payload); var signature = Decode(receipt.Signature); var der = Decode(profile.PublicKey);
        if (bytes.Length > 16384 || signature.Length != 64 || receipt.KeyId != Convert.ToHexString(SHA256.HashData(der)).ToLowerInvariant()) throw new IOException("Invalid observer receipt");
        using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(der, out _);
        if (!key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new IOException("Invalid observer signature");
        var o = JsonSerializer.Deserialize<EgressObservation>(bytes, ProxyCheckService.Json) ?? throw new IOException("Invalid observation");
        if (o.Version != 1 || o.ServerId != profile.ServerId || o.SessionId != ticket.SessionId || o.ProbeId != ticket.Id || o.Nonce != ticket.Nonce || o.Kind != ticket.Kind || !IPAddress.TryParse(o.PeerIp, out _) || o.PeerPort is < 1 or > 65535 || o.ObservedAt > ticket.Expires || o.ObservedAt < ticket.Expires.AddMinutes(-3)) throw new IOException("Observation does not match this sample");
        return o;
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_id.Length > 0) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); using var response = await _client.DeleteAsync($"/v1/sessions/{_id}", timeout.Token); } }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
        finally { _client.Dispose(); }
    }
}
