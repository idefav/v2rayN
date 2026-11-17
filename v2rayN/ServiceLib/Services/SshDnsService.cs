using System.Net.Http;

namespace ServiceLib.Services;

public enum SshDnsVerdict { Match, Mismatch, Unknown, Cancelled }
public sealed record SshDnsCheckResult(SshDnsVerdict Verdict, string? ExitIp, string[] DnsIps, int Probes,
    DateTimeOffset CheckedAt, string? Error = null);

internal interface ISshDnsRemote : IAsyncDisposable
{
    Task<JsonObject> SendAsync(object request, CancellationToken token);
}

internal sealed class SshDnsRemote : ISshDnsRemote
{
    private readonly SshGatewayRemote _remote;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "v2rayn-dns-" + Guid.NewGuid().ToString("N"));
    private string _password, _hash = "";
    private bool _root, _uploaded;
    private string Stage => "/tmp/v2rayn-dns-" + _id;
    private SshDnsRemote(ProfileItem node, string password) { _remote = new(node); _password = password; }
    internal static async Task<SshDnsRemote> OpenAsync(ProfileItem node, string password, CancellationToken token)
    {
        var value = new SshDnsRemote(JsonUtils.DeepCopy(node), password);
        try
        {
            var host = await value._remote.InspectHostAsync(token);
            value._root = host.Root;
            new SshKeyStore(value._directory).EnsureDirectory();
            var helper = Path.Combine(value._directory, "helper.py");
            var bytes = Encoding.UTF8.GetBytes(SshGatewayAssets.Resource("ssh-dns-helper.py"));
            using (var file = SshKeyStore.CreatePrivateFile(helper)) await file.WriteAsync(bytes, token);
            value._hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await value._remote.UploadFilesAsync(value.Stage, [(helper, "helper.py")], () => value._uploaded = true, token);
            return value;
        }
        catch { await value.DisposeAsync(); throw; }
    }
    public Task<JsonObject> SendAsync(object request, CancellationToken token) =>
        _remote.DnsHelperAsync(_id, _hash, _root, _password, request, token);
    public async ValueTask DisposeAsync()
    {
        _password = "";
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (_uploaded) await _remote.RemoveDnsStageAsync(Stage, cleanup.Token);
        }
        catch { }
        finally { _remote.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}

public sealed class SshDnsService
{
    internal Func<ProfileItem, Func<string, CancellationToken, Task<JsonObject>>?, CancellationToken, Task<SshDnsCheckResult>>? ProbeOverride { get; init; }
    internal static async Task<SshDnsCheckResult> ObserveAsync(
        Func<string, CancellationToken, Task<JsonObject>> fetch,
        Func<string, CancellationToken, Task<JsonObject>>? candidate, CancellationToken token)
    {
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        string? exit = null;
        var completed = 0;
        try
        {
            exit = (await fetch("https://ipv4.ipleak.net/json/", token))["ip"]?.GetValue<string>();
            if (!IPAddress.TryParse(exit, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new SshSetupException("E_EXIT_ADDRESS");
            var session = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
            for (var i = 0; i < 3; i++)
            {
                token.ThrowIfCancellationRequested();
                var name = session + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5)) + ".ipleak.net";
                var response = candidate != null ? await candidate(name, token)
                    : await fetch("https://" + name + "/dnsdetection/", token);
                if (response["session"]?.GetValue<string>() != session || response["ip"] is not JsonObject observed || observed.Count == 0)
                    throw new SshSetupException("E_OBSERVER_RESPONSE");
                foreach (var entry in observed)
                {
                    if (!IPAddress.TryParse(entry.Key, out var address) || entry.Value?.GetValue<int>() is not > 0)
                        throw new SshSetupException("E_OBSERVER_RESPONSE");
                    addresses.Add(address.ToString());
                }
                completed++;
            }
            if ((await fetch("https://ipv4.ipleak.net/json/", token))["ip"]?.GetValue<string>() != exit)
                throw new SshSetupException("E_EXIT_CHANGED");
            return new(addresses.SetEquals([exit]) ? SshDnsVerdict.Match : SshDnsVerdict.Mismatch,
                exit, addresses.Order().ToArray(), completed, DateTimeOffset.UtcNow);
        }
        catch (Exception e)
        {
            return new(token.IsCancellationRequested ? SshDnsVerdict.Cancelled : SshDnsVerdict.Unknown,
                exit, addresses.Order().ToArray(), completed, DateTimeOffset.UtcNow,
                e is SshSetupException known ? known.Code : "E_OBSERVER_UNAVAILABLE");
        }
    }
    public async Task<SshDnsCheckResult> CheckAsync(ProfileItem snapshot, CancellationToken token) =>
        await CheckAsync(snapshot, null, token);
    internal async Task<SshDnsCheckResult> CheckAsync(ProfileItem snapshot,
        Func<string, CancellationToken, Task<JsonObject>>? candidate, CancellationToken token)
    {
        if (ProbeOverride != null) return await ProbeOverride(snapshot, candidate, token);
        try
        {
            return await new SshConnectionTestService().RunProxyAsync(JsonUtils.DeepCopy(snapshot), async (port, password, ct) =>
            {
                using var handler = new SocketsHttpHandler { UseProxy = true, UseCookies = false, AllowAutoRedirect = false,
                    Proxy = new WebProxy($"socks5://127.0.0.1:{port}") { Credentials = new NetworkCredential("probe", password) } };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
                return await ObserveAsync(async (url, requestToken) =>
                {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestToken);
                    response.EnsureSuccessStatusCode();
                    using var stream = await response.Content.ReadAsStreamAsync(requestToken);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[4096];
                    int count;
                    while ((count = await stream.ReadAsync(chunk, requestToken)) > 0)
                    {
                        if (buffer.Length + count > 65536) throw new SshSetupException("E_OBSERVER_RESPONSE");
                        buffer.Write(chunk, 0, count);
                    }
                    return JsonNode.Parse(buffer.ToArray())!.AsObject();
                }, candidate, ct);
            }, token);
        }
        catch (Exception e)
        {
            return new(token.IsCancellationRequested ? SshDnsVerdict.Cancelled : SshDnsVerdict.Unknown,
                null, [], 0, DateTimeOffset.UtcNow, e is SshSetupException known ? known.Code : "E_SSH_PROBE");
        }
    }

    internal Task<SshDnsDeployment> PrepareAsync(ProfileItem snapshot, string password, CancellationToken token) =>
        SshDnsDeployment.PrepareAsync(snapshot, password, this, token);

    internal async Task<JsonObject> RollbackAsync(ProfileItem snapshot, string password, CancellationToken token)
    {
        await using var remote = await SshDnsRemote.OpenAsync(snapshot, password, token);
        var status = await remote.SendAsync(new { version = 1, action = "status" }, token);
        if (status["id"]?.GetValue<string>() is not { } id) throw new SshSetupException("E_NOT_INSTALLED");
        return await remote.SendAsync(new { version = 1, action = "rollback", transactionId = id }, token);
    }
}

