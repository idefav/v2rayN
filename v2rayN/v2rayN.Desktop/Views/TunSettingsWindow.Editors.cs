using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using ServiceLib.Services;

namespace v2rayN.Desktop.Views;

public partial class TunSettingsWindow
{
    private Control BuildList(TunSettingField field) => ListEditor([field], () => TunSettingsCodec.Lines(field.Value),
        values => field.Value = string.Join("\n", values), field.Definition.Example);

    private Control ListEditor(TunSettingField[] fields, Func<List<string>> read, Action<List<string>> write,
        string example, int minimum = 0)
    {
        var panel = new StackPanel { Spacing = 8 };
        var entries = new StackPanel { Spacing = 8 };
        var add = new Button { Content = LinkText(T("添加条目", "Add entry")), Classes = { "tunLink" }, HorizontalAlignment = HorizontalAlignment.Left };
        var writing = false;
        void Commit(List<string> values)
        {
            writing = true;
            try { write(values); }
            finally { writing = false; }
        }
        TextBox? Render(bool append = false)
        {
            entries.Children.Clear();
            var values = read();
            if (append) values.Add("");
            var boxes = new List<TextBox>();
            foreach (var (value, index) in values.Select((value, index) => (value, index)))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,44") };
                var input = new TextBox { Text = value, PlaceholderText = example, FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 12 };
                AutomationProperties.SetName(input, fields[0].Definition.Title + " · " + (index + 1));
                boxes.Add(input);
                var previousText = value;
                input.TextChanged += (_, _) =>
                {
                    if (input.Text == previousText) return;
                    previousText = input.Text ?? "";
                    Commit(boxes.Select(b => b.Text?.Trim() ?? "").ToList());
                };
                var remove = new Button { Content = "×", Classes = { "tunGhost" }, Width = 44, Padding = new Thickness(8), IsEnabled = values.Count > minimum };
                AutomationProperties.SetName(remove, T("移除条目 ", "Remove entry ") + (index + 1));
                remove.Click += (_, _) => { var current = boxes.Select(b => b.Text?.Trim() ?? "").ToList(); current.RemoveAt(index); Commit(current); Render(); };
                row.Children.Add(input); Grid.SetColumn(remove, 2); row.Children.Add(remove); entries.Children.Add(row);
            }
            if (values.Count == 0)
                entries.Children.Add(Notice("", T("尚未添加自定义条目，保持自动或默认范围。", "No custom entries; retain the automatic or default range.")));
            return boxes.LastOrDefault();
        }
        add.Click += (_, _) => { var input = Render(true); input?.Focus(); };
        panel.Children.Add(entries); panel.Children.Add(add);
        Action<TunSettingField> update = _ => { if (!writing) Render(); };
        foreach (var field in fields) { field.Edited += update; _detach.Add(() => field.Edited -= update); }
        Render();
        return panel;
    }

    private Control BuildAddressFamily(bool ipv6)
    {
        var primary = _model![ipv6 ? "ipv6_address" : "ipv4_address"];
        var extra = _model["additional_addresses"];
        var fallback = ipv6 ? Global.TunIPv6Address.First() : Global.TunIPv4Address.First();
        List<string> Read() => new[] { string.IsNullOrWhiteSpace(primary.Value) ? fallback : primary.Value }
            .Concat(TunSettingsCodec.Lines(extra.Value).Where(a => a.Contains(':') == ipv6)).ToList();
        void Write(List<string> values)
        {
            primary.Value = values.FirstOrDefault() ?? "";
            extra.Value = string.Join("\n", TunSettingsCodec.Lines(extra.Value).Where(a => a.Contains(':') != ipv6).Concat(values.Skip(1)).Where(a => a.Length > 0));
        }
        return FieldRow(primary, ListEditor([primary, extra], Read, Write, fallback, 1), true,
            DesignTitle(primary.Definition), DesignHint(primary.Definition), "additional_addresses");
    }

    private Control BuildDnsAddresses(TunSettingField field)
    {
        var panel = new StackPanel { Spacing = 8 };
        var choices = new[] { new TunChoice("", "自动从接口地址推导", "Derive from interface addresses"), new TunChoice("custom", "自定义入口地址", "Custom entry addresses") };
        var selector = new ComboBox { Name = "tunDnsAddressMode", ItemsSource = choices, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(selector, T("虚拟 DNS 入口", "Virtual DNS entry"));
        var entries = BuildList(field);
        var summary = new StackPanel { Spacing = 4 };
        var caption = Muted(T("接口 → 虚拟 DNS 入口", "Interface → virtual DNS entry"), 12); caption.LineHeight = 20.4; summary.Children.Add(caption);
        var addresses = Text("", 12); addresses.FontFamily = new FontFamily("Menlo, Consolas, monospace"); addresses.LineHeight = 20.4;
        summary.Children.Add(addresses);
        var card = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = summary };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("TunSurface"));
        card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("TunBorder"));
        var changing = false;
        var retained = field.Value;
        void Update()
        {
            changing = true;
            selector.SelectedIndex = string.IsNullOrWhiteSpace(field.Value) ? 0 : 1;
            entries.IsVisible = selector.SelectedIndex == 1;
            card.IsVisible = selector.SelectedIndex == 0;
            changing = false;
            _model!.ValidateDraft(out var draft);
            var peers = SingboxTunSettings.DnsAddresses(draft);
            var source = SingboxTunSettings.Addresses(draft);
            addresses.Text = string.Join(", ", source) + "\n→ " + string.Join(", ", peers);
        }
        selector.SelectionChanged += (_, _) =>
        {
            if (changing) return;
            if (selector.SelectedIndex == 0) { retained = field.Value; field.Value = ""; }
            else
            {
                _model!.ValidateDraft(out var draft);
                field.Value = retained.Length > 0 ? retained : string.Join("\n", SingboxTunSettings.DnsAddresses(draft));
            }
            Update();
        };
        Action<TunSettingField> changed = _ => Update(); field.Edited += changed; _detach.Add(() => field.Edited -= changed);
        PropertyChangedEventHandler modelChanged = (_, e) => { if (e.PropertyName == nameof(TunSettingsViewModel.DnsSummary)) Update(); };
        var subscribedModel = _model!;
        subscribedModel.PropertyChanged += modelChanged; _detach.Add(() => subscribedModel.PropertyChanged -= modelChanged);
        panel.Children.Add(selector); panel.Children.Add(entries); panel.Children.Add(card); Update();
        return panel;
    }

    private Control BuildUidList(bool exclude)
    {
        var uid = _model![exclude ? "exclude_uid" : "include_uid"];
        var range = _model[exclude ? "exclude_uid_range" : "include_uid_range"];
        var editor = ListEditor([uid, range], () => TunSettingsCodec.Lines(uid.Value).Concat(TunSettingsCodec.Lines(range.Value)).ToList(), values =>
        {
            uid.Value = string.Join("\n", values.Where(v => !v.Contains(':') && !v.Contains('-')));
            range.Value = string.Join("\n", values.Where(v => v.Contains(':') || v.Contains('-')).Select(v => v.Replace('-', ':')));
        }, "1000 / 2000:2999");
        return FieldRow(uid, editor, true, DesignTitle(uid.Definition), DesignHint(uid.Definition), range.Definition.Key);
    }

    private Control BuildScopeSelector(string includeKey, string excludeKey, string title, string hint)
    {
        var include = _model![includeKey]; var exclude = _model[excludeKey];
        var panel = new StackPanel { Spacing = 8 };
        var modes = new[] { T("不限制", "Unrestricted"), T("仅包含", "Include only"), T("排除", "Exclude") };
        var mode = new ComboBox { ItemsSource = modes, HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = include.Value.Length > 0 ? 1 : exclude.Value.Length > 0 ? 2 : 0 };
        AutomationProperties.SetName(mode, title);
        var list = new ContentControl();
        void ShowList()
        {
            list.IsVisible = mode.SelectedIndex > 0;
            if (mode.SelectedIndex > 0) list.Content = BuildList(mode.SelectedIndex == 1 ? include : exclude);
        }
        var changing = false;
        mode.SelectionChanged += (_, _) =>
        {
            if (changing) return;
            changing = true;
            try
            {
                if (mode.SelectedIndex != 1) include.Value = "";
                if (mode.SelectedIndex != 2) exclude.Value = "";
                ShowList();
            }
            finally { changing = false; }
        };
        Action<TunSettingField> update = _ =>
        {
            if (changing) return;
            var next = include.Value.Length > 0 ? 1 : exclude.Value.Length > 0 ? 2 : 0;
            if (next == mode.SelectedIndex) return;
            changing = true;
            mode.SelectedIndex = next;
            changing = false; ShowList();
        };
        include.Edited += update; exclude.Edited += update;
        _detach.Add(() => { include.Edited -= update; exclude.Edited -= update; });
        panel.Children.Add(mode); panel.Children.Add(list); ShowList();
        return FieldRow(include, panel, true, title, hint, excludeKey);
    }

    private Control BuildRuleSetSelection(TunSettingField field)
    {
        var panel = new StackPanel { Spacing = 8 };
        var selected = new WrapPanel();
        var selector = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, FontWeight = FontWeight.Normal };
        var choices = new StackPanel();
        var search = new TextBox { PlaceholderText = T("搜索规则集", "Search rule sets"), MinHeight = 44 };
        var body = new StackPanel { Spacing = 8, Width = 280 };
        body.Children.Add(search); body.Children.Add(new ScrollViewer { Content = choices, MaxHeight = 240 });
        var manual = new TextBox { PlaceholderText = T("已有规则集名称", "Existing rule-set tag") };
        var add = new Button { Content = T("添加名称", "Add tag"), MinHeight = 44 };
        body.Children.Add(manual); body.Children.Add(add);
        var flyout = new Flyout { Content = body, Placement = PlacementMode.BottomEdgeAlignedLeft };
        selector.Flyout = flyout;
        void Options()
        {
            choices.Children.Clear();
            var values = TunSettingsCodec.Lines(field.Value);
            foreach (var value in field.Choices.Select(c => c.Value).Concat(values).Distinct().Where(v => v.Length > 0 && v.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)))
            {
                var check = new CheckBox { Content = value, IsChecked = values.Contains(value), MinHeight = 44 };
                check.IsCheckedChanged += (_, _) =>
                {
                    var tags = TunSettingsCodec.Lines(field.Value);
                    if (check.IsChecked == true) tags.Add(value); else tags.Remove(value);
                    field.Value = string.Join("\n", tags.Distinct());
                };
                choices.Children.Add(check);
            }
            if (choices.Children.Count == 0) choices.Children.Add(Muted(T("未找到规则集，请先添加来源。", "No rule sets found. Add a source first."), 12));
        }
        void Render()
        {
            selected.Children.Clear();
            var values = TunSettingsCodec.Lines(field.Value);
            selector.Content = T("选择规则集", "Choose rule sets") + (values.Count > 0 ? $"  ·  {values.Count}  ⌄" : "  ⌄");
            foreach (var value in values)
            {
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                chip.Children.Add(new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
                var remove = new Button { Content = "×", Classes = { "tunGhost" }, Width = 44, Padding = new Thickness(4) };
                AutomationProperties.SetName(remove, T("移除 ", "Remove ") + value);
                remove.Click += (_, _) => field.Value = string.Join("\n", TunSettingsCodec.Lines(field.Value).Where(v => v != value));
                chip.Children.Add(remove);
                var border = new Border { Child = chip, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(8, 0, 0, 0), Margin = new Thickness(0, 0, 8, 8) };
                border.Bind(Border.BackgroundProperty, this.GetResourceObservable("TunSurface")); border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("TunBorder"));
                selected.Children.Add(border);
            }
        }
        search.TextChanged += (_, _) => Options();
        flyout.Opening += (_, _) => Options();
        add.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(manual.Text)) field.Value = string.Join("\n", TunSettingsCodec.Lines(field.Value).Append(manual.Text.Trim()).Distinct()); manual.Text = ""; Options(); };
        Action<TunSettingField> changed = _ => Render(); field.Edited += changed; _detach.Add(() => field.Edited -= changed);
        panel.Children.Add(selector); panel.Children.Add(selected); Render();
        return panel;
    }

    private Control BuildRuleSets(TunSettingField field)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var source in _model!.RuleSets)
        {
            var copy = new StackPanel { Spacing = 4 };
            copy.Children.Add(Text(source.Tag, 14, true));
            copy.Children.Add(Muted(source.Type + " · " + source.Format, 12));
            copy.Children.Add(Muted(source.Type == "local" ? source.Path ?? "" : source.Url ?? "", 12));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,8,Auto") };
            row.Children.Add(copy);
            var edit = new Button { Content = T("编辑", "Edit"), Classes = { "tunGhost" } };
            edit.Click += async (_, _) => await EditRuleSetAsync(source);
            var remove = new Button { Content = "×", Classes = { "tunGhost" }, Width = 44 };
            remove.Click += (_, _) =>
            {
                foreach (var key in new[] { "route_address_set", "route_exclude_address_set" })
                    _model[key].Value = string.Join("\n", TunSettingsCodec.Lines(_model[key].Value).Where(t => t != source.Tag));
                _model.SetRuleSets(_model.RuleSets.Where(s => s.Tag != source.Tag));
            };
            Grid.SetColumn(edit, 1); row.Children.Add(edit); Grid.SetColumn(remove, 3); row.Children.Add(remove);
            var card = new Border { Child = row, Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("TunBorder")); panel.Children.Add(card);
        }
        if (_model.RuleSets.Count == 0) panel.Children.Add(Notice("", T("尚未添加来源。可引用已有规则集，或添加本地文件、远程资源。", "No sources added. Reference existing rule sets or add local/remote resources.")));
        var add = new Button { Name = "tunAddRuleSet", Content = T("添加规则集", "Add rule set"), HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += async (_, _) => await EditRuleSetAsync(null);
        panel.Children.Add(add);
        return panel;
    }

    private async Task EditRuleSetAsync(TunRulesetSource? original)
    {
        if (_model == null || _model.IsTemplateOwned || _model.IsSaving || TopLevel.GetTopLevel(this) is not Window owner) return;
        var name = new TextBox { Text = original?.Tag ?? "", PlaceholderText = "office-net", MinHeight = 44 };
        var types = new[] { new TunChoice("local", "本地文件", "Local file"), new TunChoice("remote", "远程 HTTP(S) 资源", "Remote HTTP(S) resource") };
        var formats = new[] { new TunChoice("binary", "二进制 SRS", "Binary SRS"), new TunChoice("source", "源文件 JSON", "Source JSON") };
        var type = new ComboBox { ItemsSource = types, SelectedIndex = original?.Type == "remote" ? 1 : 0, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        var format = new ComboBox { ItemsSource = formats, SelectedIndex = original?.Format == "source" ? 1 : 0, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        var location = new TextBox { Text = original?.Path ?? original?.Url ?? "", PlaceholderText = T("文件路径或 HTTP(S) URL", "File path or HTTP(S) URL"), MinHeight = 44 };
        var choose = new Button { Content = T("选择文件…", "Choose file…"), MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left };
        var error = Text("", 12); error.Foreground = Brushes.IndianRed;
        var cancel = new Button { Content = T("取消", "Cancel"), MinHeight = 44 };
        var save = new Button { Content = T("保存来源", "Save source"), MinHeight = 44, Classes = { "primary" } };
        var editor = new StackPanel { Spacing = 8, Margin = new Thickness(20) };
        foreach (var (label, control) in new (string, Control)[] { (T("名称", "Name"), name), (T("来源", "Source"), type), (T("格式", "Format"), format), (T("文件或地址", "File or URL"), location) })
        { editor.Children.Add(Text(label, 14, true)); editor.Children.Add(control); }
        editor.Children.Add(choose); editor.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 12) };
        actions.Children.Add(cancel); actions.Children.Add(save);
        var root = new DockPanel();
        var heading = new Border { Padding = new Thickness(20, 12), Child = Text(original == null ? T("添加规则集", "Add rule set") : T("编辑规则集", "Edit rule set"), 16, true) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        root.Children.Add(new ScrollViewer { Content = editor, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var window = new Window { Title = T("规则集来源", "Rule-set source"), Width = Math.Min(500, owner.ClientSize.Width - 32), Height = Math.Min(620, owner.ClientSize.Height - 32), Content = root, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        cancel.Click += (_, _) => window.Close();
        type.SelectionChanged += (_, _) => choose.IsVisible = type.SelectedIndex == 0;
        choose.IsVisible = type.SelectedIndex == 0;
        choose.Click += async (_, _) =>
        {
            try
            {
                var picked = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = T("选择 IP 规则集", "Choose an IP rule set"), AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("sing-box") { Patterns = ["*.srs", "*.json"] }] });
                var path = picked.FirstOrDefault()?.TryGetLocalPath();
                if (path == null) return;
                location.Text = path; type.SelectedIndex = 0; format.SelectedIndex = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (string.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(path);
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        save.Click += (_, _) =>
        {
            if (_model.IsTemplateOwned || _model.IsSaving) return;
            var next = new TunRulesetSource { Tag = name.Text?.Trim() ?? "", Type = types[type.SelectedIndex].Value, Format = formats[format.SelectedIndex].Value };
            if (next.Type == "local") next.Path = location.Text?.Trim(); else next.Url = location.Text?.Trim();
            var sources = _model.RuleSets.Where(s => s.Tag != original?.Tag).Append(next).ToList();
            var issues = SingboxTunSettings.Validate(new TunModeItem { Singbox = new() { RuleSets = sources } }, _model.Platform).Where(i => i.Key == "rule_sets").ToArray();
            error.Text = string.Join("\n", issues.Select(i => i.Message));
            if (issues.Length > 0) return;
            if (original != null && original.Tag != next.Tag)
                foreach (var key in new[] { "route_address_set", "route_exclude_address_set" })
                    _model[key].Value = string.Join("\n", TunSettingsCodec.Lines(_model[key].Value).Select(t => t == original.Tag ? next.Tag : t));
            _model.SetRuleSets(sources); window.Close();
        };
        await window.ShowDialog(owner);
    }
}
