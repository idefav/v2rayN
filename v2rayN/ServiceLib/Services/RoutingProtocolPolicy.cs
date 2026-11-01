using System.Globalization;

namespace ServiceLib.Services;

/// <summary>Protocols identified by each core's traffic sniffers, not outbound protocols.</summary>
public static class RoutingProtocolPolicy
{
    // Keep this catalog aligned with the bundled cores' sniffers. Do not add
    // sing-box-only protocols to Xray's shared routing configuration.
    private static readonly IReadOnlyList<string> SingboxProtocols = Array.AsReadOnly<string>(
        ["http", "tls", "quic", "bittorrent", "dns", "stun", "dtls", "ssh", "rdp", "ntp"]);
    private static readonly IReadOnlyList<string> XrayProtocols = Array.AsReadOnly<string>(
        ["http", "tls", "quic", "bittorrent"]);

    public static IReadOnlyList<string> Supported(ECoreType core) => core switch
    {
        ECoreType.sing_box => SingboxProtocols,
        ECoreType.Xray or ECoreType.v2fly or ECoreType.v2fly_v5 => XrayProtocols,
        _ => Array.Empty<string>()
    };

    public static IReadOnlyList<string> EditorOptions(ECoreType core, IEnumerable<string>? selected)
        => Supported(core).Concat(selected ?? []).Distinct(StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<string> Unsupported(ECoreType core, IEnumerable<string>? selected)
        => (selected ?? []).Except(Supported(core), StringComparer.Ordinal).ToArray();

    public static string CoreName(ECoreType core) => core == ECoreType.sing_box ? "sing-box" : core.ToString();

    public static string Documentation(ECoreType core) => core switch
    {
        ECoreType.sing_box => "https://sing-box.sagernet.org/configuration/route/sniff/",
        ECoreType.v2fly or ECoreType.v2fly_v5 => "https://www.v2fly.org/config/routing.html#ruleobject",
        _ => "https://xtls.github.io/config/routing.html#ruleobject"
    };

    public static string Describe(ECoreType core, IEnumerable<string>? selected, bool sniffingEnabled)
    {
        var text = T($"当前节点核心：{CoreName(core)}。", $"Active profile core: {CoreName(core)}. ");
        text += sniffingEnabled
            ? T("协议匹配使用流量嗅探。", "Protocol matching uses traffic sniffing.")
            : T("流量嗅探已关闭，协议规则无法生效。", "Traffic sniffing is off; protocol rules cannot match.");
        var unsupported = Unsupported(core, selected);
        if (unsupported.Count > 0)
            text += T($" 当前核心不支持：{string.Join(", ", unsupported)}。已保留原选项，请取消勾选或切换核心。",
                $" Unsupported: {string.Join(", ", unsupported)}. Existing selections are preserved; deselect them or switch cores.");
        return text;
    }

    public static string? ValidateRule(RulesItem rule, ECoreType core)
    {
        if (!rule.Enabled || rule.RuleType == ERuleType.DNS) return null;
        var unsupported = Unsupported(core, rule.Protocol);
        if (unsupported.Count == 0) return null;
        var label = rule.Remarks.IsNotEmpty() ? rule.Remarks : rule.Id;
        return T($"路由规则“{label}”包含 {CoreName(core)} 不支持的协议：{string.Join(", ", unsupported)}。请修改规则或切换核心。",
            $"Routing rule '{label}' contains protocols unsupported by {CoreName(core)}: {string.Join(", ", unsupported)}. Edit the rule or switch cores.");
    }

    public static string? ValidateRules(RoutingItem? routing, ECoreType core)
    {
        if (string.IsNullOrWhiteSpace(routing?.RuleSet)) return null;
        try
        {
            var rules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet);
            if (rules == null || rules.Any(rule => rule == null))
                return T("路由规则格式无效。", "Invalid routing rules.");
            return rules.Select(rule => ValidateRule(rule, core)).FirstOrDefault(error => error != null);
        }
        catch (JsonException)
        {
            return T("路由规则格式无效。", "Invalid routing rules.");
        }
    }

    private static string T(string chinese, string english)
        => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english;
}
