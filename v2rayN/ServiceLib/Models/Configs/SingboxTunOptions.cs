namespace ServiceLib.Models.Configs;

/// <summary>Optional sing-box features. Missing values preserve the existing generated configuration.</summary>
public sealed class SingboxTunOptions
{
    public string? InterfaceName { get; set; }
    public List<string>? AdditionalAddresses { get; set; }
    public string? DnsMode { get; set; }
    public List<string>? DnsAddress { get; set; }
    public string? UdpTimeout { get; set; }
    public string? UdpMapping { get; set; }
    public string? UdpFiltering { get; set; }
    public uint? UdpNatMax { get; set; }
    public List<string>? RouteAddress { get; set; }
    public List<string>? RouteAddressSet { get; set; }
    public List<string>? RouteExcludeAddressSet { get; set; }
    public List<TunRulesetSource>? RuleSets { get; set; }
    public bool? AutoRedirect { get; set; }
    public uint? AutoRedirectInputMark { get; set; }
    public uint? AutoRedirectOutputMark { get; set; }
    public uint? AutoRedirectResetMark { get; set; }
    public ushort? AutoRedirectNfQueue { get; set; }
    public int? IpRoute2TableIndex { get; set; }
    public int? IpRoute2RuleIndex { get; set; }
    public int? AutoRedirectFallbackRuleIndex { get; set; }
    public List<string>? IncludeInterface { get; set; }
    public List<string>? ExcludeInterface { get; set; }
    public List<uint>? IncludeUid { get; set; }
    public List<uint>? ExcludeUid { get; set; }
    public List<string>? IncludeUidRange { get; set; }
    public List<string>? ExcludeUidRange { get; set; }
    public List<string>? IncludeMacAddress { get; set; }
    public List<string>? ExcludeMacAddress { get; set; }
    public string? NetNs { get; set; }
    public bool? ExcludeMptcp { get; set; }
    public List<string>? LoopbackAddress { get; set; }
}

public sealed class TunRulesetSource
{
    public string Tag { get; set; } = "";
    public string Type { get; set; } = "local";
    public string Format { get; set; } = "binary";
    public string? Path { get; set; }
    public string? Url { get; set; }
}
