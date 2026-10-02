namespace ServiceLib.Services;

public sealed class SshConfigurationException(string message) : Exception(message);

public static class SshDnsPolicy
{
    // Inspect the final graph, including selectors, chains and user templates.
    public static string Apply(string content, bool customDns)
    {
        var config = JsonNode.Parse(content)!;
        var nodes = (config["outbounds"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (!nodes.Any(o => o["type"]?.GetValue<string>() == "ssh")) return content;
        var outbounds = nodes.Where(o => o["tag"] != null)
            .ToDictionary(o => o["tag"]!.GetValue<string>(), o => o);
        bool UsesSsh(string? tag, HashSet<string> seen)
        {
            if (tag == null || !seen.Add(tag) || !outbounds.TryGetValue(tag, out var outbound)) return false;
            if (outbound["type"]?.GetValue<string>() == "ssh") return true;
            if (UsesSsh(outbound["detour"]?.GetValue<string>(), seen)) return true;
            return (outbound["outbounds"] as JsonArray ?? [])
                .Any(child => UsesSsh(child?.GetValue<string>(), seen));
        }
        var final = config["route"]?["final"]?.GetValue<string>()
            ?? outbounds.Keys.FirstOrDefault();
        foreach (var server in (config["dns"]?["servers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var detour = server["detour"]?.GetValue<string>();
            // Typed DNS without a detour uses a direct dialer; legacy DNS follows routing.
            var legacy = server["address"]?.GetValue<string>();
            if (legacy is "local" or "fakeip" || legacy?.StartsWith("rcode://") == true) continue;
            if (!UsesSsh(detour ?? (legacy != null ? final : null), [])) continue;
            var type = server["type"]?.GetValue<string>();
            if (type == "udp" && !customDns)
            {
                server["type"] = "tcp";
                continue;
            }
            if (type is "udp" or "quic" or "h3" || server["http3"]?.GetValue<bool>() == true
                || (legacy != null && !legacy.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
                    && !legacy.StartsWith("tls://", StringComparison.OrdinalIgnoreCase)
                    && !legacy.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                throw new SshConfigurationException(SshStrings.DnsUnsupported);
        }
        return JsonUtils.Serialize(config);
    }
}
