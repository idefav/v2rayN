namespace ServiceLib.Services;

internal sealed record TunListener(string? Name, IReadOnlyList<IPAddress> Addresses);

internal sealed record CoreListenerPlan(IReadOnlyList<CoreListener> Ports, IReadOnlyList<TunListener> Tuns)
{
    internal static CoreListenerPlan Read(string json, ECoreType core)
    {
        if (!CoreListenerCheck.Supports(core)) return new([], []);
        var ports = CoreListenerCheck.Read(json, core);
        var root = JsonNode.Parse(json, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var tuns = new List<TunListener>();
        if (root?["inbounds"] is JsonArray inbounds)
            foreach (var inbound in inbounds.OfType<JsonObject>())
            {
                var xray = core == ECoreType.Xray;
                if (inbound[xray ? "protocol" : "type"]?.GetValue<string>() != "tun") continue;
                var name = (xray ? inbound["settings"]?["name"] : inbound["interface_name"])?.GetValue<string>();
                var addresses = xray ? inbound["settings"]?["gateway"] : inbound["address"];
                var values = addresses is JsonArray list ? list.Select(v => v!.GetValue<string>())
                    : addresses is JsonValue value ? [value.GetValue<string>()] : Array.Empty<string>();
                var parsed = values.Select(v => IPAddress.Parse(v.Split('/')[0])).ToArray();
                if (string.IsNullOrWhiteSpace(name) && parsed.Length == 0)
                    throw new InvalidOperationException("无法验证 TUN 接口：缺少名称或地址 / TUN requires an interface name or address.");
                tuns.Add(new(name, parsed));
            }
        return new(ports, tuns);
    }

    internal static void ValidateTogether(CoreListenerPlan main, CoreListenerPlan? pre)
    {
        var ports = main.Ports.Select(p => (Role: "main", Port: p))
            .Concat((pre?.Ports ?? []).Select(p => (Role: "pre", Port: p))).ToArray();
        for (var i = 0; i < ports.Length; i++)
            for (var j = i + 1; j < ports.Length; j++)
            {
                var a = ports[i]; var b = ports[j];
                if (!CoreListenerCheck.Overlaps(a.Port, b.Port)) continue;
                if (!ListenerBindProbe.Conflicts(a.Port.Address, b.Port.Address, a.Port.Protocol)) continue;
                throw new IOException($"候选监听冲突 / Candidate listener conflict: {a.Role}/{a.Port.Source} {a.Port} ↔ {b.Role}/{b.Port.Source} {b.Port}");
            }
        var tuns = main.Tuns.Concat(pre?.Tuns ?? []).ToArray();
        if (tuns.Where(t => !string.IsNullOrEmpty(t.Name)).GroupBy(t => t.Name).Any(g => g.Count() > 1))
            throw new IOException("候选 TUN 接口名称冲突 / Duplicate candidate TUN interface name.");
    }

    internal static IReadOnlySet<string> ExistingInterfaces() => NetworkInterface.GetAllNetworkInterfaces().Select(n => n.Id).ToHashSet();

    internal IReadOnlySet<string> MatchingInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => Tuns.Any(t => (string.IsNullOrEmpty(t.Name) || n.Name == t.Name)
            && t.Addresses.All(a => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(a)))))
        .Select(n => n.Id).ToHashSet();

    internal bool TunReady(IReadOnlySet<string> before)
        => Tuns.All(t => NetworkInterface.GetAllNetworkInterfaces().Any(n => !before.Contains(n.Id)
            && n.OperationalStatus == OperationalStatus.Up && (string.IsNullOrEmpty(t.Name) || n.Name == t.Name)
            && t.Addresses.All(a => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(a)))));
}
