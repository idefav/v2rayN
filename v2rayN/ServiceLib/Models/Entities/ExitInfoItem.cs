namespace ServiceLib.Models.Entities;

// Separate cache: old ProfileExItem.IpInfo consumers and WPF remain compatible.
public sealed class ExitInfoItem
{
    [PrimaryKey] public string IndexId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string? Ip { get; set; }
    public string? CountryCode { get; set; }
    public string? Country { get; set; }
    public string? Region { get; set; }
    public string? City { get; set; }
    public string? Source { get; set; }
    public DateTime CheckedAt { get; set; }
    public DateTime AttemptedAt { get; set; }
    public DateTime RetryAfter { get; set; }
    public string State { get; set; } = "pending";
    public string? Error { get; set; }
}
