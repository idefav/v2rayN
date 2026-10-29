namespace ServiceLib.ViewModels;

public sealed class TunSettingField : ReactiveObject
{
    private string _value;
    private string _error = "";
    private bool _enabled = true;
    private string _reason = "";
    private bool _customEditing;
    public TunFieldDefinition Definition { get; }
    public List<TunChoice> Choices { get; }
    public event Action<TunSettingField>? Edited;
    public TunSettingField(TunFieldDefinition definition, string value)
    {
        Definition = definition;
        _value = value;
        Choices = [.. definition.Choices];
        if (definition.Kind == TunFieldKind.Preset)
            Choices.Add(new("__custom__", "自定义…", "Custom…"));
    }
    public string Value
    {
        get => _value;
        set
        {
            value ??= "";
            if (_value == value) return;
            this.RaiseAndSetIfChanged(ref _value, value);
            this.RaisePropertyChanged(nameof(SelectedChoice));
            this.RaisePropertyChanged(nameof(IsCustom));
            Edited?.Invoke(this);
        }
    }
    public TunChoice? SelectedChoice
    {
        get => IsCustom ? Choices.FirstOrDefault(c => c.Value == "__custom__") : Choices.FirstOrDefault(c => c.Value == Value);
        set
        {
            if (value == null) return;
            _customEditing = value.Value == "__custom__";
            if (!_customEditing) Value = value.Value;
            this.RaisePropertyChanged(nameof(IsCustom));
            this.RaisePropertyChanged(nameof(SelectedChoice));
        }
    }
    public bool IsCustom => Definition.Kind == TunFieldKind.Preset && (_customEditing || !Choices.Any(c => c.Value == Value));
    public void Restore(string value)
    {
        _customEditing = false;
        Value = value;
        this.RaisePropertyChanged(nameof(IsCustom));
        this.RaisePropertyChanged(nameof(SelectedChoice));
    }
    public string Error { get => _error; set => this.RaiseAndSetIfChanged(ref _error, value); }
    public bool Enabled { get => _enabled; set => this.RaiseAndSetIfChanged(ref _enabled, value); }
    public string UnavailableReason { get => _reason; set => this.RaiseAndSetIfChanged(ref _reason, value); }
}

