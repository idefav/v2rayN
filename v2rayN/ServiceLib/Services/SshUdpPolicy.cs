namespace ServiceLib.Services;

public static class SshUdpPolicy
{
    // Opt-in skips only the exact bundled rule, never a domain/IP/process-specific block.
    public static bool SkipDefaultUdp443Block(ProfileItem node, RulesItem rule)
    {
        var extra = node.GetProtocolExtra();
        return node.ConfigType == EConfigType.SSH && extra.SshUdpForwarding == true && extra.SshAllowHttp3 == true
            && rule.Remarks == "阻断udp443" && rule.OutboundTag == Global.BlockTag && rule.Network == "udp" && rule.Port == "443"
            && string.IsNullOrEmpty(rule.Type) && rule.RuleType is null or ERuleType.Routing or ERuleType.ALL
            && (rule.InboundTag?.Count ?? 0) == 0
            && (rule.Ip?.Count ?? 0) == 0 && (rule.Domain?.Count ?? 0) == 0 && (rule.Protocol?.Count ?? 0) == 0
            && (rule.Process?.Count ?? 0) == 0;
    }
}
