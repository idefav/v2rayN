using System.Threading.Channels;

namespace ServiceLib.Services;

/// <summary>Desktop opt-in lifecycle. Never uses the system proxy or the running core.</summary>
public sealed class ExitInfoService
{
    public static bool DesktopOwnsDetection { get; private set; }
    public static ExitInfoService Instance { get; } = new();
    private readonly ConcurrentDictionary<string, ExitInfoItem> _cache = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();
    private readonly ConcurrentDictionary<string, bool> _automatic = new();
    private readonly Channel<(ProfileItem Node, string Fingerprint, CancellationTokenSource Cancel)> _queue = Channel.CreateUnbounded<(ProfileItem, string, CancellationTokenSource)>();
    private readonly SemaphoreSlim _startGate = new(1);
    private readonly SemaphoreSlim _scanGate = new(1);
    private readonly SemaphoreSlim _launchGate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private DateTime _lastStart;
    private bool _started;
    private Task[] _workers = [];
    public event Action<string>? Changed;
    public ExitInfoItem? Get(string id) => _cache.GetValueOrDefault(id);

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        DesktopOwnsDetection = true;
        SQLiteHelper.Instance.CreateTable<ExitInfoItem>();
        foreach (var item in await SQLiteHelper.Instance.TableAsync<ExitInfoItem>().ToListAsync())
        {
            if (item.State is "queued" or "checking") item.State = "cancelled";
            _cache[item.IndexId] = item;
        }
        _workers = [WorkerAsync(), WorkerAsync()];
        _ = WatchAsync();
    }

    public void Cancel()
    {
        foreach (var token in _pending.Values) TryCancel(token);
    }
    private static void TryCancel(CancellationTokenSource token) { try { token.Cancel(); } catch (ObjectDisposedException) { } }

    public void Stop() { Cancel(); _lifetime.Cancel(); _queue.Writer.TryComplete(); }
    public async Task StopAsync() { Stop(); await Task.WhenAll(_workers); }

    private static async Task<string> EffectiveFingerprint(ProfileItem node)
    {
        var seen = new HashSet<string>();
        async Task<string> Visit(ProfileItem current)
        {
            if (!seen.Add(current.IndexId)) return "";
            var value = Fingerprint(current, AppManager.Instance.Config);
            var key = current.GetProtocolExtra().SshPrivateKeyPath;
            if (current.ConfigType == EConfigType.SSH && !string.IsNullOrEmpty(key))
                try { value += Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(key))); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { value += "unreadable-key"; }
            if (current.ConfigType is EConfigType.PolicyGroup or EConfigType.ProxyChain)
            {
                var (children, _) = await GroupProfileManager.GetChildProfileItems(current);
                foreach (var child in children) value += await Visit(child);
            }
            if (!string.IsNullOrEmpty(current.Subid))
            {
                var sub = await AppManager.Instance.GetSubItem(current.Subid);
                foreach (var reference in new[] { sub?.PrevProfile, sub?.NextProfile }.Where(s => !string.IsNullOrEmpty(s)))
                {
                    var linked = await AppManager.Instance.GetProfileItemViaRemarks(reference);
                    value += linked == null ? "missing-chain-member" : await Visit(linked);
                }
            }
            return value;
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(await Visit(node))));
    }

    public static string Fingerprint(ProfileItem node, Config config)
    {
        var json = JsonUtils.SerializeToNode(node) as JsonObject ?? new JsonObject();
        foreach (var name in new[] { "IndexId", "Remarks", "Subid", "IsSub", "DisplayLog" })
            foreach (var key in json.Select(p => p.Key).Where(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray()) json.Remove(key);
        var input = json.ToJsonString() + JsonUtils.Serialize(config.CoreTypeItem) + JsonUtils.Serialize(config.CoreBasicItem)
            + JsonUtils.Serialize(config.Mux4RayItem) + JsonUtils.Serialize(config.Mux4SboxItem) + JsonUtils.Serialize(config.HysteriaItem)
            + config.SpeedTestItem.IPAPIUrl;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    public static bool NeedsCheck(ExitInfoItem? item, string fingerprint, bool active, DateTime now)
        => item == null || item.Fingerprint != fingerprint ||
           item.State is not ("queued" or "checking" or "unsupported") && now >= item.RetryAfter &&
           (item.CheckedAt == default || active && now - item.CheckedAt >= TimeSpan.FromHours(24));

    public async Task ScanAsync()
    {
        if (!_started || !await _scanGate.WaitAsync(0)) return;
        try
        {
            var config = AppManager.Instance.Config;
            var nodes = await SQLiteHelper.Instance.TableAsync<ProfileItem>().ToListAsync();
            var ids = nodes.Select(n => n.IndexId).ToHashSet();
            foreach (var id in _cache.Keys.Where(id => !ids.Contains(id)))
            {
                if (_pending.TryGetValue(id, out var cancel)) TryCancel(cancel);
                if (_cache.TryRemove(id, out var old)) await SQLiteHelper.Instance.DeleteAsync(old);
            }
            if (!config.SpeedTestItem.AutoDetectExit)
            {
                foreach (var id in _automatic.Where(p => p.Value).Select(p => p.Key))
                    if (_pending.TryGetValue(id, out var token)) TryCancel(token);
                return;
            }
            foreach (var node in nodes)
                if (NeedsCheck(Get(node.IndexId), await EffectiveFingerprint(node), node.IndexId == config.IndexId, DateTime.UtcNow)) Queue(node, true);
        }
        finally { _scanGate.Release(); }
    }

    public void Queue(ProfileItem node, bool automatic = false)
    {
        if (!_started) return;
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (!_pending.TryAdd(node.IndexId, cancel)) { cancel.Dispose(); return; }
        _automatic[node.IndexId] = automatic;
        // The worker resolves dependencies immediately before launch, and verifies them again before committing.
        var fingerprint = Get(node.IndexId)?.Fingerprint ?? "";
        var old = Get(node.IndexId);
        var state = old?.Fingerprint == fingerprint ? JsonUtils.DeepCopy(old) : new ExitInfoItem { IndexId = node.IndexId, Fingerprint = fingerprint };
        state.State = "queued";
        _cache[node.IndexId] = state;
        Changed?.Invoke(node.IndexId);
        _queue.Writer.TryWrite((JsonUtils.DeepCopy(node), fingerprint, cancel));
    }

    private async Task WatchAsync()
    {
        try
        {
            try { await ScanAsync(); } catch (Exception ex) { Logging.SaveLog("ExitInfo scan", ex); }
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                try { await ScanAsync(); } catch (Exception ex) { Logging.SaveLog("ExitInfo scan", ex); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logging.SaveLog("ExitInfo", ex); }
    }

    private async Task WorkerAsync()
    {
        await foreach (var job in _queue.Reader.ReadAllAsync())
        {
            var id = job.Node.IndexId;
            var state = JsonUtils.DeepCopy(Get(id) ?? new ExitInfoItem { IndexId = id, Fingerprint = job.Fingerprint });
            ProcessService? process = null;
            try
            {
                job.Cancel.Token.ThrowIfCancellationRequested();
                if (_automatic.GetValueOrDefault(id) && !AppManager.Instance.Config.SpeedTestItem.AutoDetectExit) throw new OperationCanceledException();
                var fingerprint = await EffectiveFingerprint(job.Node);
                if (state.Fingerprint != fingerprint) state = new ExitInfoItem { IndexId = id, Fingerprint = fingerprint };
                await _startGate.WaitAsync(job.Cancel.Token);
                try
                {
                    var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _lastStart);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, job.Cancel.Token);
                    _lastStart = DateTime.UtcNow;
                }
                finally { _startGate.Release(); }
                job.Cancel.CancelAfter(TimeSpan.FromSeconds(30));
                state.State = "checking"; state.AttemptedAt = DateTime.UtcNow; state.Error = null;
                _cache[id] = state; Changed?.Invoke(id);
                if (job.Node.ConfigType is EConfigType.Custom or EConfigType.Outbound)
                    throw new NotSupportedException("Independent detection is unavailable for custom configurations.");
                var test = new ServerTestItem { IndexId = id, Profile = job.Node, ConfigType = job.Node.ConfigType };
                await _launchGate.WaitAsync(job.Cancel.Token);
                try
                {
                    var launch = CoreManager.Instance.LoadCoreConfigSpeedtest(test);
                    try { process = await launch.WaitAsync(job.Cancel.Token); }
                    catch (OperationCanceledException)
                    {
                        _ = CleanupLateLaunch(launch);
                        throw;
                    }
                    if (process == null) throw new InvalidOperationException("Unable to start the node's independent test core.");
                    await WaitForListenerAsync(test.Port, job.Cancel.Token);
                }
                finally { _launchGate.Release(); }
                var url = AppManager.Instance.Config.SpeedTestItem.IPAPIUrl;
                if (string.IsNullOrWhiteSpace(url)) url = Global.IPAPIUrls[0];
                var result = await QueryAsync(url, new WebProxy($"socks5://127.0.0.1:{test.Port}"), job.Cancel.Token);
                state.Ip = result.Ip; state.CountryCode = result.CountryCode; state.Country = result.Country;
                state.Region = result.Region; state.City = result.City; state.Source = url;
                state.CheckedAt = DateTime.UtcNow; state.State = "success"; state.RetryAfter = default;
            }
            catch (OperationCanceledException) { state.State = "cancelled"; state.Error = "Cancelled or timed out"; }
            catch (ExitRateLimitException ex) { state.State = "failed"; state.Error = ex.Message; state.RetryAfter = ex.RetryAfter; }
            catch (NotSupportedException ex) { state.State = "unsupported"; state.Error = ex.Message; }
            catch (Exception ex) { state.State = "failed"; state.Error = ex.Message; }
            finally
            {
              try
              {
                if (process != null) { await process.StopAsync(); process.Dispose(); }
                if (state.State != "success") state.RetryAfter = new[] { state.RetryAfter, DateTime.UtcNow.AddMinutes(30) }.Max();
                var current = await AppManager.Instance.GetProfileItem(id);
                if (current != null && await EffectiveFingerprint(current) == state.Fingerprint)
                {
                    _cache[id] = JsonUtils.DeepCopy(state);
                    await SQLiteHelper.Instance.ReplaceAsync(state);
                    if (state.State == "success")
                        RxSchedulers.MainThreadScheduler.Schedule(() =>
                        {
                            ProfileExManager.Instance.SetTestIpInfo(id, new IpInfoResult(state.CountryCode ?? state.Country ?? "unknown", state.Ip).ToString());
                            _ = ProfileExManager.Instance.SaveTo();
                        });
                }
                else _cache.TryRemove(id, out _);
              }
              catch (Exception ex) { Logging.SaveLog("ExitInfo save", ex); }
              finally { _automatic.TryRemove(id, out _); _pending.TryRemove(id, out _); job.Cancel.Dispose(); Changed?.Invoke(id); }
            }
        }
    }

    private static async Task CleanupLateLaunch(Task<ProcessService?> launch)
    {
        try { var process = await launch; if (process != null) { await process.StopAsync(); process.Dispose(); } }
        catch (Exception ex) { Logging.SaveLog("ExitInfo cleanup", ex); }
    }

    public sealed class ExitRateLimitException(DateTime retryAfter) : HttpRequestException("IP lookup rate limited (429).")
    {
        public DateTime RetryAfter { get; } = retryAfter;
    }

    public static async Task<ExitInfoItem> QueryAsync(string url, IWebProxy proxy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        using var handler = new SocketsHttpHandler { UseProxy = true, Proxy = proxy, ConnectTimeout = TimeSpan.FromSeconds(10), AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1024 * 1024 };
        using var response = await client.GetAsync(url, cancellationToken);
        if ((int)response.StatusCode == 429)
            throw new ExitRateLimitException(response.Headers.RetryAfter?.Date?.UtcDateTime ?? DateTime.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(30)));
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task WaitForListenerAsync(int port, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port, token); return; }
            catch (SocketException) { await Task.Delay(100, token); }
        }
    }

    public static ExitInfoItem Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? Field(JsonElement element, params string[] keys)
        {
            foreach (var key in keys)
                if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a JSON object.");
        var ip = Field(root, "ip", "clientIp", "ip_addr", "query");
        if (!IPAddress.TryParse(ip, out var address) || !IsPublicAddress(address))
            throw new FormatException("IP lookup returned no valid public exit address.");
        var location = root.TryGetProperty("location", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
        var code = Field(root, "country_code", "countryCode") ?? Field(location, "country_code");
        var country = Field(root, "country_name", "country") ?? Field(location, "country");
        if (code == null && country?.Length == 2) code = country;
        code = code?.ToUpperInvariant();
        return new ExitInfoItem { Ip = address.ToString(), CountryCode = code?.Length == 2 && code.All(char.IsAsciiLetter) ? code : null,
            Country = country, Region = Field(location, "region", "regionName", "state"), City = Field(location, "city", "city_name") };
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? !(bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) : (bytes[0] & 0xfe) != 0xfc;
    }
}