public partial class TunSettingsViewModel : MyReactiveObject, ICloseable
{
    private readonly Config _appConfig;
    private TunModeItem _original;
    private string _savedSnapshot;
    private bool _suppress;
    private bool _singboxAvailable = true;
    private readonly bool _loadRuntime;
    private readonly HashSet<string> _knownRuleSetTags = new(StringComparer.Ordinal);
    public TunPlatform Platform { get; }
    public IReadOnlyList<TunSettingField> Fields { get; }
    public Task Initialization { get; }
    public event EventHandler? RequestClose;
    public Func<string, Task<bool>>? ApplyAsync { get; set; }
    public ReactiveCommand<RxVoid, RxVoid> SaveCmd { get; }
    [Reactive] public partial bool IsSaving { get; set; }
    [Reactive] public partial bool IsTemplateOwned { get; set; }
    [Reactive] public partial string Status { get; set; } = "";
    [Reactive] public partial bool SaveSucceeded { get; set; }
    [Reactive] public partial string RuntimeSummary { get; set; } = "";
    [Reactive] public partial string RuntimeCore { get; set; } = TunSettingsText.T("内核与版本未确认", "Core/version unavailable");
    [Reactive] public partial string HelpQuery { get; set; } = "";
    public string DraftSnapshot => JsonSerializer.Serialize(Fields.ToDictionary(f => f.Definition.Key, f => f.Value));
    public bool IsDirty => DraftSnapshot != _savedSnapshot;
    public bool CanSave => !IsSaving && !IsTemplateOwned && IsDirty && Fields.All(f => f.Error.Length == 0);
    public bool SingboxAvailable => _singboxAvailable;
    public bool TunEnabled => _appConfig.TunModeItem.EnableTun;
    public IReadOnlyList<string> ModifiedFields
    {
        get
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(_savedSnapshot)!;
            return Fields.Where(f => saved[f.Definition.Key] != f.Value).Select(f => f.Definition.Key).ToArray();
        }
    }
    public TunSettingField this[string key] => Fields.First(f => f.Definition.Key == key);

    public TunSettingsViewModel() : this(AppManager.Instance.Config, SingboxTunSettings.CurrentPlatform, true) { }

    public TunSettingsViewModel(Config config, TunPlatform platform, bool loadRuntime = false)
    {
        _appConfig = config;
        Platform = platform;
        _loadRuntime = loadRuntime;
        _original = JsonUtils.DeepCopy(config.TunModeItem)!;
        var values = TunSettingsCodec.Read(_original);
        Fields = TunSettingsCatalog.Fields.Select(f => new TunSettingField(f, values[f.Key])).ToArray();
        foreach (var field in Fields) field.Edited += _ =>
        {
            if (!_suppress) { Status = ""; SaveSucceeded = false; }
            Refresh();
        };
        _savedSnapshot = DraftSnapshot;
        RefreshRuleSetChoices();
        SaveCmd = ReactiveCommand.CreateFromTask(async () => { await SaveAsync(); });
        this.WhenAnyValue(v => v.IsSaving, v => v.IsTemplateOwned).Subscribe(_ =>
        {
            RefreshAvailability();
            this.RaisePropertyChanged(nameof(CanSave));
        });
        Refresh();
        Initialization = loadRuntime ? InitializeAsync() : Task.CompletedTask;
        if (!loadRuntime)
        {
            RuntimeCore = "sing-box 1.14+ · " + TunSettingsText.T("隔离评审", "isolated fixture");
            RuntimeSummary = $"{platform} · {RuntimeCore}";
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            _singboxAvailable = true;
            IsTemplateOwned = false;
            _knownRuleSetTags.Clear();
            var profile = await AppManager.Instance.GetProfileItem(_appConfig.IndexId);
            ECoreType? core = null;
            if (profile != null)
            {
                // Resolve which core owns TUN without changing or enabling the running setup.
                var inspection = JsonUtils.DeepCopy(_appConfig)!;
                inspection.TunModeItem.EnableTun = true;
                var all = await CoreConfigContextBuilder.BuildAll(inspection, profile);
                var context = all.PreSocksResult?.Context is { IsTunEnabled: true } pre ? pre : all.MainResult.Context;
                core = context.RunCoreType;
                _singboxAvailable = core == ECoreType.sing_box;
                IsTemplateOwned = context.FullConfigTemplate?.Enabled == true;
                AddKnownRuleSets(context);
            }
            string version = "";
            if (core != null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { version = (await new UpdateService(_appConfig, (_, _) => Task.CompletedTask).GetCoreVersion(core.Value, timeout.Token)).ToString(); }
                catch (Exception) { version = TunSettingsText.T("版本未确认", "version unavailable"); }
                if (version == "0.0.0") version = TunSettingsText.T("版本未确认", "version unavailable");
            }
            var state = _appConfig.TunModeItem.EnableTun
                ? TunSettingsText.T("TUN 配置开启；运行接管需通过代理检查验证", "TUN configured on; verify capture with Proxy check")
                : TunSettingsText.T("TUN 已关闭 · 保存不会自动开启", "TUN is off · saving does not enable it");
            RuntimeSummary = $"{Platform} · {(core?.ToString() ?? TunSettingsText.T("尚未选择节点", "No selected node"))} {version}\n{state}";
            RuntimeCore = (core?.ToString() ?? TunSettingsText.T("尚未选择节点", "No selected node")) + " " + version;
            AddRuntimeChoices();
        }
        catch (Exception ex)
        {
            RuntimeSummary = TunSettingsText.T("运行能力信息暂不可用；保存时仍会执行预检。", "Runtime capability information unavailable; saving still performs preflight.");
            Logging.SaveLog("TunSettingsInitialization", ex);
        }
        finally { Refresh(); }
    }

    private void AddKnownRuleSets(CoreConfigContext context)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var directory = Utils.GetBinPath("srss");
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory, "*.srs")) tags.Add(Path.GetFileNameWithoutExtension(file));
        var path = context.RoutingItem?.CustomRulesetPath4Singbox;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            foreach (var source in JsonUtils.Deserialize<List<Ruleset4Sbox>>(File.ReadAllText(path)) ?? [])
                if (!string.IsNullOrEmpty(source.tag)) tags.Add(source.tag);
        foreach (var row in new[] { this["route_address_set"], this["route_exclude_address_set"] })
            foreach (var tag in tags) _knownRuleSetTags.Add(tag);
        RefreshRuleSetChoices();
    }

    private void AddRuntimeChoices()
    {
        if (Platform != TunPlatform.Linux || !OperatingSystem.IsLinux()) return;
        foreach (var key in new[] { "include_interface", "exclude_interface", "include_uid", "exclude_uid", "include_mac_address", "exclude_mac_address", "netns" })
        {
            var field = this[key];
            field.Choices.RemoveAll(c => c.Value != "__custom__" && !field.Definition.Choices.Any(d => d.Value == c.Value));
        }
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var key in new[] { "include_interface", "exclude_interface" })
                    this[key].Choices.Add(new(network.Name, network.Name, network.Name));
            if (File.Exists("/etc/passwd"))
                foreach (var line in File.ReadLines("/etc/passwd"))
                {
                    var parts = line.Split(':');
                    if (parts.Length < 3 || !uint.TryParse(parts[2], out _)) continue;
                    foreach (var key in new[] { "include_uid", "exclude_uid" })
                        this[key].Choices.Add(new(parts[2], $"{parts[0]} · {parts[2]}", $"{parts[0]} · {parts[2]}"));
                }
            if (Directory.Exists("/run/netns"))
                foreach (var entry in Directory.EnumerateFiles("/run/netns"))
                    this["netns"].Choices.Add(new(Path.GetFileName(entry), Path.GetFileName(entry), Path.GetFileName(entry)));
            if (File.Exists("/proc/net/arp"))
                foreach (var line in File.ReadLines("/proc/net/arp").Skip(1))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4 || parts[3] == "00:00:00:00:00:00") continue;
                    foreach (var key in new[] { "include_mac_address", "exclude_mac_address" })
                        this[key].Choices.Add(new(parts[3], $"{parts[3]} · {parts[0]}", $"{parts[3]} · {parts[0]}"));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NetworkInformationException)
        { Logging.SaveLog("TunSettingsChoices", ex); }
    }

    public IReadOnlyList<TunValidationIssue> ValidateDraft(out TunModeItem draft)
    {
        var errors = TunSettingsCodec.Parse(_original, Fields.ToDictionary(f => f.Definition.Key, f => f.Value), Platform, out draft);
        // A different core retains sing-box options without interpreting them.
        if (!_singboxAvailable) errors.RemoveAll(e => TunSettingsCatalog.Fields.Any(f => f.Key == e.Key && f.Singbox));
        return errors;
    }

    public async Task RefreshRuntimeAsync()
    {
        await Initialization;
        if (IsSaving || !_loadRuntime) return;
        if (!IsDirty && JsonUtils.Serialize(_original, false) != JsonUtils.Serialize(_appConfig.TunModeItem, false)) Discard();
        await InitializeAsync();
    }

    public void Refresh()
    {
        if (_suppress) return;
        var errors = ValidateDraft(out _);
        foreach (var field in Fields) field.Error = string.Join("\n", errors.Where(e => e.Key == field.Definition.Key).Select(e => e.Message));
        RefreshAvailability();
        this.RaisePropertyChanged(nameof(IsDirty));
        this.RaisePropertyChanged(nameof(CanSave));
        this.RaisePropertyChanged(nameof(DnsSummary));
    }

    private void RefreshAvailability()
    {
        if (Fields == null) return;
        foreach (var field in Fields)
        {
            var d = field.Definition;
            var reason = IsTemplateOwned ? TunSettingsText.T("当前由完整 TUN 模板管理。", "Managed by the full TUN template.")
                : d.LinuxOnly && Platform != TunPlatform.Linux ? TunSettingsText.T("仅适用于 Linux。", "Linux only.")
                : d.NotOnMac && Platform == TunPlatform.MacOS ? TunSettingsText.T("此项只在 Windows / Linux 生效。", "This option applies to Windows / Linux only.")
                : d.Singbox && !_singboxAvailable ? TunSettingsText.T("当前 TUN 由其他内核负责，此项保留但不应用。", "Another core owns TUN; this value is retained but not applied.")
                : d.Dependency == "ipv6" && this["enable_ipv6"].Value != "true" ? TunSettingsText.T("需先开启 TUN IPv6。", "Enable TUN IPv6 first.")
                : d.Dependency == "dns" && this["dns_mode"].Value == "disabled" ? TunSettingsText.T("DNS 模式关闭时此项不生效。", "Inactive while DNS handling is disabled.")
                : d.Dependency is "auto_route" or "auto_redirect" && this["auto_route"].Value != "true" ? TunSettingsText.T("需先开启自动路由。", "Enable auto route first.")
                : d.Dependency == "auto_redirect" && this["auto_redirect"].Value != "true" ? TunSettingsText.T("需先开启自动重定向。", "Enable auto redirect first.") : "";
            field.UnavailableReason = reason;
            var canTurnOff = d.Key is "auto_redirect" or "exclude_mptcp" && field.Value == "true"
                && Platform == TunPlatform.Linux && _singboxAvailable && !IsTemplateOwned;
            field.Enabled = (reason.Length == 0 || canTurnOff) && !IsSaving;
        }
    }

    public string DnsSummary
    {
        get
        {
            ValidateDraft(out var draft);
            return TunSettingsText.T("虚拟 DNS：", "Virtual DNS: ") + string.Join(", ", SingboxTunSettings.DnsAddresses(draft));
        }
    }

    private IEnumerable<TunSettingField> ResettableFields(string group) => Fields.Where(f => f.Definition.Group == group
        && f.Definition.Key != "rule_sets"
        && (!f.Definition.LinuxOnly || Platform == TunPlatform.Linux)
        && (!f.Definition.NotOnMac || Platform != TunPlatform.MacOS) && (!f.Definition.Singbox || _singboxAvailable));

    private static Dictionary<string, string> DefaultValues() => TunSettingsCodec.Read(new TunModeItem
    {
        Mtu = 9000, Stack = Global.TunStacks.First(), IcmpRouting = Global.TunIcmpRoutingPolicies.First()
    });

    public bool CanResetGroup(string group)
    {
        if (IsTemplateOwned || IsSaving) return false;
        var defaults = DefaultValues();
        return ResettableFields(group).Any(f => f.Value != defaults[f.Definition.Key]);
    }

    public void ResetGroup(string group)
    {
        if (IsTemplateOwned || IsSaving) return;
        SaveSucceeded = false;
        Status = "";
        var defaults = DefaultValues();
        _suppress = true;
        try
        {
            foreach (var field in ResettableFields(group))
                field.Restore(defaults[field.Definition.Key]);
        }
        finally { _suppress = false; RefreshRuleSetChoices(); Refresh(); }
    }

    public void Discard()
    {
        if (IsSaving) return;
        SaveSucceeded = false;
        _original = JsonUtils.DeepCopy(_appConfig.TunModeItem)!;
        var values = TunSettingsCodec.Read(_original);
        _suppress = true;
        try { foreach (var field in Fields) field.Restore(values[field.Definition.Key]); }
        finally { _suppress = false; _savedSnapshot = DraftSnapshot; RefreshRuleSetChoices(); Refresh(); }
        Status = TunSettingsText.T("已取消未保存修改。", "Unsaved edits discarded.");
    }

    public List<TunRulesetSource> RuleSets => JsonUtils.Deserialize<List<TunRulesetSource>>(this["rule_sets"].Value) ?? [];
    public void SetRuleSets(IEnumerable<TunRulesetSource> values)
    {
        this["rule_sets"].Value = JsonUtils.Serialize(values.ToList(), false);
        RefreshRuleSetChoices();
        this.RaisePropertyChanged(nameof(RuleSets));
    }

    private void RefreshRuleSetChoices()
    {
        var tags = _knownRuleSetTags.Concat(RuleSets.Select(s => s.Tag)).Distinct(StringComparer.Ordinal);
        foreach (var key in new[] { "route_address_set", "route_exclude_address_set" })
        {
            var choices = this[key].Choices;
            choices.Clear();
            foreach (var tag in tags) choices.Add(new(tag, tag, tag));
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (IsSaving || IsTemplateOwned) return false;
        SaveSucceeded = false;
        Refresh();
        var errors = ValidateDraft(out var draft);
        if (errors.Count > 0)
        {
            Status = TunSettingsText.T("请先修正标记的字段。", "Correct the highlighted fields first.");
            return false;
        }
        if (!IsDirty) { Status = TunSettingsText.T("配置未变化，保留现有连接。", "Unchanged; existing connections are preserved."); return true; }
        if (JsonUtils.Serialize(_original, false) != JsonUtils.Serialize(_appConfig.TunModeItem, false))
        {
            Status = TunSettingsText.T("TUN 设置已被其他窗口修改，请取消当前草稿并重新打开本页。", "TUN settings changed elsewhere; discard this draft and reopen this page.");
            return false;
        }
        IsSaving = true;
        try
        {
            var candidate = JsonUtils.DeepCopy(_appConfig)!;
            candidate.TunModeItem = draft;
            var checkedCore = false;
            if (_loadRuntime)
            {
                var profile = await AppManager.Instance.GetProfileItem(candidate.IndexId);
                if (profile == null && draft.EnableTun)
                    throw new TunConfigurationException(TunSettingsText.T("请先选择节点，再应用开启状态的 TUN 配置。", "Select a node before applying enabled TUN settings."));
                if (draft.EnableTun && ApplyAsync == null)
                    throw new TunConfigurationException(TunSettingsText.T("当前页面没有连接到内核应用流程，请重新打开设置。", "The core apply handler is unavailable; reopen settings."));
                if (profile != null)
                {
                    // Validate the future TUN graph even while TUN is off, without enabling it.
                    var inspection = JsonUtils.DeepCopy(candidate)!;
                    inspection.TunModeItem.EnableTun = true;
                    var all = await CoreConfigContextBuilder.BuildAll(inspection, profile);
                    if (!all.Success) throw new TunConfigurationException(string.Join("\n", all.CombinedValidatorResult.Errors));
                    var owner = all.PreSocksResult?.Context is { IsTunEnabled: true } pre ? pre : all.MainResult.Context;
                    if (owner.FullConfigTemplate?.Enabled == true)
                    {
                        IsTemplateOwned = true;
                        throw new TunConfigurationException(TunSettingsText.T("完整 TUN 模板已接管配置，请前往模板页修改。", "A full TUN template now owns the configuration; edit it in Config templates."));
                    }
                    if (_singboxAvailable && owner.RunCoreType != ECoreType.sing_box)
                    {
                        _singboxAvailable = false;
                        throw new TunConfigurationException(TunSettingsText.T("当前节点由其他内核负责 TUN，sing-box 专用值将保留但不应用。请检查页面后重新保存。", "Another core now owns TUN; sing-box values are retained but not applied. Review the page and save again."));
                    }
                    foreach (var context in new[] { all.MainResult.Context, all.PreSocksResult?.Context }.OfType<CoreConfigContext>())
                        await TunSettingsPreflight.CheckAsync(context);
                    checkedCore = true;
                }
                if (_appConfig.IndexId != candidate.IndexId) throw new TunConfigurationException(TunSettingsText.T("预检期间节点已变更，请重新保存。", "The node changed during preflight; save again."));
                if (JsonUtils.Serialize(_original, false) != JsonUtils.Serialize(_appConfig.TunModeItem, false))
                    throw new TunConfigurationException(TunSettingsText.T("预检期间 TUN 设置或开关已变更，请重新打开本页。", "TUN settings or its switch changed during preflight; reopen this page."));
            }
            var result = await TunSettingsTransaction.ApplyAsync(_appConfig, draft,
                async () => await ConfigHandler.SaveConfig(_appConfig) == 0, ApplyAsync);
            Status = result.Message;
            if (result.Success && _loadRuntime && !checkedCore)
                Status += "\n" + TunSettingsText.T("尚未选择节点，未执行内核预检。", "No node selected; core preflight has not been performed.");
            if (!result.Success) return false;
            SaveSucceeded = true;
            _original = JsonUtils.DeepCopy(_appConfig.TunModeItem)!;
            _savedSnapshot = DraftSnapshot;
            this.RaisePropertyChanged(nameof(IsDirty));
            RequestClose?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TunSettingsSave", ex);
            Status = TunSettingsText.T("尚未应用，运行配置保持不变。", "Not applied; the running configuration is unchanged.") + "\n" + ex.Message;
            return false;
        }
        finally { IsSaving = false; Refresh(); }
    }
}
