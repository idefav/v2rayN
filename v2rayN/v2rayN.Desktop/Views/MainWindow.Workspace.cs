using Avalonia.Data;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Manager;

namespace v2rayN.Desktop.Views;

public partial class MainWindow
{
    private sealed record NavigationEntry(string Key, string Title, string Glyph);
    private sealed class PageSession(string key, Control view, object model)
    {
        public string Key { get; } = key;
        public Control View { get; } = view;
        public object Model { get; } = model;
        public string Snapshot { get; set; } = EditorSnapshot.Capture(model);
        public Task Ready { get; set; } = Task.CompletedTask;
        public bool IsReady { get; set; }
        public bool IsDraft => Model is OptionSettingViewModel or DNSSettingViewModel or FullConfigTemplateViewModel or GlobalHotkeySettingViewModel or BackupAndRestoreViewModel;
        public bool IsDirty => IsReady && IsDraft && Snapshot != EditorSnapshot.Capture(Model);
    }

    private readonly Dictionary<string, PageSession> _sessions = new();
    private readonly Dictionary<string, ListBoxItem> _navigationItems = new();
    private bool _workspaceInitialized;
    private bool _navigating;
    private bool _logExpanded;
    private string _currentPage = "Servers";
    private string _settingsKey = "Core";
    private MsgView? _logs;
    private ServerWorkspaceView? _servers;
    private ProxyCheckView? _proxyCheck;
    private ThemeSettingView? _appearance;
    private Grid? _settingsWorkspace;
    private ContentControl? _settingsBody;
    private ListBox? _settingsNavigation;
    private Task _pendingSave = Task.CompletedTask;

    public async Task<bool> PrepareExitAsync()
    {
        if (!await ResolveDraftsAsync()) return false;
        if (_currentPage is "Subscriptions" or "Routing") await FlushManagementPageAsync(_currentPage);
        return true;
    }

    internal IEnumerable<string> DirtyDraftFields()
    {
        foreach (var session in _sessions.Values.Where(s => s.IsDirty))
        {
            using var before = System.Text.Json.JsonDocument.Parse(session.Snapshot);
            using var after = System.Text.Json.JsonDocument.Parse(EditorSnapshot.Capture(session.Model));
            foreach (var property in before.RootElement.EnumerateObject())
                if (property.Value.GetRawText() != after.RootElement.GetProperty(property.Name).GetRawText()) yield return session.Key + "." + property.Name;
        }
    }

