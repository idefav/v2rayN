using static ServiceLib.Services.RoutingCoreCompatibility;

namespace ServiceLib.Services;

public static class RouteTargetPolicy
{
    internal const string BridgeTag = "route-target-bridge";
    private static readonly byte[] BridgeSecret = RandomNumberGenerator.GetBytes(32);
    private static readonly ConcurrentDictionary<int, int> BridgePorts = new();

    internal static int BridgePort(int mixedPort) => BridgePorts.GetOrAdd(mixedPort, port =>
        Utils.GetFreePort(port < 65527 ? port + 8 : port - 1));

    internal static string BridgeUserId(string tag) =>
        new Guid(HMACSHA256.HashData(BridgeSecret, Encoding.UTF8.GetBytes(tag)).AsSpan(0, 16)).ToString();

    public static bool CanSelect(ProfileItem profile) => !profile.ConfigType.IsGroupType() && profile.ConfigType != EConfigType.Custom;

    public static string Summary(RouteOutboundTarget target, string? currentName = null)
        => target.SubscriptionId != null
            ? T($"跟随 {currentName ?? target.SubscriptionName ?? target.SubscriptionId} · 自动低延迟", $"Follow {currentName ?? target.SubscriptionName ?? target.SubscriptionId} · lowest latency")
            : T($"手选 {target.ProfileIds?.Count ?? 0} 台 · 自动低延迟", $"{target.ProfileIds?.Count ?? 0} selected · lowest latency");

    internal static List<ProfileItem> Candidates(RouteOutboundTarget target, IEnumerable<ProfileItem> profiles, bool subscriptionExists)
    {
        var ids = (target.ProfileIds ?? []).ToHashSet(StringComparer.Ordinal);
        var selected = target.SubscriptionId is { } sub
            ? profiles.Where(p => subscriptionExists && sub.Length > 0 && p.Subid == sub)
            : profiles.Where(p => ids.Contains(p.IndexId));
        return selected.Where(CanSelect).OrderBy(p => p.IndexId, StringComparer.Ordinal).ToList();
    }

    // ReadActiveRules returns detached objects. Rewrite only the effective rules for this generation.
    internal static void Materialize(List<RulesItem> rules, CoreConfigContext context, Func<string, List<ProfileItem>, string> build)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in rules.Where(r => r.OutboundTarget != null))
        {
            var key = rule.OutboundTarget!.Tag();
            if (!tags.TryGetValue(key, out var tag))
            {
                tag = context.RouteTargets.TryGetValue(key, out var candidates) && candidates.Count > 0
                    ? build(key, candidates) : Global.BlockTag;
                tags.Add(key, tag);
            }
            rule.OutboundTag = tag;
        }
    }

    internal static string Availability(RouteOutboundTarget target, bool subscriptionExists, int found, int valid, ECoreType core)
    {
        if (target.SubscriptionId != null && !subscriptionExists)
            return T("订阅已删除，命中此规则的请求将被阻断", "Subscription missing. Matching requests are blocked.");
        if (found == 0 && target.ProfileIds is { Count: > 0 } saved)
            return T($"所选 {saved.Count} 台机器均已失效，命中此规则的请求将被阻断", $"All {saved.Count} saved machines are missing. Matching requests are blocked.");
        if (found == 0)
            return T("没有候选机器，命中此规则的请求将被阻断", "No candidates. Matching requests are blocked.");
        var missing = target.SubscriptionId == null ? Math.Max(0, (target.ProfileIds?.Count ?? 0) - found) : 0;
        var status = T($"{core} 可用 {valid}/{found} 台", $"{valid}/{found} usable with {core}");
        if (missing > 0) status += T($"，{missing} 台已失效", $", {missing} missing");
        if (valid == 0) status += T("，命中此规则的请求将被阻断", ". Matching requests are blocked.");
        return status;
    }
}
