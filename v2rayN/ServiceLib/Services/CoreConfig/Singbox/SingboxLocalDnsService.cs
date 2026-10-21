namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    private void GenLocalDns()
    {
        // .local is an mDNS namespace, including absolute names with a trailing dot.
        // A direct outbound's explicit domain_resolver bypasses DNS rules, so resolve
        // the destination before routing it directly, even in Global mode.
        const string localDomain = @"(?i)\.local\.?$";
        const string tagPrefix = "mdns-local";
        _coreConfig.dns ??= new();
        _coreConfig.dns.servers ??= [];
        _coreConfig.dns.rules ??= [];
        var tag = tagPrefix;
        for (var suffix = 1; _coreConfig.dns.servers.Any(server => server.tag == tag); suffix++)
        {
            tag = $"{tagPrefix}-{suffix}";
        }
        // On macOS use the system resolver's mDNS support. A separate multicast
        // transport may miss answers already managed by the shared mDNSResponder.
        _coreConfig.dns.servers.Add(new()
        {
            type = OperatingSystem.IsMacOS() ? "local" : "mdns",
            tag = tag,
        });
        _coreConfig.dns.rules.Insert(0, new()
        {
            domain_regex = [localDomain],
            server = tag,
        });

        // Keep sniffing, DNS hijacking and TUN safety rules ahead of these rules,
        // but precede every resolver, mode override and user routing rule.
        var index = _coreConfig.route.rules.FindIndex(rule =>
            rule.action == "resolve" || rule.clash_mode != null);
        if (index < 0)
        {
            index = _coreConfig.route.rules.Count;
        }
        _coreConfig.route.rules.InsertRange(index,
        [
            new() { domain_regex = [localDomain], action = "resolve", server = tag },
            new() { domain_regex = [localDomain], outbound = Global.DirectTag },
        ]);
    }
}
