using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using ServiceLib.Services;
using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>Native controls follow the retained Local Codex layout and control contract.</summary>
public partial class TunSettingsWindow : PageBase<TunSettingsViewModel>
{
    private TunSettingsViewModel? _model;
    private string _group = "basic";
    private TunFieldDefinition? _help;
    private readonly List<IDisposable> _bindings = [];
    private readonly List<Action> _detach = [];
    private readonly List<(Grid Grid, Control Editor, bool Wide)> _responsiveRows = [];
    private readonly Dictionary<string, Control> _editors = [];
    private readonly HashSet<string> _expandedGroups = [];
    private readonly Dictionary<string, (Button Button, TextBlock Index, TextBlock Label, TextBlock Dot)> _navButtons = [];
    private TextBox? _helpSearchBox;
    private Expander? _advanced;
    private Control? _helpTrigger;
    public event Action<string>? NavigateRequested;
    public string SelectedGroup => _group;
    private bool Compact => Bounds.Width <= 760;

    public TunSettingsWindow()
    {
        InitializeComponent();
        FontSize = 14;
        pageNote.Text = T("独立配置网络接管，不改变主界面的 TUN 开关。", "Configure network capture independently of the main TUN switch.");
        runtimeCoreLabel.Text = T("实际 TUN 内核", "Actual TUN core");
        runtimeStateLabel.Text = T("运行状态", "Runtime state");
        runtimeConfigLabel.Text = T("配置状态", "Configuration");
        runtimeNote.Text = T("保存不会自动开启 TUN。\n运行接管需通过代理检查验证。", "Saving does not enable TUN.\nVerify actual capture with Proxy check.");
        navigationLabel.Text = T("TUN 设置", "TUN SETTINGS");
        navigationLabel.LineHeight = 18;
        navigationNoteTitle.Text = T("保存不等于启用", "Save ≠ enable");
        navigationNoteBody.Text = T("TUN 开关保留在主界面。\n⌘ / Ctrl S  保存", "The TUN switch stays in the main window.\nCtrl / ⌘ S  Save");
        showOtherPlatforms.Content = T("查看其他平台选项（只读）", "View other platforms (read only)");
        searchHelp.Content = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M11,7 A4,4 0 1 1 3,7 A4,4 0 1 1 11,7 M10,10 L15,15"),
            StrokeThickness = 1.7, Width = 18, Height = 18, Stretch = Stretch.Uniform,
            [!Avalonia.Controls.Shapes.Shape.StrokeProperty] = this.GetResourceObservable("TunText").ToBinding(),
        };
        AutomationProperties.SetName(searchHelp, T("搜索离线帮助（F1）", "Search offline help (F1)"));
        ToolTip.SetTip(searchHelp, T("搜索离线帮助（F1）", "Search offline help (F1)"));
        jumpToTemplate.Content = T("打开配置模板", "Open config templates");
        templateNote.Text = T("当前 TUN 由完整配置模板管理。此页设置不会覆盖模板，请在模板页修改。", "A full template owns TUN. Edit the template instead of these retained settings.");
        closeHelp.Content = T("关闭说明", "Close help");
        helpDrawerTitle.Text = T("使用说明", "Usage guide");
        resetGroup.Content = T("恢复本组默认", "Reset this group");
        discardChanges.Content = T("取消修改", "Discard edits");
        saveChanges.Content = T("保存并应用", "Save & apply");
        jumpToError.Content = LinkText(T("定位第一个错误", "Go to the first error"));
        showOtherPlatforms.IsCheckedChanged += (_, _) => BuildGroup();
        searchHelp.Click += (_, _) => { SelectGroup("help"); _helpSearchBox?.Focus(); };
        jumpToTemplate.Click += (_, _) => NavigateRequested?.Invoke("Templates");
        closeHelp.Click += (_, _) => CloseHelp();
        helpBackdrop.PointerPressed += (_, _) => CloseHelp();
        resetGroup.Click += async (_, _) =>
        {
            var group = _group;
            if (_model?.CanResetGroup(group) == true && await ConfirmDraftActionAsync(T("恢复本组默认", "Reset this group"),
                T("仅恢复本组适用的选项，保留其他组和规则集来源。恢复后仍需保存。", "Reset this group's applicable options. Other groups and rule-set sources are retained. Save to apply.")))
            { _model.ResetGroup(group); BuildGroup(); }
        };
        discardChanges.Click += async (_, _) =>
        {
            if (_model?.IsDirty == true && await ConfirmDraftActionAsync(T("取消修改", "Discard edits"),
                T("取消所有组尚未保存的修改，恢复到上次保存的配置？", "Discard unsaved edits in all groups and return to the last saved configuration?")))
            { _model.Discard(); BuildGroup(); }
        };
        jumpToError.Click += (_, _) => FocusFirstError();
        saveChanges.Click += async (_, _) => await SaveAsync();
        DataContextChanged += (_, _) => _ = AttachAsync(DataContext as TunSettingsViewModel ?? ViewModel);
        Loaded += (_, _) => _ = AttachAsync(ViewModel ?? DataContext as TunSettingsViewModel);
        SizeChanged += (_, _) => AdaptLayout();
        editorScroll.SizeChanged += (_, _) => AdaptRows();
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.F1) { SelectGroup("help"); _helpSearchBox?.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape && helpOverlay.IsVisible) { CloseHelp(); e.Handled = true; }
            else if (e.Key == Key.S && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            { e.Handled = true; await SaveAsync(); }
        };
    }

    private async Task AttachAsync(TunSettingsViewModel? model)
    {
        if (model == null || ReferenceEquals(_model, model)) return;
        if (_model != null) _model.PropertyChanged -= ModelChanged;
        _model = model;
        ApplyDesignChoices(model);
        model.PropertyChanged += ModelChanged;
        BuildNavigation();
        BuildGroup();
        await model.Initialization;
        if (!ReferenceEquals(_model, model)) return;
        BuildNavigation();
        BuildGroup();
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
        discardChanges.IsEnabled = !_model.IsSaving && _model.IsDirty && !_model.IsTemplateOwned;
        resetGroup.IsEnabled = _group != "help" && _model.CanResetGroup(_group);
        var modified = _model.ModifiedFields;
        var errors = _model.Fields.Where(f => f.Error.Length > 0).ToArray();
        runtimeStateValue.Text = _model.TunEnabled ? T("TUN 配置开启", "TUN configured on") : T("TUN 已关闭", "TUN is off");
        footerDot.IsVisible = _model.IsDirty;
        runtimeConfigValue.Text = _model.IsDirty ? T("有未保存修改", "Unsaved changes")
            : _model.TunEnabled ? T("配置已保存", "Configuration saved") : T("配置待启用", "Pending activation");
        runtimeConfigValue.Classes.Set("tunWarning", !_model.TunEnabled || _model.IsDirty);
        groupChangeCount.Text = _group == "help" ? T("无需联网", "No connection needed")
            : modified.Count(k => _model[k].Definition.Group == _group) is var count && count > 0
                ? T($"{count} 项修改", $"{count} changed") : T("保留现有行为", "Keep existing behavior");
        statusText.Text = _model.IsSaving ? T("正在检查并应用…", "Checking and applying…")
            : _model.IsDirty ? T($"已修改 {modified.Count} 项", $"{modified.Count} changes")
            : T("所有修改已保存", "All changes saved");
        statusHint.Text = _model.TunEnabled ? T("TUN 配置开启 · 接管效果需验证", "TUN configured on · verify capture")
            : T("TUN 保持关闭 · 配置待启用", "TUN remains off · pending activation");
        statusNotice.IsVisible = _model.Status.Length > 0 && _group != "help";
        noticeText.Text = _model.Status;
        statusNotice.Classes.Set("tunSuccess", _model.SaveSucceeded);
        errorNotice.IsVisible = errors.Length > 0 && _group != "help";
        errorSummary.Text = T($"有 {errors.Length} 项需要修正。请检查标记的字段，草稿仍保留。", $"{errors.Length} fields need attention. Your draft is retained.");
        foreach (var (key, nav) in _navButtons)
        {
            var dirty = modified.Any(k => _model[k].Definition.Group == key);
            var errorCount = errors.Count(f => f.Definition.Group == key);
            nav.Dot.Text = errorCount > 0 ? errorCount.ToString() : dirty ? "•" : "";
        }
    }

    public void SelectGroup(string group)
    {
        _group = group;
        foreach (var (key, nav) in _navButtons) nav.Button.Classes.Set("isCurrent", key == group);
        BuildGroup();
    }

    private static string NavTitle(string key) => key switch
    {
        "basic" => T("基础", "General"), "dns" => "DNS", "udp" => "UDP",
        "routes" => T("捕获范围", "Capture"), "linux" => T("Linux 高级", "Linux"),
        _ => T("帮助", "Help")
    };

    private void BuildNavigation()
    {
        groupNavigation.Children.Clear();
        _navButtons.Clear();
        var groups = new[] { "basic", "dns", "udp", "routes", "linux", "help" };
        foreach (var (key, index) in groups.Select((key, index) => (key, index)))
        {
            var number = Muted((index + 1).ToString("00"), 12);
            number.FontFamily = new FontFamily("Menlo, Consolas, monospace");
            number.VerticalAlignment = VerticalAlignment.Center;
            var title = Text(NavTitle(key), 14);
            title.VerticalAlignment = VerticalAlignment.Center;
            var dot = Muted("", 12); dot.VerticalAlignment = VerticalAlignment.Center;
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("20,8,*,Auto") };
            content.Children.Add(number); Grid.SetColumn(title, 2); content.Children.Add(title);
            Grid.SetColumn(dot, 3); content.Children.Add(dot);
            var button = new Button { Name = "tunNav_" + key, Content = content, MinHeight = 44,
                HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "tunNav" } };
            button.Classes.Set("isCurrent", key == _group);
            AutomationProperties.SetName(button, NavTitle(key));
            button.Click += (_, _) => SelectGroup(key);
            groupNavigation.Children.Add(button);
            _navButtons[key] = (button, number, title, dot);
        }
        AdaptNavigation();
    }

    private void ClearBindings()
    {
        foreach (var binding in _bindings) binding.Dispose();
        _bindings.Clear();
        foreach (var detach in _detach) detach();
        _detach.Clear();
        _responsiveRows.Clear();
        _editors.Clear();
        _advanced = null;
    }

    private void BuildGroup()
    {
        ClearBindings();
        editorBody.Children.Clear();
        if (_model == null) return;
        groupTitle.Text = _group switch { "basic" => T("基础设置", "General settings"), "dns" => T("DNS 接管", "DNS handling"), "udp" => T("UDP 行为", "UDP behavior"), "routes" => T("捕获范围", "Capture scope"), "linux" => T("Linux 高级", "Linux advanced"), _ => T("离线帮助", "Offline help") };
        groupDescription.Text = _group switch
        {
            "dns" => T("决定哪些 DNS 请求交给 TUN 处理。", "Choose which DNS requests are handled by TUN."),
            "udp" => T("按需调整映射与过滤；默认沿用现有行为。", "Adjust mapping and filtering only when needed."),
            "routes" => T("设置操作系统交给 TUN 的流量范围。", "Choose which OS traffic enters TUN."),
            "linux" => T("调整重定向、进程范围与网络命名空间。", "Tune redirection, process scope and network namespaces."),
            "help" => T("按选项名称、技术字段或使用场景搜索。", "Search by name, technical field or use case."),
            _ => T("配置路由与网络接口，其余选项可保持自动。", "Configure routing and the interface. Keep other options on automatic.")
        };
        groupTitle.LineHeight = 28;
        platformBar.IsVisible = _group != "help";
        platformLabel.Text = _model.Platform.ToString() == "MacOS" ? "macOS" : _model.Platform.ToString();
        showOtherPlatforms.IsVisible = _model.Platform != TunPlatform.Linux;
        if (_group == "help") { BuildManual(); UpdateStatus(); AdaptLayout(); return; }
        if (_group == "linux" && _model.Platform != TunPlatform.Linux && showOtherPlatforms.IsChecked != true)
        {
            editorBody.Children.Add(Notice(T("这些选项仅适用于 Linux", "These options apply to Linux"),
                T("当前平台不应用 Linux 路由、防火墙与命名空间选项。勾选“查看其他平台选项”可阅读只读说明。", "Linux routing, firewall and namespace options do not apply here. Enable “View other platforms” to inspect them read only.")));
        }
        else
        {
            if (_group == "dns")
            {
                var note = Notice(T("虚拟入口 ≠ 公共 DNS 上游", "Virtual entry ≠ upstream resolver"),
                    T("这里只设置 TUN 如何接收 DNS。公共解析服务及混合端口 DNS 保持原行为。", "Choose how TUN receives DNS. Upstream resolvers and mixed-port DNS retain their behavior."));
                editorBody.Children.Add(note);
            }
            else if (_group == "routes") editorBody.Children.Add(Notice(
                T("捕获范围与代理分流是两步", "Capture and proxy routing are separate"),
                T("未捕获或排除的流量不进入 TUN。规则集必须包含目标 IP CIDR，域名不能替代系统路由。", "Uncaptured or excluded traffic bypasses TUN. System routes need destination IP CIDRs, not domain rules.")));
            var advanced = new StackPanel();
            var addressKeys = new[] { "interface_name", "ipv4_address", "ipv6_address", "additional_addresses", "loopback_address" };
            foreach (var field in _model.Fields.Where(f => f.Definition.Group == _group))
            {
                var d = field.Definition;
                if ((d.LinuxOnly && _model.Platform != TunPlatform.Linux || d.NotOnMac && _model.Platform == TunPlatform.MacOS)
                    && showOtherPlatforms.IsChecked != true) continue;
                if (_group == "basic" && addressKeys.Contains(d.Key)) continue;
                if (_group == "linux" && new[] { "include_interface", "exclude_interface", "include_uid", "include_uid_range", "exclude_uid", "exclude_uid_range", "include_mac_address", "exclude_mac_address" }.Contains(d.Key)) continue;
                (d.Expert ? advanced.Children : editorBody.Children).Add(BuildField(field));
            }
            if (_group == "basic")
            {
                advanced.Children.Insert(0, BuildField(_model["interface_name"]));
                advanced.Children.Insert(1, BuildAddressFamily(false));
                advanced.Children.Insert(2, BuildAddressFamily(true));
                advanced.Children.Add(BuildField(_model["loopback_address"]));
            }
            if (_group == "linux")
            {
                // Keep the presentation's combined selectors over the existing typed model.
                editorBody.Children.Insert(1, BuildScopeSelector("include_interface", "exclude_interface",
                    T("网卡范围", "Interface scope"), T("按来源网卡选择接管范围。", "Select capture by source interface.")));
                editorBody.Children.Insert(2, BuildUidList(false));
                editorBody.Children.Insert(3, BuildUidList(true));
                editorBody.Children.Insert(4, BuildScopeSelector("include_mac_address", "exclude_mac_address",
                    T("MAC 范围", "MAC scope"), T("适合 Linux 网关按局域网设备选择接管范围。", "Select LAN devices on a Linux gateway.")));
            }
            if (advanced.Children.Count > 0)
            {
                _advanced = new Expander { Header = _group == "basic" ? T("接口地址与 TCP 回接", "Interface addresses & TCP loopback") : T("标记、队列与路由编号", "Marks, queue & routing indexes"),
                    Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    IsExpanded = _expandedGroups.Contains(_group), Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(0) };
                var group = _group;
                var expander = _advanced;
                expander.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Expander.IsExpandedProperty)
                    { if (expander.IsExpanded) _expandedGroups.Add(group); else _expandedGroups.Remove(group); }
                };
                editorBody.Children.Add(_advanced);
            }
            if (_group == "dns")
            {
                var mode = _model["dns_mode"];
                string Explain() => mode.Value switch
                {
                    "native" => T("仅发往虚拟入口的请求会被处理；其他 DNS 请求不会因为 native 模式而自动接管。", "Only requests to the virtual entry are handled. Native does not automatically intercept other DNS."),
                    "disabled" => T("TUN 不自动配置或接管 DNS。请确认其他服务已负责解析，混合代理端口不受影响。", "TUN does not automatically configure or intercept DNS. Ensure another service manages resolution; mixed ports are unaffected."),
                    _ => T("自动沿用现有行为；虚拟 DNS 入口与公共上游分别管理。", "Automatic preserves existing behavior. Virtual entry and upstream resolvers are configured separately.")
                };
                var note = Notice("", Explain());
                var explanation = (TextBlock)((StackPanel)note.Child!).Children[0];
                explanation.Name = "tunDnsModeExplanation";
                Action<TunSettingField> changed = _ => explanation.Text = Explain();
                mode.Edited += changed;
                _detach.Add(() => mode.Edited -= changed);
                editorBody.Children.Add(note);
            }
        }
        var first = _model.Fields.FirstOrDefault(f => f.Definition.Group == _group)?.Definition;
        if (first != null && (_help == null || _help.Group != _group)) _help = first;
        RenderHelp();
        editorScroll.Offset = default;
        UpdateStatus();
        AdaptLayout();
    }

    private Control BuildField(TunSettingField field)
    {
        var d = field.Definition;
        var editor = d.Key switch
        {
            "dns_address" => BuildDnsAddresses(field),
            "route_address_set" or "route_exclude_address_set" => BuildRuleSetSelection(field),
            "rule_sets" => BuildRuleSets(field),
            _ => d.Kind == TunFieldKind.List ? BuildList(field) : BuildChoice(field),
        };
        var wide = d.Kind is TunFieldKind.List or TunFieldKind.RuleSets;
        return FieldRow(field, editor, wide);
    }

    private Control FieldRow(TunSettingField field, Control editor, bool wide, string? title = null, string? hint = null, params string[] extraKeys)
    {
        var d = field.Definition;
        var copy = new StackPanel();
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 44 };
        var label = Text(title ?? DesignTitle(d), 14, true); label.VerticalAlignment = VerticalAlignment.Center;
        var help = new Button { Name = "tunHelp_" + d.Key, Content = LinkText(T("使用说明", "Help")), Classes = { "tunLink" } };
        AutomationProperties.SetName(help, (title ?? DesignTitle(d)) + " · " + T("使用说明", "Help"));
        help.Click += (_, _) => ShowHelp(d, help);
        heading.Children.Add(label); Grid.SetColumn(help, 1); heading.Children.Add(help);
        copy.Children.Add(heading);
        var desc = Muted(hint ?? DesignHint(d), 12); desc.LineHeight = 19.8; copy.Children.Add(desc);
        var error = Text("", 12); error.LineHeight = 19;
        _bindings.Add(error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("WorkspaceDanger")));
        var reason = Muted("", 12); reason.LineHeight = 19;
        void Update()
        {
            var fields = new[] { d.Key }.Concat(extraKeys).Select(k => _model![k]).ToArray();
            error.Text = string.Join("\n", fields.Where(f => f.Error.Length > 0).Select(f => f.Error).Distinct());
            error.IsVisible = error.Text.Length > 0;
            reason.Text = field.UnavailableReason; reason.IsVisible = reason.Text.Length > 0;
            editor.IsEnabled = field.Enabled;
        }
        PropertyChangedEventHandler changed = (_, _) => Update();
        foreach (var f in new[] { d.Key }.Concat(extraKeys).Select(k => _model![k]))
        { f.PropertyChanged += changed; _detach.Add(() => f.PropertyChanged -= changed); }
        editor.Name = "tunField_" + d.Key;
        AutomationProperties.SetName(editor, title ?? DesignTitle(d));
        AutomationProperties.SetHelpText(editor, hint ?? d.Hint);
        _editors[d.Key] = editor;
        foreach (var key in extraKeys) _editors.TryAdd(key, editor);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,216"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto") };
        grid.Children.Add(copy);
        Grid.SetColumn(editor, wide ? 0 : 2); Grid.SetRow(editor, wide ? 1 : 0);
        if (wide) { Grid.SetColumnSpan(copy, 3); Grid.SetColumnSpan(editor, 3); }
        grid.Children.Add(editor);
        Grid.SetRow(reason, 2); Grid.SetColumnSpan(reason, 3); grid.Children.Add(reason);
        Grid.SetRow(error, 3); Grid.SetColumnSpan(error, 3); grid.Children.Add(error);
        _responsiveRows.Add((grid, editor, wide));
        Update();
        return new Border { Name = "tunRow_" + d.Key, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 14),
            Child = grid, [!Border.BorderBrushProperty] = this.GetResourceObservable("TunSoftBorder").ToBinding() };
    }

    private Control BuildChoice(TunSettingField field)
    {
        var panel = new StackPanel { Spacing = 8 };
        if (field.Choices.Count == 2 && field.Choices.Any(c => c.Value == "true")
            || field.Definition.Key is "auto_redirect" or "exclude_mptcp")
        {
            var state = Muted("", 12); state.VerticalAlignment = VerticalAlignment.Center;
            var knob = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center };
            var track = new Border { Width = 44, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(3), Child = knob };
            var toggle = new Button { Content = track, Classes = { "tunGhost" }, Width = 48, MinHeight = 44, Padding = new Thickness(2, 8), HorizontalAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetName(toggle, field.Definition.Title);
            void Update()
            {
                var on = field.Value == "true";
                state.Text = on ? T("开启", "On") : T("关闭", "Off");
                track.Bind(Border.BackgroundProperty, this.GetResourceObservable(on ? "TunText" : "TunSurface"));
                track.Bind(Border.BorderBrushProperty, this.GetResourceObservable(on ? "TunText" : "TunMuted"));
                knob.Bind(Border.BackgroundProperty, this.GetResourceObservable(on ? "TunBackground" : "TunMuted"));
                knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            }
            toggle.Click += (_, _) => field.Value = field.Value == "true" ? "false" : "true";
            Action<TunSettingField> update = _ => Update();
            field.Edited += update; _detach.Add(() => field.Edited -= update);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
            line.Children.Add(state); line.Children.Add(toggle); panel.Children.Add(line); Update();
        }
        else
        {
            var combo = new ComboBox { Name = "tunChoice_" + field.Definition.Key, ItemsSource = field.Choices,
                HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44 };
            AutomationProperties.SetName(combo, DesignTitle(field.Definition));
            _bindings.Add(combo.Bind(SelectingItemsControl.SelectedItemProperty,
                new Binding(nameof(TunSettingField.SelectedChoice)) { Source = field, Mode = BindingMode.TwoWay }));
            panel.Children.Add(combo);
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

    private Border Notice(string title, string body)
    {
        var content = new StackPanel();
        if (title.Length > 0)
        {
            var heading = Text(title, 12, true); heading.LineHeight = 20.4; content.Children.Add(heading);
        }
        var text = Text(body, 12); text.LineHeight = 20.4; content.Children.Add(text);
        var border = new Border { Padding = new Thickness(16, 12), Margin = new Thickness(0, 16), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = content };
        border.Bind(Border.BackgroundProperty, this.GetResourceObservable("TunSurface"));
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("TunBorder"));
        return border;
    }

    private void BuildManual()
    {
        var query = new TextBox { PlaceholderText = T("搜索名称、参数或使用场景", "Search options, parameters or use cases"), Text = _model!.HelpQuery, Name = "tunHelpSearch", Margin = new Thickness(0, 16) };
        _helpSearchBox = query;
        editorBody.Children.Add(query);
        var export = new Button { Content = LinkText(T("导出完整离线说明", "Export full offline guide")), Classes = { "tunLink" }, HorizontalAlignment = HorizontalAlignment.Left };
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
            catch (Exception ex) { Logging.SaveLog("TunHelpExport", ex); _model.Status = T("导出失败：", "Export failed: ") + ex.Message; }
        };
        editorBody.Children.Add(export);
        var results = new StackPanel();
        void Search()
        {
            results.Children.Clear();
            var search = query.Text ?? "";
            foreach (var field in TunSettingsCatalog.Fields.Where(f => (f.Help + "\n" + f.Chinese + "\n" + f.English).Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                var copy = new StackPanel { Spacing = 4 };
                var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                top.Children.Add(Text(DesignTitle(field), 14, true));
                var key = Muted(field.Key, 12); key.FontFamily = new FontFamily("Menlo, Consolas, monospace");
                Grid.SetColumn(key, 1); top.Children.Add(key); copy.Children.Add(top);
                var hint = Muted(DesignHint(field), 12); hint.LineHeight = 20.4; copy.Children.Add(hint);
                var result = new Button { Content = copy, Classes = { "tunHelpResult" }, MinHeight = 64,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                result.Click += (_, _) => ShowHelp(field, result);
                results.Children.Add(result);
            }
            if (results.Children.Count == 0) results.Children.Add(Muted(T("没有匹配项。", "No matching options."), 12));
        }
        query.TextChanged += (_, _) => { _model.HelpQuery = query.Text ?? ""; Search(); };
        Search(); editorBody.Children.Add(results);
    }

    private void ShowHelp(TunFieldDefinition field, Control? trigger = null)
    {
        _help = field;
        _helpTrigger = trigger;
        RenderHelp();
        if (Bounds.Width <= 1100) { helpOverlay.IsVisible = true; closeHelp.Focus(); }
    }

    private void CloseHelp()
    {
        helpOverlay.IsVisible = false;
        if (_helpTrigger?.IsEffectivelyVisible == true) _helpTrigger.Focus();
        else if (_navButtons.TryGetValue(_group, out var nav)) nav.Button.Focus();
    }

    public void ShowHelpForReview(string key) => ShowHelp(TunSettingsCatalog.Fields.First(f => f.Key == key));

    private void RenderHelp()
    {
        if (_help == null) return;
        helpBody.Children.Clear(); overlayBody.Children.Clear();
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 28, Margin = new Thickness(0, 0, 0, 16) };
        title.Children.Add(Muted(T("使用说明", "Usage guide"), 12));
        var offline = Muted(T("离线文档", "Offline"), 12); Grid.SetColumn(offline, 1); title.Children.Add(offline);
        helpBody.Children.Add(title);
        helpBody.Children.Add(Muted(NavTitle(_help.Group) + " / " + T("使用说明", "Usage guide"), 12));
        helpBody.Children.Add(HelpContent(_help));
        overlayBody.Children.Add(HelpContent(_help));
    }

    private static Control HelpContent(TunFieldDefinition field)
    {
        var panel = new StackPanel();
        var title = Text(DesignTitle(field), 16, true); title.LineHeight = 22.4; title.Margin = new Thickness(0, 6, 0, 0); panel.Children.Add(title);
        var key = Muted(field.Key, 12); key.FontFamily = new FontFamily("Menlo, Consolas, monospace"); key.LineHeight = 19.2; key.Margin = new Thickness(0, 6, 0, 0); panel.Children.Add(key);
        foreach (var section in field.Help.Split("\n\n").Skip(1))
        {
            var parts = section.Split('\n', 2);
            var heading = Text(parts[0], 12, true); heading.Margin = new Thickness(0, 16, 0, 4); panel.Children.Add(heading);
            if (parts.Length > 1) panel.Children.Add(new SelectableTextBlock { Text = parts[1], TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 21.6, Classes = { "tunMuted" } });
        }
        return panel;
    }

    private async Task SaveAsync()
    {
        if (_model == null || _model.IsSaving) return;
        if (!await _model.SaveAsync()) FocusFirstError();
    }

    private async Task<bool> ConfirmDraftActionAsync(string title, string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        var dialog = new MessageBoxDialog(title, message, title);
        return await dialog.ShowDialog<ButtonResult>(owner) == ButtonResult.Yes;
    }

    private void FocusFirstError()
    {
        var error = _model?.Fields.FirstOrDefault(f => f.Error.Length > 0);
        if (error == null) return;
        _expandedGroups.Add(error.Definition.Group);
        SelectGroup(error.Definition.Group);
        if (_editors.TryGetValue(error.Definition.Key, out var editor)) { editor.BringIntoView(); editor.Focus(); }
        _help = error.Definition;
        RenderHelp();
    }

    private void AdaptNavigation()
    {
        if (_navButtons.Count == 0) return;
        groupNavigation.RowDefinitions = new RowDefinitions(Compact ? "Auto" : "Auto,Auto,Auto,Auto,Auto,Auto");
        groupNavigation.ColumnDefinitions = new ColumnDefinitions(Compact ? "*,*,*,*,*,*" : "*");
        foreach (var (nav, index) in _navButtons.Values.Select((nav, index) => (nav, index)))
        {
            Grid.SetColumn(nav.Button, Compact ? index : 0); Grid.SetRow(nav.Button, Compact ? 0 : index);
            nav.Button.Margin = new Thickness(0, 0, 0, Compact ? 0 : 4);
            nav.Button.CornerRadius = new CornerRadius(Compact ? 0 : 6);
            nav.Button.Classes.Set("compact", Compact);
            nav.Label.FontWeight = nav.Button.Classes.Contains("isCurrent") ? FontWeight.SemiBold : FontWeight.Normal;
            nav.Button.Padding = Compact ? new Thickness(2, 10) : new Thickness(9);
            nav.Index.IsVisible = !Compact; nav.Label.FontSize = Compact ? 12 : 14;
            nav.Label.TextAlignment = Compact ? TextAlignment.Center : TextAlignment.Left;
            nav.Label.LineHeight = 21;
            nav.Index.LineHeight = 18;
            var content = (Grid)nav.Button.Content!;
            content.ColumnDefinitions = new ColumnDefinitions(Compact ? "0,0,*,Auto" : "20,8,*,Auto");
            nav.Label.TextWrapping = Compact ? TextWrapping.Wrap : TextWrapping.NoWrap;
            nav.Button.BorderThickness = Compact ? new Thickness(0, 0, 0, _group == _navButtons.First(kv => kv.Value.Button == nav.Button).Key ? 2 : 0) : new Thickness(1);
        }
    }

    private void AdaptLayout()
    {
        var width = Bounds.Width;
        var small = width <= 760;
        var gutters = small ? 16 : 24;
        pageHeading.Margin = new Thickness(gutters, small ? 16 : 20, gutters, small ? 12 : 16);
        pageNote.FontSize = small ? 12 : 14;
        pageNote.LineHeight = small ? 18 : 21;
        searchHelp.IsVisible = !small;
        workbench.Margin = new Thickness(gutters, 0);
        runtimeGrid.ColumnDefinitions = new ColumnDefinitions(width <= 1100 ? "1.15*,0.8*,0.95*,0" : "1.15*,0.9*,0.95*,1.45*");
        Grid.SetColumn(runtimeNoteCell, 3);
        runtimeNoteCell.IsVisible = width > 1100;
        runtimeCoreValue.FontSize = runtimeStateValue.FontSize = runtimeConfigValue.FontSize = small ? 12 : 14;
        runtimeCoreCell.Margin = small ? new Thickness(12, 8) : new Thickness(16, 10);
        runtimeStateCell.Padding = runtimeConfigCell.Padding = small ? new Thickness(12, 8) : new Thickness(16, 10);
        workbench.ColumnDefinitions = new ColumnDefinitions(small ? "*" : width <= 1100 ? "152,20,*" : width >= 1660 ? "180,24,*,24,300" : "168,24,*,24,284");
        workbench.RowDefinitions = new RowDefinitions(small ? "Auto,*" : "*");
        navigationRail.BorderThickness = small ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0);
        navigationRail.Padding = small ? new Thickness(0) : new Thickness(0, 4, 16, 16);
        navigationLabel.IsVisible = navigationNote.IsVisible = !small;
        Grid.SetRow(navigationRail, 0); Grid.SetColumn(navigationRail, 0);
        Grid.SetColumn(editorScroll, small ? 0 : 2); Grid.SetRow(editorScroll, small ? 1 : 0);
        editorScroll.Padding = new Thickness(0, small ? 16 : 4, 4, small ? 24 : 32);
        helpDock.IsVisible = width > 1100; Grid.SetColumn(helpDock, width > 1100 ? 4 : 0);
        if (width > 1100) helpOverlay.IsVisible = false;
        helpSheet.Width = Math.Min(400, Math.Max(220, width - 24));
        groupTitle.FontSize = small ? 16 : 20;
        groupTitle.LineHeight = small ? 22.4 : 28;
        groupDescription.FontSize = small ? 12 : 14;
        platformBar.Margin = new Thickness(0, small ? 4 : 12, 0, 4);
        platformBadge.IsVisible = !small;
        statusText.FontSize = small ? 12 : 14;
        footerLayout.Margin = new Thickness(gutters, small ? 12 : 0);
        footerLayout.MinHeight = small ? 96 : 76;
        footerLayout.ColumnDefinitions = new ColumnDefinitions(small ? "*" : "*,Auto");
        footerLayout.RowDefinitions = new RowDefinitions(small ? "Auto,Auto" : "*");
        Grid.SetColumn(footerActions, small ? 0 : 1); Grid.SetRow(footerActions, small ? 1 : 0);
        footerState.Margin = new Thickness(0, 0, small ? 0 : 16, small ? 8 : 0);
        footerActions.ColumnDefinitions = new ColumnDefinitions(small ? "*,8,*,8,*" : "Auto,8,Auto,8,Auto");
        foreach (var button in new[] { resetGroup, discardChanges, saveChanges })
        {
            button.FontSize = small ? 12 : 14; button.Padding = new Thickness(small ? 8 : 14, 8);
            button.HorizontalAlignment = small ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        }
        AdaptNavigation(); AdaptRows();
    }

    private void AdaptRows()
    {
        var width = Bounds.Width;
        var columns = width <= 440 ? "*" : width <= 760 ? "*,20," + Math.Max(168, (editorScroll.Bounds.Width - 4) * .42).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : width <= 1100 ? "*,24,228" : width >= 1660 ? "*,24,232" : "*,24,216";
        foreach (var (grid, editor, wide) in _responsiveRows)
        {
            grid.ColumnDefinitions = new ColumnDefinitions(columns);
            var stacked = wide || width <= 440;
            Grid.SetColumn(editor, stacked ? 0 : 2); Grid.SetRow(editor, stacked ? 1 : 0);
            Grid.SetColumnSpan(editor, stacked && width > 440 ? 3 : 1);
            var copy = grid.Children[0];
            Grid.SetColumnSpan(copy, wide && width > 440 ? 3 : 1);
            editor.Margin = new Thickness(0, stacked ? 8 : 0, 0, 0);
        }
    }

    private static TextBlock LinkText(string value) => new() { Text = value, FontSize = 12, TextDecorations = TextDecorations.Underline };
    private static TextBlock Muted(string value, double size) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Classes = { "tunMuted" } };
    private static TextBlock Text(string value, double size, bool bold = false) => new()
    { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
    private static string T(string zh, string en) => TunSettingsText.T(zh, en);
}