internal sealed class SshDnsDeployment : IAsyncDisposable
{
    private readonly ISshDnsRemote _remote;
    private readonly ProfileItem _node;
    private readonly SshDnsService _checks;
    private readonly string _id = Guid.NewGuid().ToString("N");
    internal JsonObject Plan { get; set; } = new();
    internal SshDnsDeployment(ISshDnsRemote remote, ProfileItem node, SshDnsService checks)
    { _remote = remote; _node = JsonUtils.DeepCopy(node); _checks = checks; }
    internal static async Task<SshDnsDeployment> PrepareAsync(ProfileItem node, string password, SshDnsService checks, CancellationToken token)
    {
        var value = new SshDnsDeployment(await SshDnsRemote.OpenAsync(node, password, token), node, checks);
        try
        {
            value.Plan = await value._remote.SendAsync(new { version = 1, action = "inspect" }, token);
            var probe = await checks.CheckAsync(node, token);
            token.ThrowIfCancellationRequested();
            if (probe.ExitIp == null || probe.Verdict == SshDnsVerdict.Unknown) throw new SshSetupException(probe.Error ?? "E_OBSERVER_UNAVAILABLE");
            if (probe.ExitIp != value.Plan["exit"]?.GetValue<string>()) throw new SshSetupException("E_EXIT_MISMATCH");
            return value;
        }
        catch { await value.DisposeAsync(); throw; }
    }
    internal async Task<SshDnsCheckResult> InstallAsync(CancellationToken token)
    {
        var phase = Plan["phase"]?.GetValue<string>();
        if (phase == "committed") return await _checks.CheckAsync(_node, token);
        var intent = false;
        SshDnsCheckResult? verified = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (phase == "legacy")
            {
                var legacyCheck = await _checks.CheckAsync(_node, token);
                RequireMatch(legacyCheck, token);
                await _remote.SendAsync(new { version = 1, action = "adopt", planToken = Plan["token"]!.GetValue<string>(),
                    expectedExit = legacyCheck.ExitIp, transactionId = _id }, token);
                return legacyCheck;
            }
            intent = true;
            await _remote.SendAsync(new { version = 1, action = "prepare", planToken = Plan["token"]!.GetValue<string>(),
                expectedExit = Plan["exit"]!.GetValue<string>(), transactionId = _id }, token);
            var candidate = await _checks.CheckAsync(_node, (name, ct) => _remote.SendAsync(
                new { version = 1, action = "candidate", transactionId = _id, name }, ct), token);
            RequireMatch(candidate, token);
            await _remote.SendAsync(new { version = 1, action = "activate", transactionId = _id }, token);
            var result = await _checks.CheckAsync(_node, token);
            RequireMatch(result, token);
            verified = result;
            await _remote.SendAsync(new { version = 1, action = "commit", transactionId = _id,
                verifiedExit = result.ExitIp, verifiedDns = result.DnsIps }, token);
            return result;
        }
        catch (Exception original)
        {
            if (intent)
            {
                try
                {
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    var state = await _remote.SendAsync(new { version = 1, action = "status" }, recovery.Token);
                    if (state["id"]?.GetValue<string>() == _id && state["phase"]?.GetValue<string>() == "committed" && verified != null)
                        return verified; // Lost commit acknowledgement is not a failed deployment.
                    if (state["id"]?.GetValue<string>() == _id && state["phase"]?.GetValue<string>() is not ("committed" or "rolled_back"))
                        await _remote.SendAsync(new { version = 1, action = "rollback", transactionId = _id }, recovery.Token);
                }
                catch { throw new SshSetupException("E_ROLLBACK_UNCONFIRMED"); }
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
    }
    private static void RequireMatch(SshDnsCheckResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (result.Verdict != SshDnsVerdict.Match) throw new SshSetupException(result.Error ?? "E_DNS_MISMATCH");
    }
    public ValueTask DisposeAsync() => _remote.DisposeAsync();
}
