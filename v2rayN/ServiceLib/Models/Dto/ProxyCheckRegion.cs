using System.ComponentModel;
using System.Globalization;

namespace ServiceLib.Models.Dto;

public static class ProxyCheckCountries
{
    // Some region codes (e.g. DG) cannot be passed back to RegionInfo as culture names.
    private static readonly Dictionary<string, string> EnglishNames = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(c => { try { return new RegionInfo(c.Name); } catch (ArgumentException) { return null; } }).OfType<RegionInfo>()
        .Where(r => r.TwoLetterISORegionName.Length == 2 && r.TwoLetterISORegionName.All(char.IsAsciiLetter))
        .GroupBy(r => r.TwoLetterISORegionName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().EnglishName, StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<string> Codes { get; } = EnglishNames.Keys.Order().ToArray();
    public static string? Normalize(string? code) => code is not null && EnglishNames.ContainsKey(code.Trim()) ? code.Trim().ToUpperInvariant() : null;
    public static string EnglishName(string code) => EnglishNames.GetValueOrDefault(code, code);
    public static string Name(string code) => CountryExtension.CountryDisplayName(code, EnglishName(code)) ?? code;
    public static bool SameAddress(string first, string second) => IPAddress.TryParse(first, out var a) && IPAddress.TryParse(second, out var b)
        && (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(b.IsIPv4MappedToIPv6 ? b.MapToIPv4() : b);

    // Advisory only. Never changes connectivity, IP comparison, combined conclusion or counters.
    public static string Compare(ProxyDiagnosticResult row, EgressLocation? location)
    {
        if (row.ObservationType is "not-applicable" or "recursive-resolver") return "not-applicable";
        if (row.ExpectedCountryCodes is not { Count: > 0 }) return "not-configured";
        var code = Normalize(location?.CountryCode);
        if (code != null) return row.ExpectedCountryCodes.Contains(code, StringComparer.OrdinalIgnoreCase) ? "passed" : "failed";
        return row.LocationState == "loading" ? "loading" : "unknown";
    }
    public static string Compare(ProxyDiagnosticResult row)
    {
        var basis = Compare(row, null);
        if (basis is "not-applicable" or "not-configured") return basis;
        var states = row.Egress.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ip => Compare(row, row.EgressLocations.FirstOrDefault(l => SameAddress(ip, l.Ip)))).ToArray();
        return states.Contains("failed") ? "failed" : states.Length > 0 && states.All(s => s == "passed") ? "passed"
            : row.LocationState == "loading" ? "loading" : "unknown";
    }
    public static string Label(string state, bool resolver = false) => state switch
    {
        "passed" => ProxyCheckText.T("✓ 地域匹配", "✓ Region matches"),
        "failed" => ProxyCheckText.T("≠ 地域不匹配", "≠ Region differs"),
        "loading" => ProxyCheckText.T("地域查询中…", "Looking up region…"),
        "not-configured" => ProxyCheckText.T("未配置地域", "No region expectation"),
        "not-applicable" => resolver ? ProxyCheckText.T("解析器地域 · 不比较", "Resolver region · Not compared") : ProxyCheckText.T("地域不适用", "Region not applicable"),
        _ => ProxyCheckText.T("地域未知", "Region unknown")
    };
}

public sealed class ProxyCheckCountryChoice(string code) : INotifyPropertyChanged
{
    private bool _selected;
    public string Code { get; } = code;
    public string Name => ProxyCheckCountries.Name(Code);
    public string EnglishName => ProxyCheckCountries.EnglishName(Code);
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ProxyCheckExitAddress(ProxyDiagnosticResult row, string address)
{
    public string Address { get; } = address;
    private EgressLocation? Location => row.EgressLocations.FirstOrDefault(l => ProxyCheckCountries.SameAddress(l.Ip, Address));
    public string? CountryCode => ProxyCheckCountries.Normalize(Location?.CountryCode);
    public string LocationLabel => Location?.Label ?? (row.LocationState == "loading" ? ProxyCheckText.T("地域查询中…", "Looking up location…") : ProxyCheckText.T("地域未知", "Location unavailable"));
    public string RegionState => ProxyCheckCountries.Compare(row, Location);
    public string RegionLabel => ProxyCheckCountries.Label(RegionState, row.ObservationType == "recursive-resolver");
    public bool RegionMatches => RegionState == "passed";
    public bool RegionDiffers => RegionState == "failed";
    public bool ShowRegionComparison => RegionState is not ("unknown" or "loading");
    public string Hint => ProxyCheckText.T("地域为第三方数据库信息，独立比较；不影响 IP 判定，不属于签名证据。", "Third-party region data is compared independently; it does not affect IP verdicts and is not signed evidence.")
        + "\n" + ProxyCheckText.T("本轮预期地域：", "Run region expectation: ")
        + (row.ExpectedCountryCodes is { Count: > 0 } ? string.Join(", ", row.ExpectedCountryCodes.Select(ProxyCheckCountries.Name)) : ProxyCheckText.T("未配置", "Not configured"))
        + (Location is { } location ? $"\n{location.Provider} · {location.CheckedAt.ToLocalTime():g}" : "");
}
