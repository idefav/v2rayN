namespace ServiceLib.Models.Dto;

public sealed record EgressLocation(string Ip, string? CountryCode, string? Country, string? Region, string? City,
    string Provider, DateTimeOffset CheckedAt)
{
    [JsonIgnore]
    public string Label
    {
        get
        {
            var country = CountryExtension.CountryDisplayName(CountryCode, Country);
            var details = new[] { Region, City }.Where(s => !string.Equals(s, Country, StringComparison.OrdinalIgnoreCase));
            return string.Join(" · ", details.Prepend(country).Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }
}
