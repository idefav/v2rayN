namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    private void ConvertGeo2Ruleset()
    {
        static void AddRuleSets(List<string> ruleSets, List<string>? rule_set)
        {
            if (rule_set != null)
            {
                ruleSets.AddRange(rule_set);
            }
        }
        var geosite = "geosite";
        var geoip = "geoip";
        var ruleSets = new List<string>();
        foreach (var inbound in _coreConfig.inbounds.Where(i => i.type == "tun"))
        {
            AddRuleSets(ruleSets, inbound.route_address_set);
            AddRuleSets(ruleSets, inbound.route_exclude_address_set);
        }
        var tunReferences = ruleSets.ToHashSet(StringComparer.Ordinal);

        //convert route geosite & geoip to ruleset
        foreach (var rule in _coreConfig.route.rules.Where(t => t.geosite?.Count > 0).ToList() ?? [])
        {
            rule.rule_set ??= [];
            rule.rule_set.AddRange(rule?.geosite?.Select(t => $"{geosite}-{t}").ToList() ?? []);
            rule.geosite = null;
            AddRuleSets(ruleSets, rule.rule_set);
        }
        foreach (var rule in _coreConfig.route.rules.Where(t => t.geoip?.Count > 0).ToList() ?? [])
        {
            rule.rule_set ??= [];
            rule.rule_set.AddRange(rule?.geoip?.Select(t => $"{geoip}-{t}").ToList() ?? []);
            rule.geoip = null;
            AddRuleSets(ruleSets, rule.rule_set);
        }

        //convert dns geosite & geoip to ruleset
        foreach (var rule in _coreConfig.dns?.rules.Where(t => t.geosite?.Count > 0).ToList() ?? [])
        {
            rule.rule_set ??= [];
            rule.rule_set.AddRange(rule?.geosite?.Select(t => $"{geosite}-{t}").ToList() ?? []);
            rule.geosite = null;
        }
        foreach (var rule in _coreConfig.dns?.rules.Where(t => t.geoip?.Count > 0).ToList() ?? [])
        {
            rule.rule_set ??= [];
            rule.rule_set.AddRange(rule?.geoip?.Select(t => $"{geoip}-{t}").ToList() ?? []);
            rule.geoip = null;
        }
        foreach (var dnsRule in _coreConfig.dns?.rules.Where(t => t.rule_set?.Count > 0).ToList() ?? [])
        {
            AddRuleSets(ruleSets, dnsRule.rule_set);
        }
        //rules in rules
        foreach (var item in _coreConfig.dns?.rules.Where(t => t.rules?.Count > 0).Select(t => t.rules).ToList() ?? [])
        {
            foreach (var item2 in item ?? [])
            {
                AddRuleSets(ruleSets, item2.rule_set);
            }
        }

        //load custom ruleset file
        List<Ruleset4Sbox> customRulesets = [];

        var routing = context.RoutingItem;
        if (!string.IsNullOrEmpty(routing?.CustomRulesetPath4Singbox))
        {
            var result = EmbedUtils.LoadResource(routing.CustomRulesetPath4Singbox);
            if (result.IsNotEmpty())
            {
                customRulesets = (JsonUtils.Deserialize<List<Ruleset4Sbox>>(result) ?? [])
                    .Where(t => t.tag != null)
                    .Where(t => t.type != null)
                    .Where(t => t.format != null)
                    .ToList();
            }
        }

        // Definitions and references belong to one graph; a selected TUN tag must be loaded
        // even when no ordinary route or DNS rule happens to reference it.
        foreach (var source in context.FullConfigTemplate?.Enabled == true ? [] : _config.TunModeItem.Singbox?.RuleSets ?? [])
        {
            if (!ruleSets.Contains(source.Tag)) continue;
            if (customRulesets.Any(r => r.tag == source.Tag))
                throw new TunConfigurationException($"rule_sets: {TunSettingsText.T("规则集名称与当前路由重复", "Rule-set tag conflicts with the current routing definitions")}: {source.Tag}");
            var localPath = source.Type == "local" ? Path.GetFullPath(source.Path!, Utils.GetBinConfigPath()) : null;
            if (localPath != null && !File.Exists(localPath))
                throw new TunConfigurationException($"rule_sets: {TunSettingsText.T("找不到规则集文件", "Rule-set file not found")}: {source.Tag}");
            customRulesets.Add(new()
            {
                tag = source.Tag, type = source.Type, format = source.Format,
                path = localPath, url = source.Type == "remote" ? source.Url : null,
                http_client = source.Type == "remote" ? Global.SingboxSrsDownloadHttpClientTag : null,
            });
        }

        //Local srs files address
        var localSrss = Utils.GetBinPath("srss");

        //Add ruleset srs
        _coreConfig.route.rule_set = [];
        var containRemoteRuleset = false;
        foreach (var item in new HashSet<string>(ruleSets))
        {
            if (item.IsNullOrEmpty())
            { continue; }
            var customRuleset = customRulesets.FirstOrDefault(t => t.tag != null && t.tag.Equals(item));
            if (customRuleset?.type == "remote") containRemoteRuleset = true;
            if (customRuleset is null)
            {
                var pathSrs = Path.Combine(localSrss, $"{item}.srs");
                if (File.Exists(pathSrs))
                {
                    customRuleset = new()
                    {
                        type = "local",
                        format = "binary",
                        tag = item,
                        path = pathSrs,
                    };
                }
                else
                {
                    if (tunReferences.Contains(item) && !item.StartsWith("geoip-", StringComparison.Ordinal) && !item.StartsWith("geosite-", StringComparison.Ordinal))
                        throw new TunConfigurationException($"rule_sets: {TunSettingsText.T("请先添加规则集来源", "Add the rule-set source before selecting its tag")}: {item}");
                    containRemoteRuleset = true;

                    var srsUrl = string.IsNullOrEmpty(_config.ConstItem.SrsSourceUrl)
                        ? Global.SingboxRulesetUrl
                        : _config.ConstItem.SrsSourceUrl;

                    customRuleset = new()
                    {
                        type = "remote",
                        format = "binary",
                        tag = item,
                        url = string.Format(srsUrl, item.StartsWith(geosite) ? geosite : geoip, item),
                        http_client = Global.SingboxSrsDownloadHttpClientTag,
                    };
                }
            }
            _coreConfig.route.rule_set.Add(customRuleset);
        }

        if (containRemoteRuleset)
        {
            _coreConfig.http_clients ??= [];
            _coreConfig.http_clients.Add(new()
            {
                tag = Global.SingboxSrsDownloadHttpClientTag,
                detour = Global.ProxyTag,
            });
        }
    }
}
