namespace ServiceLib.Services;

/// <summary>Optional metadata for an already observed address; never discovers or replaces the exit.</summary>
public sealed class EgressLocationService
{
    public static EgressLocationService Instance { get; } = new((url, token) =>
        new DownloadService().TryDownloadString(url, true, "v2rayN-IP-location", token));
    private sealed record Cached(EgressLocation? Location, DateTimeOffset Expires);
    private readonly Func<string, CancellationToken, Task<string?>> _download;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _requests = new(2);
    private readonly ConcurrentDictionary<string, Cached> _cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<EgressLocation?>>> _pending = new();

    internal EgressLocationService(Func<string, CancellationToken, Task<string?>> download, TimeProvider? clock = null)
    {
        _download = download;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<EgressLocation?> LookupAsync(string address, string? provider, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!IPAddress.TryParse(address, out var ip) || !ExitInfoService.IsPublicAddress(ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var url = LookupUrl(provider, ip.ToString());
        if (_cache.TryGetValue(url, out var cached) && cached.Expires > _clock.GetUtcNow()) return cached.Location;
        // A cancelled view stops waiting without cancelling another row's request for the same address.
        var task = _pending.GetOrAdd(url, _ => new(() => QueryAsync(url, ip))).Value;
        return await task.WaitAsync(token);
    }

    internal static string LookupUrl(string? provider, string address)
    {
        var encoded = Uri.EscapeDataString(address);
        if (provider?.Contains("{ip}", StringComparison.Ordinal) == true)
        {
            var expanded = provider.Replace("{ip}", encoded, StringComparison.Ordinal);
            if (Uri.TryCreate(expanded, UriKind.Absolute, out var custom) && custom.Scheme == "https" && custom.UserInfo.Length == 0)
                return custom.AbsoluteUri;
        }
        if (Uri.TryCreate(provider, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0)
        {
            if (uri.Host is "api.ip.sb" or "api-ipv4.ip.sb" or "api-ipv6.ip.sb")
                return new UriBuilder(uri) { Path = "/geoip/" + address, Query = "", Fragment = "" }.Uri.AbsoluteUri;
            if (uri.Host == "api.ipapi.is")
            {
                var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => !p.Split('=')[0].Equals("q", StringComparison.OrdinalIgnoreCase));
                return new UriBuilder(uri) { Path = "/", Query = string.Join('&', parameters.Append("q=" + encoded)), Fragment = "" }.Uri.AbsoluteUri;
            }
        }
        // A caller-IP-only endpoint cannot locate a different observed address.
        return "https://api.ip.sb/geoip/" + encoded;
    }

    private async Task<EgressLocation?> QueryAsync(string url, IPAddress expected)
    {
        EgressLocation? location = null;
        try
        {
            await _requests.WaitAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var json = await _download(url, timeout.Token);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var result = ExitInfoService.Parse(json);
                    var returned = IPAddress.Parse(result.Ip!);
                    if (returned.IsIPv4MappedToIPv6) returned = returned.MapToIPv4();
                    if (returned.Equals(expected) && new[] { result.CountryCode, result.Country, result.Region, result.City }.Any(s => !string.IsNullOrWhiteSpace(s)))
                        location = new(expected.ToString(), result.CountryCode, result.Country, result.Region, result.City,
                            new Uri(url).Host, _clock.GetUtcNow());
                }
            }
            finally { _requests.Release(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or JsonException or OperationCanceledException)
        {
            // Lookup failure does not change connectivity, signed observations, or expectation comparison.
        }
        finally
        {
            _cache[url] = new(location, _clock.GetUtcNow().Add(location == null ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(24)));
            foreach (var entry in _cache.OrderBy(p => p.Value.Expires).Take(Math.Max(0, _cache.Count - 512)))
                _cache.TryRemove(entry.Key, out _);
            _pending.TryRemove(url, out _);
        }
        return location;
    }
}