    private void InitializeWorkspace()
    {
        if (_workspaceInitialized || ViewModel == null) return;
        _workspaceInitialized = true;
        _appearance = new ThemeSettingView();
        TablePresentation.TextIconButton(btnAppearance, WorkspaceText.T("外观与语言", "Appearance and language"),
            "M12,3 L12,1 M12,23 L12,21 M3,12 L1,12 M23,12 L21,12 M5,5 L3,3 M21,21 L19,19 M5,19 L3,21 M21,3 L19,5 M18,12 A6,6 0 1 1 6,12 A6,6 0 1 1 18,12", true);
        btnAppearance.Margin = Utils.IsWindows() ? new Thickness(0, 0, 128, 0) : new Thickness(0);
        var appearanceMenu = new MenuFlyout();
        appearanceMenu.Popup.ShouldUseOverlayLayer = true;
        appearanceMenu.Closed += (_, _) => btnAppearance.Focus();
        foreach (var (theme, label) in new[] { (nameof(ETheme.Light), WorkspaceText.T("浅色", "Light")), (nameof(ETheme.Dark), WorkspaceText.T("深色", "Dark")), (nameof(ETheme.FollowSystem), WorkspaceText.T("跟随系统", "Follow system")) })
        {
            var choice = new MenuItem { Header = label };
            choice.Click += (_, _) => { if (_appearance.ViewModel != null) _appearance.ViewModel.CurrentTheme = theme; };
            appearanceMenu.Items.Add(choice);
        }
        appearanceMenu.Items.Add(new Separator());
        var appearanceSettings = new MenuItem { Header = WorkspaceText.T("外观与语言设置…", "Appearance and language settings…") };
        appearanceSettings.Click += async (_, _) => await OpenSettingsAsync("Appearance");
        appearanceMenu.Items.Add(appearanceSettings);
        btnAppearance.Flyout = appearanceMenu;
        _logs = (MsgView)SimpleViewLocator.Instance.Build(ViewModel.MsgViewModel);
        _logs.ViewModel = ViewModel.MsgViewModel;
        _logs.SetCompact(true);
        _servers = new ServerWorkspaceView { ViewModel = ViewModel.ProfilesViewModel };
        _servers.DetailsOpened += (_, _) => { _logExpanded = false; ResizeWorkspace(); };
        contentStatusBarView.Content = new StatusBarView { ViewModel = ViewModel.StatusBarViewModel };
        txtVersion.Text = "v" + Utils.GetVersionInfo() + " · " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        ToolTip.SetTip(txtVersion, Utils.GetVersion());
        var entries = new[]
        {
            new NavigationEntry("Servers", WorkspaceText.Servers, "M3,4 L21,4 21,10 3,10 Z M3,14 L21,14 21,20 3,20 Z M6,7 L7,7 M6,17 L7,17"),
            new NavigationEntry("Subscriptions", WorkspaceText.GroupsAndSubscriptions, "M6,3 L15,3 20,8 20,21 6,21 Z M15,3 L15,8 20,8 M9,12 L17,12 M9,16 L17,16"),
            new NavigationEntry("Proxies", ResUI.TbProxies, "M4,6 L10,6 10,12 4,12 Z M14,12 L20,12 20,18 14,18 Z M10,9 L17,9 17,12"),
            new NavigationEntry("Routing", WorkspaceText.Routing, "M4,4 L10,4 10,10 4,10 Z M14,14 L20,14 20,20 14,20 Z M7,10 L7,17 14,17 M10,7 L17,7 17,14"),
            new NavigationEntry("Connections", ResUI.TbConnections, "M2,12 L6,12 9,4 13,20 17,9 20,12 23,12"),
            new NavigationEntry("ProxyCheck", ServiceLib.Models.Dto.ProxyCheckText.Title, "M12,2 L21,6 20,15 12,22 4,15 3,6 Z M7,12 L11,16 17,8"),
            new NavigationEntry("Logs", WorkspaceText.Logs, "M4,3 L20,3 20,21 4,21 Z M8,7 L16,7 M8,12 L16,12 M8,17 L13,17")
        };
        foreach (var entry in entries)
        {
            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = StreamGeometry.Parse(entry.Glyph), Width = 24, Height = 24, Fill = null,
                StrokeThickness = 1.6, Stretch = Stretch.Uniform
            };
            path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WorkspaceText"));
            var icon = new Viewbox { Width = 20, Height = 20, Child = path };
            var label = new TextBlock { Text = entry.Title, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var item = new ListBoxItem
            {
                Tag = entry, Padding = new Thickness(10), Margin = new Thickness(0, 2), MinHeight = 44,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { icon, label } }
            };
            ToolTip.SetTip(item, entry.Title);
            _navigationItems.Add(entry.Key, item);
            navigation.Items.Add(item);
        }
        navigation.SelectionChanged += async (_, _) =>
        {
            if (!_navigating && navigation.SelectedItem is ListBoxItem { Tag: NavigationEntry entry }) await NavigateAsync(entry.Key);
        };
        navigation.SelectedItem = _navigationItems["Servers"];
        pageContent.Content = _servers;
        btnImport.Flyout = CreateServerMenu(true);
        btnAddServer.Flyout = CreateServerMenu(false);
        _servers.SetServerMenus(CreateServerMenu(true), CreateServerMenu(false));
        btnDiagnosticLogs.Click += (_, _) => ToggleLogDrawer();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            if (contentStatusBarView.Content is StatusBarView status && status.CloseConnectionControls()) e.Handled = true;
            else if (_servers.CloseDetails()) e.Handled = true;
            else if (_logExpanded) { ToggleLogDrawer(); btnDiagnosticLogs.Focus(); e.Handled = true; }
        }, RoutingStrategies.Bubble);
        ResizeWorkspace();
    }

    private MenuFlyout CreateServerMenu(bool imports)
    {
        var flyout = new MenuFlyout();
        foreach (var source in menuServers.Items.OfType<MenuItem>())
        {
            if ((source.Name?.Contains("Via", StringComparison.Ordinal) == true) != imports) continue;
            var item = new MenuItem { Header = source.Header };
            item.Bind(MenuItem.CommandProperty, new Binding(nameof(MenuItem.Command)) { Source = source });
            item.Bind(IsVisibleProperty, new Binding(nameof(IsVisible)) { Source = source });
            item.Bind(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = source });
            item.Click += (_, _) => { if (item.Command == null) source.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); };
            flyout.Items.Add(item);
        }
        return flyout;
    }

    private void ShowHelpMenu()
    {
        var flyout = new MenuFlyout();
        flyout.Popup.ShouldUseOverlayLayer = true;
        flyout.Closed += (_, _) => btnHelp.Focus();
        var about = new MenuItem { Header = WorkspaceText.T("关于 v2rayN", "About v2rayN") };
        about.Click += async (_, _) =>
        {
            var window = CreateAboutWindow();
            await window.ShowDialog(this);
        };
        flyout.Items.Add(about);
        foreach (var source in menuHelp.Items.OfType<MenuItem>())
        {
            var item = new MenuItem { Header = source.Header, IsEnabled = source.IsEnabled };
            item.Click += (_, _) => source.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            flyout.Items.Add(item);
        }
        foreach (var source in new[] { menuPromotion, menuClose })
        {
            var item = new MenuItem { Header = source.Header };
            item.Click += (_, _) => source.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            flyout.Items.Add(item);
        }
        flyout.ShowAt(btnHelp);
    }

    private Window CreateAboutWindow()
    {
        var close = new Button { Name = "btnAboutClose", Content = WorkspaceText.Close, HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        var body = new StackPanel { Spacing = 16, Children =
        {
            new TextBlock { Text = "v2rayN", FontSize = 24, FontWeight = FontWeight.SemiBold },
            new SelectableTextBlock { Text = Utils.GetVersion(), TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = System.Runtime.InteropServices.RuntimeInformation.OSDescription, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Avalonia · .NET " + Environment.Version, TextWrapping = TextWrapping.Wrap }
        } };
        var content = new DockPanel { Margin = new Thickness(24) };
        close.Margin = new Thickness(0, 16, 0, 0);
        DockPanel.SetDock(close, Dock.Bottom);
        content.Children.Add(close);
        content.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var window = new Window { Title = WorkspaceText.T("关于 v2rayN", "About v2rayN"), Width = 480, Height = 280, MinWidth = 380, MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Content = content, Icon = Icon };
        close.Click += (_, _) => window.Close();
        return window;
    }

    public async Task NavigateAsync(string key)
    {
        if (!_workspaceInitialized || ViewModel == null || _navigating) return;
        if (key == _currentPage && pageContent.Content != null) return;
        if (key == "Connections" && !ViewModel.ShowClashUI) return;
        _navigating = true;
        try
        {
            if (_currentPage == "Settings" && key != "Settings" && !await ResolveDraftsAsync()) return;
            if (_currentPage is "Subscriptions" or "Routing" && _currentPage != key)
                await FlushManagementPageAsync(_currentPage);
            if (_currentPage == "Settings" && key != "Settings" && _settingsBody != null) _settingsBody.Content = null;
            pageContent.Content = null;
            drawerLogs.Content = null;
            _servers?.CloseDetails();
            _logExpanded = false;
            if (contentStatusBarView.Content is StatusBarView status) status.CloseConnectionControls();
            _currentPage = key;
            _logs?.SetCompact(key != "Logs");
            switch (key)
            {
                case "Servers": pageContent.Content = _servers; break;
                case "ProxyCheck": pageContent.Content = _proxyCheck ??= new ProxyCheckView(); break;
                case "Logs": pageContent.Content = _logs; break;
                case "Proxies":
                    var proxies = (ClashProxiesView)SimpleViewLocator.Instance.Build(ViewModel.ClashProxiesViewModel);
                    proxies.ViewModel = ViewModel.ClashProxiesViewModel;
                    proxies.ChangeServer = () => NavigateAsync("Servers");
                    proxies.EditServer = async id =>
                    {
                        var item = await AppManager.Instance.GetProfileItem(id);
                        if (item == null) return;
                        await NavigateAsync("Servers");
                        ViewModel.ProfilesViewModel.SelectedProfile = new ProfileItemModel { IndexId = id, Remarks = item.Remarks, ConfigType = item.ConfigType };
                        await ViewModel.ProfilesViewModel.EditServerCmd.Execute().ToTask();
                    };
                    pageContent.Content = proxies;
                    break;
                case "Connections":
                    var connections = (ClashConnectionsView)SimpleViewLocator.Instance.Build(ViewModel.ClashConnectionsViewModel);
                    connections.ViewModel = ViewModel.ClashConnectionsViewModel;
                    pageContent.Content = connections;
                    break;
                case "Settings":
                    BuildSettingsWorkspace();
                    pageContent.Content = _settingsWorkspace;
                    await SelectSettingsAsync(_settingsKey);
                    break;
                default: pageContent.Content = GetSession(key).View; break;
            }
            navigation.SelectedItem = _navigationItems.GetValueOrDefault(key);
            btnSettings.Classes.Set("selected", key == "Settings");
            if (key != "Settings") txtPageTitle.Text = (_navigationItems.GetValueOrDefault(key)?.Tag as NavigationEntry)?.Title;
            serverActions.IsVisible = false;
            btnDiagnosticLogs.IsVisible = key == "Servers";
            contentStatusBarView.Height = key == "Servers" ? double.NaN : 0;
            contentStatusBarView.ClipToBounds = true;
            contentStatusBarView.Margin = key == "Servers" ? new Thickness(0, 0, 0, 16) : new Thickness(0);
            if (key != "Settings") HotkeyManager.Instance.IsPause = false;
            ResizeWorkspace();
        }
        catch (Exception ex) { NoticeManager.Instance.Enqueue(ex.Message); }
        finally
        {
            navigation.SelectedItem = _navigationItems.GetValueOrDefault(_currentPage);
            _navigating = false;
        }
    }

    private PageSession GetSession(string key)
    {
        if (_sessions.TryGetValue(key, out var session)) return session;
        object model = key switch
        {
            "Subscriptions" => new SubSettingViewModel(), "Routing" => new RoutingSettingViewModel(),
            "Options" => new OptionSettingViewModel(), "DNS" => new DNSSettingViewModel(),
            "Templates" => new FullConfigTemplateViewModel(), "Hotkeys" => new GlobalHotkeySettingViewModel(),
            "Backup" => new BackupAndRestoreViewModel(),
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        var view = SimpleViewLocator.Instance.Build(model);
        view.DataContext = model;
        if (view is SubSettingWindow subscriptions)
        {
            subscriptions.IsEmbedded = true;
            subscriptions.UpdateSubscriptionAsync = ViewModel!.UpdateSubscriptionProcess;
        }
        session = new PageSession(key, view, model);
        _sessions.Add(key, session);
        session.Ready = InitializeSessionAsync(session);
        if (model is ServiceLib.Base.ICloseable editable)
            editable.RequestClose += (_, _) => _pendingSave = SaveSessionAsync(session);
        if (view is IWorkspacePage page)
            page.CloseRequested += result =>
            {
                if (result is true && session.IsDraft) _pendingSave = SaveSessionAsync(session);
                else if (session.IsDraft)
                {
                    _sessions.Remove(key);
                    if (_settingsBody != null) _settingsBody.Content = null;
                    _ = SelectSettingsAsync(_settingsKey);
                }
                _ = NavigateAsync("Servers");
            };
        return session;
    }

    private static async Task InitializeSessionAsync(PageSession session)
    {
        session.View.IsEnabled = false;
        var initialization = session.Model switch
        {
            OptionSettingViewModel vm => vm.Initialization,
            DNSSettingViewModel vm => vm.Initialization,
            FullConfigTemplateViewModel vm => vm.Initialization,
            _ => Task.CompletedTask
        };
        try
        {
            await initialization;
            session.Snapshot = EditorSnapshot.Capture(session.Model);
            session.IsReady = true;
        }
        finally { session.View.IsEnabled = true; }
    }

    private async Task SaveSessionAsync(PageSession session)
    {
        try
        {
            if (session.Model is not OptionSettingViewModel { OnlySourcesChanged: true })
                await ViewModel!.ApplySettingsChangesAsync(session.Key);
            session.Snapshot = EditorSnapshot.Capture(session.Model);
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        }
        catch (Exception ex) { NoticeManager.Instance.Enqueue(ex.Message); }
    }

    private async Task FlushManagementPageAsync(string key)
    {
        if (!_sessions.TryGetValue(key, out var session)) return;
        var modified = session.Model is SubSettingViewModel { IsModified: true } or RoutingSettingViewModel { IsModified: true };
        if (modified) await ViewModel!.ApplySettingsChangesAsync(key);
        _sessions.Remove(key);
    }

    private async Task<bool> ResolveDraftsAsync()
    {
        await Task.WhenAll(_sessions.Values.Select(s => s.Ready));
        var dirty = _sessions.Values.Where(s => s.IsDirty).ToArray();
        if (dirty.Length == 0) return true;
        var choice = await UI.ShowUnsavedChanges();
        if (choice == 0) return false;
        foreach (var session in dirty)
        {
            if (choice == 1)
            {
                switch (session.Model)
                {
                    case OptionSettingViewModel vm: await vm.SaveCmd.Execute().ToTask(); break;
                    case DNSSettingViewModel vm: await vm.SaveCmd.Execute().ToTask(); break;
                    case FullConfigTemplateViewModel vm: await vm.SaveCmd.Execute().ToTask(); break;
                    case GlobalHotkeySettingViewModel vm: await vm.SaveCmd.Execute().ToTask(); break;
                    case BackupAndRestoreViewModel vm: await vm.SaveCmd.Execute().ToTask(); break;
                }
                await _pendingSave;
                if (session.IsDirty) return false; // Validation did not complete a save.
            }
            else _sessions.Remove(session.Key);
        }
        return true;
    }

    private void BuildSettingsWorkspace()
    {
        if (_settingsWorkspace != null) return;
        _settingsNavigation = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        var sections = new List<(string Key, string Title)>
        {
            ("Core", WorkspaceText.T("Core 基础", "Core settings")), ("Application", WorkspaceText.T("应用设置", "Application")),
            ("SystemProxy", ResUI.menuSystemproxy), ("Tun", WorkspaceText.T("TUN 模式", "TUN mode")),
            ("Engines", WorkspaceText.T("核心分配", "Core assignment")), ("DNS", WorkspaceText.T("DNS 与流量出口", "DNS & traffic egress")),
            ("Templates", WorkspaceText.T("配置模板", "Config templates")), ("Appearance", WorkspaceText.T("外观", "Appearance")),
            ("Backup", ResUI.menuBackupAndRestore), ("Updates", ResUI.menuCheckUpdate),
            ("Sources", ServiceLib.Services.UpdateSourceText.Title),
            ("Utilities", WorkspaceText.T("应用工具", "Application tools"))
        };
        if (Utils.IsWindows()) sections.Add(("Hotkeys", ResUI.menuGlobalHotkeySetting));
        foreach (var (key, title) in sections) _settingsNavigation.Items.Add(new ListBoxItem
        { Tag = key, Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap }, Padding = new Thickness(12, 10) });
        _settingsNavigation.SelectionChanged += async (_, _) =>
        {
            if (_settingsNavigation.SelectedItem is ListBoxItem { Tag: string selected } && selected != _settingsKey) await SelectSettingsAsync(selected);
        };
        _settingsBody = new ContentControl { MaxWidth = 1040, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        _settingsWorkspace = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*") };
        var rail = new ScrollViewer { Content = _settingsNavigation, Margin = new Thickness(0, 0, 16, 0) };
        _settingsWorkspace.Children.Add(rail);
        Grid.SetColumn(_settingsBody, 1);
        _settingsWorkspace.Children.Add(_settingsBody);
    }

    public async Task OpenSettingsAsync(string key)
    {
        await NavigateAsync("Settings");
        if (_currentPage == "Settings") await SelectSettingsAsync(key);
    }

    private Task SelectSettingsAsync(string key)
    {
        if (_settingsBody == null || ViewModel == null) return Task.CompletedTask;
        var navigationItem = _settingsNavigation!.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == key);
        if (navigationItem == null) return Task.CompletedTask;
        if (key == _settingsKey && _settingsBody.Content != null) return Task.CompletedTask;
        _settingsBody.Content = null;
        _settingsKey = key;
        HotkeyManager.Instance.IsPause = key == "Hotkeys";
        switch (key)
        {
            case "Appearance": _settingsBody.Content = _appearance; break;
            case "Backup":
                _settingsBody.Content = GetSession("Backup").View;
                break;
            case "Updates":
                _checkUpdateView ??= new CheckUpdateView { ViewModel = ViewModel.CheckUpdateViewModel };
                _settingsBody.Content = _checkUpdateView;
                break;
            case "Utilities": _settingsBody.Content = BuildApplicationTools(); break;
            case "DNS" or "Templates" or "Hotkeys": _settingsBody.Content = GetSession(key).View; break;
            default:
                var options = (OptionSettingWindow)GetSession("Options").View;
                _settingsBody.Content = options;
                options.SelectSection(Array.IndexOf(new[] { "Core", "Application", "SystemProxy", "Tun", "Engines", "Sources" }, key));
                break;
        }
        _settingsNavigation.SelectedItem = navigationItem;
        txtPageTitle.Text = (navigationItem.Content as TextBlock)?.Text;
        ResizeWorkspace();
        return Task.CompletedTask;
    }

    private Control BuildApplicationTools()
    {
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(8) };
        body.Children.Add(new TextBlock { Text = WorkspaceText.T("应用与本地数据", "Application and local data"), Classes = { "sectionTitle" } });
        foreach (var source in new[] { menuOpenTheFileLocation, menuClearServerStatistics, menuRebootAsAdmin, menuSettingsSetUWP })
            body.Children.Add(ToolAction(source));
        body.Children.Add(new TextBlock { Text = ResUI.menuRegionalPresets, Classes = { "sectionTitle" }, Margin = new Thickness(0, 16, 0, 0) });
        foreach (var source in new[] { menuRegionalPresetsDefault, menuRegionalPresetsRussia, menuRegionalPresetsIran })
            body.Children.Add(ToolAction(source));
        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static Button ToolAction(MenuItem source)
    {
        var button = new Button { Content = source.Header, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200 };
        button.Bind(Button.CommandProperty, new Binding(nameof(MenuItem.Command)) { Source = source });
        button.Bind(IsVisibleProperty, new Binding(nameof(IsVisible)) { Source = source });
        button.Bind(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = source });
        button.Click += (_, _) => { if (button.Command == null) source.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); };
        return button;
    }

    private void UpdateProxyNavigation(bool enabled)
    {
        foreach (var key in new[] { "Connections" })
        {
            if (!_navigationItems.TryGetValue(key, out var item)) continue;
            item.IsEnabled = enabled;
            ToolTip.SetTip(item, enabled ? (item.Tag as NavigationEntry)?.Title : WorkspaceText.Unavailable);
        }
        if (!enabled && _currentPage == "Connections") _ = NavigateAsync("Servers");
    }

    private void ToggleLogDrawer()
    {
        _logExpanded = !_logExpanded;
        if (_logExpanded) _servers?.CloseDetails();
        ResizeWorkspace();
    }

    private void ResizeWorkspace()
    {
        var compact = Bounds.Width <= 900;
        var sidebarWidth = 196 + Math.Max(0, AppManager.Instance.Config.UiItem.CurrentFontSize - 14) * 6;
        shellGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 64 : sidebarWidth);
        brandLabel.IsVisible = !compact;
        txtVersion.IsVisible = !compact;
        foreach (var item in _navigationItems.Values)
            if (item.Content is StackPanel panel && panel.Children.LastOrDefault() is TextBlock label)
            {
                label.IsVisible = !compact;
                label.MaxWidth = sidebarWidth - 64;
            }
        TablePresentation.TextIconButton(btnSettings, ResUI.menuSetting,
            "M9,3 L15,3 16,6 19,8 21,11 19,14 18,17 15,18 14,21 10,21 9,18 6,17 4,14 3,11 5,8 8,6 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12", compact);
        TablePresentation.TextIconButton(btnHelp, ResUI.menuHelp,
            "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M8,8 C8,4 16,4 16,8 C16,11 12,10 12,14 M12,17 L12,18", compact);
        ToolTip.SetTip(btnSettings, ResUI.menuSetting);
        ToolTip.SetTip(btnHelp, ResUI.menuHelp);
        logDrawer.IsVisible = _currentPage == "Servers" && _logExpanded;
        logDrawer.Height = double.NaN;
        logDrawer.ClipToBounds = true;
        logDrawer.Margin = _currentPage == "Servers" ? new Thickness(0, 12, 0, 12) : new Thickness(0);
        drawerLogs.Content = _currentPage == "Servers" && _logExpanded ? _logs : null;
        drawerLogs.Height = Math.Max(80, Math.Min(200, Bounds.Height * 0.25));
        if (contentStatusBarView.Content is StatusBarView status)
            status.SetCompact(Bounds.Width <= 900 || Bounds.Height < 700 || FontSize > 18 && Bounds.Height < 1100);
        btnToggleLogs.Content = "×";
        _servers?.SetCompact(compact);
        if (_settingsWorkspace != null)
            _settingsWorkspace.ColumnDefinitions[0].Width = new GridLength(Bounds.Width < 900 ? 120 : 180);
    }
}
