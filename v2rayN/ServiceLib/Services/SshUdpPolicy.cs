namespace ServiceLib.Services;

public static class SshUdpPolicy
{
    public const string Custom = "custom";
    public const string Tun2Proxy = "tun2proxy";
    public const string DefaultGateway = "127.0.0.1:7300";

    public static string Mode(ProtocolExtraItem extra) => string.IsNullOrEmpty(extra.SshUdpMode) ? Custom : extra.SshUdpMode;
    public static string Gateway(ProtocolExtraItem extra) => string.IsNullOrWhiteSpace(extra.SshUdpGateway) ? DefaultGateway : extra.SshUdpGateway.Trim();
    public static string? Validate(ProtocolExtraItem extra)
    {
        if (extra.SshUdpForwarding != true) return null;
        if (Mode(extra) is not (Custom or Tun2Proxy)) return SshStrings.InvalidUdpMode;
        if (Mode(extra) == Tun2Proxy && !ValidGateway(Gateway(extra))) return SshStrings.InvalidUdpGateway;
        return null;
    }

    public static bool ValidGateway(string gateway)
    {
        if (gateway.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || gateway.IndexOfAny(['/', '?', '#', '@']) >= 0) return false;
        var separator = gateway.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(gateway[(separator + 1)..], out var port) || port is < 1 or > 65535) return false;
        var host = gateway[..separator];
        if (host.StartsWith('['))
            return host.EndsWith(']') && IPAddress.TryParse(host[1..^1], out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        return host.Length <= 255 && !host.Contains(':') && Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }

    public static bool CoreUdpEnabled(JsonNode outbound)
        => outbound["udp_mode"]?.GetValue<string>() switch
        {
            "custom" or "tun2proxy" => true,
            null or "" => outbound["udp_forwarding"]?.GetValue<bool>() == true,
            _ => false
        };

    // Opt-in skips only the exact bundled rule, never a domain/IP/process-specific block.
    public static bool SkipDefaultUdp443Block(ProfileItem node, RulesItem rule)
    {
        var extra = node.GetProtocolExtra();
        return node.ConfigType == EConfigType.SSH && extra.SshUdpForwarding == true && Validate(extra) == null && extra.SshAllowHttp3 == true
            && rule.Remarks == "阻断udp443" && rule.OutboundTag == Global.BlockTag && rule.Network == "udp" && rule.Port == "443"
            && string.IsNullOrEmpty(rule.Type) && rule.RuleType is null or ERuleType.Routing or ERuleType.ALL
            && (rule.InboundTag?.Count ?? 0) == 0
            && (rule.Ip?.Count ?? 0) == 0 && (rule.Domain?.Count ?? 0) == 0 && (rule.Protocol?.Count ?? 0) == 0
            && (rule.Process?.Count ?? 0) == 0;
    }
}
