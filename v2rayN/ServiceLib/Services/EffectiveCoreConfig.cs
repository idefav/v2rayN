namespace ServiceLib.Services;

public enum CoreLoadResult { Failed, Unchanged, Started }

/// <summary>A snapshot of the generated configs actually supplied to the running processes.</summary>
internal sealed record EffectiveCoreConfig(string Main, ECoreType MainType, string? Pre, ECoreType? PreType)
{
    internal static string Fingerprint(string content, bool generated = false)
    {
        // JSON property ordering/formatting does not change the effective configuration.
        // Preserve array order and every value, including credentials and routing dependencies.
        try
        {
            var json = JsonNode.Parse(content);
            if (generated && json is JsonObject root)
            {
                // Generated log filenames rotate daily and Xray chooses a random macOS TUN
                // interface on each generation. Neither warrants interrupting a running core.
                if (root["log"] is JsonObject log)
                    foreach (var key in new[] { "output", "access", "error" })
                        if (log[key] is JsonValue value && value.TryGetValue<string>(out var path))
                            log[key] = Regex.Replace(path, @"(sbox_|Vaccess_|Verror_)\d{4}-\d{2}-\d{2}(\.txt)$", "$1<date>$2");
                if (root["inbounds"] is JsonArray inbounds)
                    foreach (var inbound in inbounds.OfType<JsonObject>())
                        if (inbound["protocol"]?.GetValue<string>() == "tun"
                            && inbound["settings"] is JsonObject settings
                            && settings["name"] is JsonValue name && name.TryGetValue<string>(out var text)
                            && Regex.IsMatch(text, @"^utun\d+$")) settings["name"] = "<generated-utun>";
            }
            content = Canonicalize(json)?.ToJsonString() ?? "null";
        }
        catch (JsonException) { /* Custom cores may use YAML or another non-JSON format. */ }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Canonicalize(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonicalize).ToArray()),
        _ => node?.DeepClone()
    };

    internal string? ReloadReason(EffectiveCoreConfig? running, bool mainRunning, bool preRunning)
    {
        if (!mainRunning) return "主内核未运行";
        if (Pre != null && !preRunning) return "前置内核未运行";
        if (running == null) return "缺少运行配置快照";
        if (MainType != running.MainType || PreType != running.PreType) return "内核类型或前置内核发生变化";
        if (Main != running.Main) return "主内核有效配置发生变化";
        if (Pre != running.Pre) return "前置内核有效配置发生变化";
        return null;
    }
}
