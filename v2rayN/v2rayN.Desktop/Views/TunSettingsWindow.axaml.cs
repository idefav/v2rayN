using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using ServiceLib.Services;
using v2rayN.Desktop.Base;

namespace v2rayN.Desktop.Views;

/// <summary>Native Avalonia implementation of the Local Codex TUN workbench design.</summary>
public partial class TunSettingsWindow : PageBase<TunSettingsViewModel>
{
    private TunSettingsViewModel? _model;
    private string _group = "basic";
    private TunFieldDefinition? _help;
    private readonly List<IDisposable> _bindings = [];
    private readonly List<Action> _detach = [];
    private readonly List<(Grid Grid, Control Label, Control Editor)> _responsiveRows = [];
    private readonly Dictionary<string, Control> _editors = [];
    private readonly HashSet<string> _expandedGroups = [];
    private TextBox? _helpSearchBox;
    public event Action<string>? NavigateRequested;
    public string SelectedGroup => _group;

    public TunSettingsWindow()
    {
        InitializeComponent();
        pageNote.Text = T("独立配置网络接管，保留当前连接方式；保存不会自动开启 TUN。", "Configure capture independently; saving does not enable TUN.");
        showOtherPlatforms.Content = T("查看其他平台选项（只读）", "Show other platforms (read only)");
        searchHelp.Content = T("搜索离线帮助 · F1", "Search offline help · F1");
        jumpToDns.Content = T("DNS 与流量出口", "DNS & traffic egress");
        jumpToTemplate.Content = T("打开配置模板", "Open config templates");
        templateNote.Text = T("当前 TUN 由完整配置模板管理。此页保留的设置不会覆盖模板，请在模板页修改。", "A full template owns TUN. Retained settings here do not override it; edit the template instead.");
        closeHelp.Content = T("关闭说明", "Close help");
        resetGroup.Content = T("恢复本组默认", "Reset this group");
        discardChanges.Content = T("取消修改", "Discard edits");
        saveChanges.Content = T("保存并应用", "Save & apply");
        jumpToError.Content = T("查看字段错误", "Show field errors");
        showOtherPlatforms.IsCheckedChanged += (_, _) =>
        {
            if (_group == "linux" && showOtherPlatforms.IsChecked != true && _model?.Platform != TunPlatform.Linux) _group = "basic";
            BuildNavigation();
            BuildGroup();
        };
        searchHelp.Click += (_, _) => { SelectGroup("help"); _helpSearchBox?.Focus(); };
        jumpToDns.Click += (_, _) => NavigateRequested?.Invoke("DNS");
        jumpToTemplate.Click += (_, _) => NavigateRequested?.Invoke("Templates");
        closeHelp.Click += (_, _) => { helpOverlay.IsVisible = false; searchHelp.Focus(); };
        resetGroup.Click += (_, _) => { _model?.ResetGroup(_group); BuildGroup(); };
        discardChanges.Click += (_, _) => { _model?.Discard(); BuildGroup(); };
        jumpToError.Click += (_, _) => FocusFirstError();
        saveChanges.Click += async (_, _) => await SaveAsync();
        DataContextChanged += (_, _) => _ = AttachAsync(DataContext as TunSettingsViewModel ?? ViewModel);
        Loaded += (_, _) => _ = AttachAsync(ViewModel ?? DataContext as TunSettingsViewModel);
        SizeChanged += (_, _) => AdaptLayout();
        editorScroll.SizeChanged += (_, _) => AdaptLayout();
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.F1) { SelectGroup("help"); _helpSearchBox?.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape && helpOverlay.IsVisible) { helpOverlay.IsVisible = false; e.Handled = true; }
            else if (e.Key == Key.S && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            { e.Handled = true; await SaveAsync(); }
        };
    }

    private async Task AttachAsync(TunSettingsViewModel? model)
    {
        if (model == null || ReferenceEquals(_model, model)) return;
        if (_model != null) _model.PropertyChanged -= ModelChanged;
        _model = model;
        model.PropertyChanged += ModelChanged;
        BuildNavigation();
        BuildGroup();
        UpdateStatus();
        await model.Initialization;
        if (!ReferenceEquals(_model, model)) return;
        BuildNavigation();
        BuildGroup();
        UpdateStatus();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(UpdateStatus); return; }
        if (e.PropertyName == nameof(TunSettingsViewModel.RuleSets) && _group == "routes") BuildGroup();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_model == null) return;
        saveChanges.IsEnabled = _model.CanSave;
        discardChanges.IsEnabled = !_model.IsSaving && _model.IsDirty;
        resetGroup.IsEnabled = !_model.IsSaving && !_model.IsTemplateOwned && _group != "help"
            && (_group != "linux" || _model.Platform == TunPlatform.Linux);
        jumpToError.IsVisible = _model.Fields.Any(f => f.Error.Length > 0);
        statusText.Text = _model.IsSaving ? T("正在校验并应用…", "Checking and applying…")
            : _model.Status.Length > 0 ? _model.Status
            : _model.IsDirty ? T("有未保存修改；应用前会检查配置。", "Unsaved edits; configuration is checked before applying.")
            : T("配置未变化。TUN 启停继续使用连接控制中的开关。", "Unchanged. Use connection controls to start or stop TUN.");
    }

    public void SelectGroup(string group)
    {
        _group = group;
        BuildNavigation();
        BuildGroup();
    }

    private void BuildNavigation()
    {
        groupNavigation.Children.Clear();
        if (_model == null) return;
        foreach (var group in new[] { "basic", "dns", "udp", "routes", "linux", "help" })
        {
            if (group == "linux" && _model.Platform != TunPlatform.Linux && showOtherPlatforms.IsChecked != true) continue;
            var button = new Button
            {
                Name = "tunNav_" + group, Content = TunSettingsCatalog.GroupTitle(group),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10, 9)
            };
            button.Classes.Set("selected", group == _group);
            button.Click += (_, _) => SelectGroup(group);
            groupNavigation.Children.Add(button);
        }
    }

    private void ClearBindings()
    {
        foreach (var binding in _bindings) binding.Dispose();
        _bindings.Clear();
        foreach (var detach in _detach) detach();
        _detach.Clear();
        _responsiveRows.Clear();
        _editors.Clear();
    }

    private void BuildGroup()
    {
        ClearBindings();
        editorBody.Children.Clear();
        if (_model == null) return;
        editorBody.Children.Add(Text(TunSettingsCatalog.GroupTitle(_group), 18, true));
        if (_group == "help") { BuildManual(); AdaptLayout(); return; }
        editorBody.Children.Add(Text(_group switch
        {
            "dns" => T("虚拟 DNS 入口与公共 DNS 上游分开配置。混合代理端口保持原行为。", "Virtual DNS and upstream resolvers are separate. Mixed proxy ports retain their DNS behavior."),
            "udp" => T("调整 TUN 内部会话；出口仍需具备实际 UDP 能力。", "Tune TUN sessions; the outbound still needs real UDP support."),
            "routes" => T("此处决定哪些流量进入 TUN，代理分流在接管后执行。", "Choose traffic entering TUN; proxy routing runs after capture."),
            "linux" => T("按需启用高级接管。防火墙、网卡和命名空间应与现有网络规划一致。", "Enable advanced capture as needed; coordinate firewall, interfaces and namespaces with the existing network."),
            _ => T("保持当前值即可。地址与协议栈变化可能需要重载内核。", "Keep current values unless needed. Address and stack changes may require a core reload."),
        }, 12));
        if (_group == "dns")
        {
            var dns = Text(_model.DnsSummary, 13, true);
            _bindings.Add(dns.Bind(TextBlock.TextProperty, new Binding(nameof(TunSettingsViewModel.DnsSummary)) { Source = _model }));
            editorBody.Children.Add(dns);
        }
        var expert = new StackPanel { Spacing = 8 };
        foreach (var field in _model.Fields.Where(f => f.Definition.Group == _group))
        {
            if (field.Definition.LinuxOnly && _model.Platform != TunPlatform.Linux && showOtherPlatforms.IsChecked != true) continue;
            if (field.Definition.NotOnMac && _model.Platform == TunPlatform.MacOS && showOtherPlatforms.IsChecked != true) continue;
            (field.Definition.Expert ? expert.Children : editorBody.Children).Add(BuildField(field));
        }
        if (expert.Children.Count > 0)
        {
            var expander = new Expander
            {
                Header = T("更多高级选项", "More advanced options"), Content = expert,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsExpanded = _expandedGroups.Contains(_group),
            };
            var group = _group;
            expander.PropertyChanged += (_, e) =>
            {
                if (e.Property == Expander.IsExpandedProperty)
                { if (expander.IsExpanded) _expandedGroups.Add(group); else _expandedGroups.Remove(group); }
            };
            editorBody.Children.Add(expander);
        }
        var first = _model.Fields.FirstOrDefault(f => f.Definition.Group == _group)?.Definition;
        if (first != null && (_help == null || _help.Group != _group)) _help = first;
        RenderHelp();
        editorScroll.Offset = default;
        AdaptLayout();
        UpdateStatus();
    }

    private Control BuildField(TunSettingField field)
    {
        var d = field.Definition;
        var label = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 12, 0) };
        var heading = new WrapPanel();
        heading.Children.Add(Text(d.Title, 14, true));
        var help = new Button { Name = "tunHelp_" + d.Key, Content = T("使用说明", "Help"), FontSize = 12, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(4, 0) };
        AutomationProperties.SetName(help, d.Title + " · " + T("使用说明", "Help"));
        help.Click += (_, _) => ShowHelp(d);
        heading.Children.Add(help);
        label.Children.Add(heading);
        label.Children.Add(Text(d.Hint, 12));
        var error = Text(field.Error, 12);
        error.Name = "tunError_" + d.Key;
        _bindings.Add(error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("WorkspaceDanger")));
        _bindings.Add(error.Bind(TextBlock.TextProperty, new Binding(nameof(TunSettingField.Error)) { Source = field }));
        _bindings.Add(error.Bind(IsVisibleProperty, new Binding(nameof(TunSettingField.Error))
            { Source = field, Converter = new FuncValueConverter<string, bool>(s => !string.IsNullOrEmpty(s)) }));
        label.Children.Add(error);
        var reason = Text(field.UnavailableReason, 12);
        _bindings.Add(reason.Bind(TextBlock.TextProperty, new Binding(nameof(TunSettingField.UnavailableReason)) { Source = field }));
        _bindings.Add(reason.Bind(IsVisibleProperty, new Binding(nameof(TunSettingField.UnavailableReason))
            { Source = field, Converter = new FuncValueConverter<string, bool>(s => !string.IsNullOrEmpty(s)) }));
        label.Children.Add(reason);

        var editor = d.Kind switch
        {
            TunFieldKind.List => BuildList(field),
            TunFieldKind.RuleSets => BuildRuleSets(field),
            _ => BuildChoice(field),
        };
        editor.Name = "tunField_" + d.Key;
        AutomationProperties.SetName(editor, d.Title);
        AutomationProperties.SetHelpText(editor, d.Hint);
        _bindings.Add(editor.Bind(IsEnabledProperty, new Binding(nameof(TunSettingField.Enabled)) { Source = field }));
        _editors[d.Key] = editor;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,260"), RowDefinitions = new RowDefinitions("Auto") };
        grid.Children.Add(label);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        _responsiveRows.Add((grid, label, editor));
        return new Border
        {
            Name = "tunRow_" + d.Key, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 12),
            Child = grid, [!Border.BorderBrushProperty] = this.GetResourceObservable("WorkspaceBorder").ToBinding(),
        };
    }

    private Control BuildChoice(TunSettingField field)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (field.Definition.Kind == TunFieldKind.Choice && field.Choices.Count == 2 && field.Choices.Any(c => c.Value == "true"))
        {
            var toggle = new ToggleSwitch { IsChecked = field.Value == "true", HorizontalAlignment = HorizontalAlignment.Right,
                OnContent = T("开启", "On"), OffContent = T("关闭", "Off") };
            AutomationProperties.SetName(toggle, field.Definition.Title);
            toggle.IsCheckedChanged += (_, _) => field.Value = toggle.IsChecked == true ? "true" : "false";
            Action<TunSettingField> update = _ => toggle.IsChecked = field.Value == "true";
            field.Edited += update;
            _detach.Add(() => field.Edited -= update);
            panel.Children.Add(toggle);
        }
        else
        {
            var choices = new ComboBox
            {
                ItemsSource = field.Choices, HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = T("请选择", "Choose"),
            };
            AutomationProperties.SetName(choices, field.Definition.Title);
            _bindings.Add(choices.Bind(SelectingItemsControl.SelectedItemProperty,
                new Binding(nameof(TunSettingField.SelectedChoice)) { Source = field, Mode = BindingMode.TwoWay }));
            panel.Children.Add(choices);
            if (field.Definition.Kind == TunFieldKind.Preset)
            {
                var custom = new TextBox { PlaceholderText = field.Definition.Example, HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(custom, field.Definition.Title + " · " + T("自定义值", "Custom value"));
                _bindings.Add(custom.Bind(TextBox.TextProperty, new Binding(nameof(TunSettingField.Value)) { Source = field, Mode = BindingMode.TwoWay }));
                _bindings.Add(custom.Bind(IsVisibleProperty, new Binding(nameof(TunSettingField.IsCustom)) { Source = field }));
                panel.Children.Add(custom);
            }
        }
        return panel;
    }

    private Control BuildList(TunSettingField field)
    {
        var panel = new StackPanel { Spacing = 8 };
        var items = new StackPanel { Spacing = 6 };
        var input = new TextBox { PlaceholderText = field.Definition.Example, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(input, field.Definition.Title + " · " + T("添加条目", "Add item"));
        string? editing = null;
        var add = new Button { Content = T("添加", "Add"), HorizontalAlignment = HorizontalAlignment.Right };
        void Render()
        {
            items.Children.Clear();
            foreach (var value in TunSettingsCodec.Lines(field.Value))
            {
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                var text = new TextBlock { Text = value, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(text, value);
                line.Children.Add(text);
                var edit = new Button { Content = T("编辑", "Edit"), Padding = new Thickness(4, 2), FontSize = 11 };
                edit.Click += (_, _) => { editing = value; input.Text = value; add.Content = T("更新", "Update"); input.Focus(); };
                Grid.SetColumn(edit, 1); line.Children.Add(edit);
                var remove = new Button { Content = "×", Padding = new Thickness(7, 2), Margin = new Thickness(4, 0, 0, 0) };
                AutomationProperties.SetName(remove, T("移除 ", "Remove ") + value);
                remove.Click += (_, _) =>
                {
                    field.Value = string.Join("\n", TunSettingsCodec.Lines(field.Value).Where(v => v != value));
                    if (editing == value) { editing = null; input.Text = ""; add.Content = T("添加", "Add"); }
                };
                Grid.SetColumn(remove, 2); line.Children.Add(remove);
                items.Children.Add(line);
            }
            if (items.Children.Count == 0) items.Children.Add(Text(T("自动／无自定义条目", "Automatic / no custom entries"), 12));
        }
        var suggestions = field.Choices.Where(c => c.Value.Length > 0).DistinctBy(c => c.Value).ToList();
        if (suggestions.Count > 0)
        {
            var filter = new TextBox { PlaceholderText = T("搜索预设或已知项", "Search known values") };
            var select = new ComboBox { ItemsSource = suggestions, PlaceholderText = T("从列表选择…", "Choose from list…"), HorizontalAlignment = HorizontalAlignment.Stretch };
            filter.TextChanged += (_, _) => select.ItemsSource = suggestions.Where(c => c.Label.Contains(filter.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
            select.SelectionChanged += (_, _) => { if (select.SelectedItem is TunChoice choice) input.Text = choice.Value; };
            panel.Children.Add(filter);
            panel.Children.Add(select);
        }
        panel.Children.Add(items);
        panel.Children.Add(input);
        add.Click += (_, _) =>
        {
            var value = input.Text?.Trim() ?? "";
            if (value.Length == 0) return;
            var current = TunSettingsCodec.Lines(field.Value);
            if (editing == null) current.Add(value); else current = current.Select(v => v == editing ? value : v).ToList();
            field.Value = string.Join("\n", current.Distinct(StringComparer.Ordinal));
            editing = null; input.Text = ""; add.Content = T("添加", "Add");
        };
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; } };
        panel.Children.Add(add);
        Action<TunSettingField> update = _ => Render();
        field.Edited += update;
        _detach.Add(() => field.Edited -= update);
        Render();
        return panel;
    }

    private Control BuildRuleSets(TunSettingField field)
    {
        var panel = new StackPanel { Spacing = 8 };
        var sources = new StackPanel { Spacing = 8 };
        void Render()
        {
            sources.Children.Clear();
            foreach (var source in _model!.RuleSets)
            {
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(Text(source.Tag + " · " + source.Type + " / " + source.Format, 12, true));
                row.Children.Add(Text(source.Type == "local" ? source.Path ?? "" : source.Url ?? "", 12));
                var remove = new Button { Content = T("移除此来源", "Remove source"), HorizontalAlignment = HorizontalAlignment.Left };
                remove.Click += (_, _) =>
                {
                    _model.SetRuleSets(_model.RuleSets.Where(s => s.Tag != source.Tag).ToList());
                    foreach (var key in new[] { "route_address_set", "route_exclude_address_set" })
                        _model[key].Value = string.Join("\n", TunSettingsCodec.Lines(_model[key].Value).Where(t => t != source.Tag));
                };
                row.Children.Add(remove);
                sources.Children.Add(row);
            }
        }
        panel.Children.Add(sources);
        var tag = new TextBox { PlaceholderText = T("规则集名称，例如 office-net", "Tag, e.g. office-net") };
        var types = new[] { new TunChoice("local", "本地文件", "Local file"), new TunChoice("remote", "远程 HTTP(S) 资源", "Remote HTTP(S) resource") };
        var formats = new[] { new TunChoice("binary", "二进制 SRS", "Binary SRS"), new TunChoice("source", "源文件 JSON", "Source JSON") };
        var type = new ComboBox { ItemsSource = types, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var format = new ComboBox { ItemsSource = formats, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var path = new TextBox { PlaceholderText = T("文件绝对路径或 HTTP(S) 地址", "Absolute file path or HTTP(S) URL") };
        var choose = new Button { Content = T("选择文件…", "Choose file…"), HorizontalAlignment = HorizontalAlignment.Left };
        choose.Click += async (_, _) =>
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;
            var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("选择 IP 规则集", "Choose an IP rule set"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("sing-box") { Patterns = ["*.srs", "*.json"] }],
            });
            var selected = result.FirstOrDefault()?.TryGetLocalPath();
            if (selected == null) return;
            path.Text = selected; type.SelectedItem = types[0];
            format.SelectedItem = selected.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? formats[1] : formats[0];
            if (string.IsNullOrWhiteSpace(tag.Text)) tag.Text = Path.GetFileNameWithoutExtension(selected);
        };
        var hint = Text("", 12);
        _bindings.Add(hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("WorkspaceDanger")));
        var add = new Button { Content = T("添加规则集来源", "Add rule-set source"), HorizontalAlignment = HorizontalAlignment.Right };
        add.Click += (_, _) =>
        {
            var source = new TunRulesetSource { Tag = tag.Text?.Trim() ?? "", Type = (type.SelectedItem as TunChoice)?.Value ?? "local", Format = (format.SelectedItem as TunChoice)?.Value ?? "binary" };
            if (source.Type == "local") source.Path = path.Text?.Trim(); else source.Url = path.Text?.Trim();
            var all = _model!.RuleSets.Append(source).ToList();
            var errors = SingboxTunSettings.Validate(new TunModeItem { Singbox = new() { RuleSets = all } }, _model.Platform)
                .Where(e => e.Key == "rule_sets").ToArray();
            hint.Text = string.Join("\n", errors.Select(e => e.Message));
            if (errors.Length > 0) return;
            _model.SetRuleSets(all);
            tag.Text = ""; path.Text = "";
        };
        foreach (var control in new Control[] { tag, Text(T("来源 · 本地 / 远程", "Source · local / remote"), 12), type,
            Text(T("格式 · SRS 二进制 / JSON 源文件", "Format · binary SRS / source JSON"), 12), format, path, choose, hint, add }) panel.Children.Add(control);
        Action<TunSettingField> update = _ => Render();
        field.Edited += update;
        _detach.Add(() => field.Edited -= update);
        Render();
        return panel;
    }

    private void BuildManual()
    {
        var query = new TextBox { PlaceholderText = T("搜索名称、参数或使用场景", "Search names, parameters or use cases"), Text = _model!.HelpQuery };
        query.Name = "tunHelpSearch";
        _helpSearchBox = query;
        editorBody.Children.Add(query);
        editorBody.Children.Add(Text(T("默认保持现有行为。新增字段优先选择自动；保存配置不会开启 TUN，也不代表网络验证成功。", "Defaults preserve existing behavior. Prefer automatic; saving does not enable TUN or prove connectivity."), 13));
        var export = new Button { Content = T("导出完整离线说明", "Export full offline guide"), HorizontalAlignment = HorizontalAlignment.Left };
        export.Click += async (_, _) =>
        {
            try
            {
                var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage == null) return;
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = T("导出 TUN 使用说明", "Export TUN guide"),
                    SuggestedFileName = TunSettingsText.Chinese ? "tun-settings.zh-Hans.md" : "tun-settings.en.md",
                    FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }],
                });
                if (file == null) return;
                await using var stream = await file.OpenWriteAsync();
                if (stream.CanSeek) stream.SetLength(0);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(TunSettingsCatalog.Manual);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("TunHelpExport", ex);
                _model.Status = T("导出失败：", "Export failed: ") + ex.Message;
            }
        };
        editorBody.Children.Add(export);
        var results = new StackPanel { Spacing = 10 };
        void Search()
        {
            results.Children.Clear();
            var search = query.Text ?? "";
            foreach (var field in TunSettingsCatalog.Fields.Where(f => f.Help.Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                var expander = new Expander
                {
                    Header = field.Title + " · " + field.Key,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                expander.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Expander.IsExpandedProperty && expander.IsExpanded && expander.Content == null)
                        expander.Content = HelpContent(field);
                };
                results.Children.Add(expander);
            }
            if (results.Children.Count == 0) results.Children.Add(Text(T("没有匹配项。", "No matching options."), 13));
        }
        query.TextChanged += (_, _) => { _model.HelpQuery = query.Text ?? ""; Search(); };
        Search();
        editorBody.Children.Add(results);
    }

    private void ShowHelp(TunFieldDefinition field)
    {
        _help = field;
        RenderHelp();
        if (Bounds.Width < 1000) { helpOverlay.IsVisible = true; closeHelp.Focus(); }
    }

    public void ShowHelpForReview(string key) => ShowHelp(TunSettingsCatalog.Fields.First(f => f.Key == key));

    private void RenderHelp()
    {
        if (_help == null) return;
        helpBody.Children.Clear();
        overlayBody.Children.Clear();
        helpBody.Children.Add(HelpContent(_help));
        overlayBody.Children.Add(HelpContent(_help));
    }

    private static Control HelpContent(TunFieldDefinition field)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Text(field.Title, 16, true));
        panel.Children.Add(Text(field.Key, 12));
        foreach (var section in field.Help.Split("\n\n").Skip(1))
        {
            var parts = section.Split('\n', 2);
            panel.Children.Add(Text(parts[0], 12, true));
            if (parts.Length > 1) panel.Children.Add(new SelectableTextBlock { Text = parts[1], TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }
        return panel;
    }

    private async Task SaveAsync()
    {
        if (_model == null || _model.IsSaving) return;
        if (!await _model.SaveAsync()) FocusFirstError();
    }

    private void FocusFirstError()
    {
        var error = _model?.Fields.FirstOrDefault(f => f.Error.Length > 0);
        if (error == null) return;
        _expandedGroups.Add(error.Definition.Group);
        SelectGroup(error.Definition.Group);
        if (_editors.TryGetValue(error.Definition.Key, out var editor))
        {
            editor.BringIntoView();
            editor.Focus();
        }
        ShowHelp(error.Definition);
    }

    private void AdaptLayout()
    {
        var width = Bounds.Width;
        var narrow = width < 640;
        workbench.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : width < 1000 ? "124,*" : "132,*,280");
        workbench.RowDefinitions = new RowDefinitions(narrow ? "Auto,*" : "*");
        groupNavigation.Orientation = narrow ? Orientation.Horizontal : Orientation.Vertical;
        navigationScroll.Margin = new Thickness(0, 0, narrow ? 0 : 12, narrow ? 10 : 0);
        navigationScroll.MaxHeight = narrow ? 110 : double.PositiveInfinity;
        Grid.SetColumn(navigationScroll, 0); Grid.SetRow(navigationScroll, 0);
        Grid.SetColumn(editorScroll, narrow ? 0 : 1); Grid.SetRow(editorScroll, narrow ? 1 : 0);
        helpDock.IsVisible = width >= 1000;
        Grid.SetColumn(helpDock, width >= 1000 ? 2 : 0);
        if (width >= 1000) helpOverlay.IsVisible = false;
        helpOverlay.Width = Math.Max(220, Math.Min(440, width - 8));
        editorScroll.Margin = new Thickness(0, 0, helpDock.IsVisible ? 16 : 0, 0);
        var vertical = editorScroll.Bounds.Width < 480;
        foreach (var (grid, label, editor) in _responsiveRows)
        {
            grid.ColumnDefinitions = new ColumnDefinitions(vertical ? "*" : "*,260");
            grid.RowDefinitions = new RowDefinitions(vertical ? "Auto,Auto" : "Auto");
            Grid.SetColumn(editor, vertical ? 0 : 1); Grid.SetRow(editor, vertical ? 1 : 0);
            editor.Margin = new Thickness(0, vertical ? 8 : 0, 0, 0);
        }
    }

    private static TextBlock Text(string value, double size, bool bold = false) => new()
    {
        Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
    };
    private static string T(string zh, string en) => TunSettingsText.T(zh, en);
}
