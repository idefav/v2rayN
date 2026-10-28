namespace ServiceLib.ViewModels;

public partial class UpdateSourceItem : ReactiveObject
{
    public string Key { get; }
    public string Label { get; }
    public string DefaultValue { get; }
    public string DefaultLabel => UpdateSourceText.Default + DefaultValue;
    public string Hint { get; }
    public List<string> Presets { get; }
    [Reactive] public partial string? Value { get; set; }
    public ReactiveCommand<RxVoid, RxVoid> ResetCmd { get; }

    public UpdateSourceItem(string key, string label, string defaultValue, string? value, string hint, IEnumerable<string>? presets = null)
    {
        Key = key; Label = label; DefaultValue = defaultValue; Value = value; Hint = hint;
        Presets = presets?.ToList() ?? [defaultValue];
        ResetCmd = ReactiveCommand.Create(() => { Value = ""; });
    }

    public static List<UpdateSourceItem> Create(Config config)
    {
        var rows = UpdateSources.Programs.Select(t => new UpdateSourceItem(t.ToString(), t == ECoreType.sing_box ? "sing-box" : t.ToString(), Global.CoreUrls[t],
            config.CheckUpdateItem.RepositoryOverrides?.GetValueOrDefault(t.ToString()), UpdateSourceText.RepositoryHelp,
            t == ECoreType.sing_box ? [Global.CoreUrls[t], "idefav/sing-box"] : [Global.CoreUrls[t]])).ToList();
        rows.Add(new("Geo", ResUI.TbSettingsGeoFilesSource, Global.GeoUrl, config.ConstItem.GeoSourceUrl, Global.GeoUrl, Global.GeoFilesSources));
        rows.Add(new("Srs", ResUI.TbSettingsSrsFilesSource, Global.SingboxRulesetUrl, config.ConstItem.SrsSourceUrl, Global.SingboxRulesetUrl, Global.SingboxRulesetSources));
        rows.Add(new("Routing", ResUI.TbSettingsRoutingRulesSource, UpdateSourceText.Builtin, config.ConstItem.RouteRulesTemplateSourceUrl, "https://example.com/routing.json", Global.RoutingRulesSources));
        return rows;
    }

    public static void Apply(IReadOnlyList<UpdateSourceItem> rows, Config config)
    {
        // Validate the complete draft before changing any configuration.
        var repositories = rows.Take(4).Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .ToDictionary(r => r.Key, r => UpdateSources.NormalizeRepository(r.Value!));
        foreach (var type in UpdateSources.Programs)
            if (string.Equals(repositories.GetValueOrDefault(type.ToString()), Global.CoreUrls[type], StringComparison.OrdinalIgnoreCase))
                repositories.Remove(type.ToString());
        UpdateSources.ValidateTemplate(rows[4].Value, 1);
        UpdateSources.ValidateTemplate(rows[5].Value, 2);
        UpdateSources.ValidateTemplate(rows[6].Value, 0);
        config.CheckUpdateItem.RepositoryOverrides = repositories;
        config.ConstItem.GeoSourceUrl = rows[4].Value?.Trim() ?? "";
        config.ConstItem.SrsSourceUrl = rows[5].Value?.Trim() ?? "";
        config.ConstItem.RouteRulesTemplateSourceUrl = rows[6].Value?.Trim() ?? "";
    }
}
