using System.Globalization;

namespace ServiceLib.Services;

/// <summary>Protocols identified by each core's traffic sniffers, not outbound protocols.</summary>
public static class RoutingProtocolPolicy
{
    public static List<RulesItem>? ReadActiveRules(CoreConfigContext context)
    {
        var json = context.RoutingItem?.RuleSet;
        if (string.IsNullOrWhiteSpace(json)) return [];
        var rules = JsonUtils.Deserialize<List<RulesItem>>(json);
        if (rules == null || rules.Any(rule => rule == null || rule.Protocol?.Any(protocol => protocol is null) == true))
            return null;

        var supported = Supported(context.RunCoreType);
        if (context.RoutingCoreType is { } mainCore && mainCore != context.RunCoreType)
            supported = supported.Intersect(Supported(mainCore)).ToArray();
        var custom = context.Node.ConfigType == EConfigType.Custom || context.ActiveTemplate != null;
        return rules.Where(rule => rule.Enabled && (custom || rule.RuleType == ERuleType.DNS
            || rule.Protocol == null || rule.Protocol.All(supported.Contains))).ToList();
    }

    public static string InvalidRulesMessage => T("路由规则格式无效。", "Invalid routing rules.");

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

    public static IEnumerable<string> EditorOptions(IEnumerable<string>? selected)
        => SingboxProtocols.Union(XrayProtocols, StringComparer.Ordinal).Union(selected ?? [], StringComparer.Ordinal);

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
            text += T($" 使用 {CoreName(core)} 时将跳过整条规则。该核心不支持 {string.Join(", ", unsupported)}。",
                $" The entire rule is skipped with {CoreName(core)}. Unsupported protocols: {string.Join(", ", unsupported)}.");
        else if (core == ECoreType.sing_box && Unsupported(ECoreType.Xray, selected).Count > 0)
            text += T(" 切换到 Xray 时将跳过整条规则。", " Switching to Xray skips the entire rule.");
        return text;
    }

    private static string T(string chinese, string english)
        => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english;
}
