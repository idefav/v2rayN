namespace ServiceLib.Models.Entities;

[Serializable]
public class ProfileExItem
{
    [PrimaryKey]
    public string IndexId { get; set; }

    public int Delay { get; set; }
    public string? ProbeIdentity { get; set; }
    public string? TcpProbe { get; set; }
    public string? ProxyProbe { get; set; }
    public long ProbeCheckedAt { get; set; }
    public decimal Speed { get; set; }
    public int Sort { get; set; }
    public string? Message { get; set; }
    public string? IpInfo { get; set; }
}
