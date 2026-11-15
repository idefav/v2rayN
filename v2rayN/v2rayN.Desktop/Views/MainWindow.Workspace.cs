using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Controls.Notifications;
using DialogHostAvalonia;
using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Manager;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

public partial class MainWindow
{
    private sealed class PageSession(string key, Control view, object model)
    {
        public string Key { get; } = key;
        public Control View { get; } = view;
        public object Model { get; } = model;
        public string Snapshot { get; set; } = EditorSnapshot.Capture(model);
        public Task Ready { get; set; } = Task.CompletedTask;
        public bool IsReady { get; set; }
        public bool IsDraft => Model is OptionSettingViewModel or TunSettingsViewModel or DNSSettingViewModel
            or FullConfigTemplateViewModel or GlobalHotkeySettingViewModel or BackupAndRestoreViewModel or ThemeSettingViewModel;
        public bool IsDirty => IsReady && IsDraft && (Model is TunSettingsViewModel tun ? tun.IsDirty : Snapshot != EditorSnapshot.Capture(Model));
    }

    private readonly Dictionary<string, PageSession> _sessions = new();
    private readonly Dictionary<string, Button> _navigationItems = new();
    private readonly Dictionary<string, Expander> _navigationGroups = new();
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private bool _workspaceInitialized;
    private string _currentPage = "Home";
    private string _settingsKey = "Application";
    private ConnectionFeedbackView? _connectionFeedback;
    private ConnectionFeedbackView? _settingsFeedback;
    private ContentControl? _settingsFeedbackHost;
    internal ConnectionFeedbackView? ConnectionFeedback => _settingsFeedback?.PresentationEnabled == true ? _settingsFeedback : _connectionFeedback;
    private MsgView? _logs;
    private ServerWorkspaceView? _servers;
    private ProxyCheckView? _proxyCheck;
    private ThemeSettingView? _appearance;
    private Grid? _settingsWorkspace;
    private ContentControl? _settingsBody;
    private StackPanel? _settingsNavigation;
    private ScrollViewer? _settingsRail;
    private StackPanel? _searchResults;
    private Border? _searchPanel;
    private TextBlock? _settingsPageTitle;
    private TextBlock? _settingsDescription;
    private TextBlock? _settingsConnectionState;
    private StackPanel? _settingsActions;
    private ScrollViewer? _settingsActionsScroll;
    private Task _pendingSave = Task.CompletedTask;
    private Window? _settingsWindow;
    private WindowNotificationManager? _settingsNotifications;
    private bool _settingsClosePending;

    internal Window? SettingsWindow => _settingsWindow;
    internal Window WorkspaceWindow => _currentPage == "Settings" && _settingsWindow?.IsVisible == true ? _settingsWindow : this;

    internal string CurrentWorkspacePage => _currentPage == "Home" ? "Home" : _settingsKey;
    internal IReadOnlyCollection<string> SettingsPageKeys => _navigationItems.Keys;

    public async Task<bool> PrepareExitAsync()
    {
        await _navigationGate.WaitAsync();
        try
        {
            if (!await ResolveDraftsAsync()) return false;
            if (_currentPage == "Settings") await FlushManagementPageAsync(_settingsKey);
            return true;
        }
        finally { _navigationGate.Release(); }
    }

    internal IEnumerable<string> DirtyDraftFields()
    {
        foreach (var session in _sessions.Values.Where(s => s.IsDirty))
        {
            using var before = System.Text.Json.JsonDocument.Parse(session.Snapshot);
            using var after = System.Text.Json.JsonDocument.Parse(EditorSnapshot.Capture(session.Model));
            foreach (var property in before.RootElement.EnumerateObject())
                if (!after.RootElement.TryGetProperty(property.Name, out var value) || property.Value.GetRawText() != value.GetRawText())
                    yield return session.Key + "." + property.Name;
        }
    }

    private void InitializeWorkspace()
    {
        if (_workspaceInitialized || ViewModel == null) return;
        _workspaceInitialized = true;
        ViewModel.UseConnectionWorkspace = true;
        _appearance = new ThemeSettingView();
        brandIcon.Content = WorkspaceIcons.Create("brand", 22);
        WorkspaceIcons.SetButton(btnSettings, "settings", ResUI.menuSetting);
        WorkspaceIcons.SetButton(btnReturnConnection, "back", WorkspaceText.T("连接窗口", "Connection window"));
        settingsSearch.PlaceholderText = WorkspaceText.T("搜索设置、功能或关键词", "Search settings and features");
        AutomationProperties.SetName(settingsSearch, settingsSearch.PlaceholderText);
        settingsSearch.TextChanged += (_, _) => RenderSettingsSearch();
        settingsSearch.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { settingsSearch.Text = string.Empty; e.Handled = true; }
            else if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(settingsSearch.Text))
            {
                var match = WorkspacePages.Search(settingsSearch.Text).FirstOrDefault();
                if (match != null) { e.Handled = true; await OpenSettingsAsync(match.Key); }
            }
            else if (e.Key == Key.Down && _searchResults?.Children.OfType<Button>().FirstOrDefault() is { } first)
            { first.Focus(); e.Handled = true; }
        };
        _logs = (MsgView)SimpleViewLocator.Instance.Build(ViewModel.MsgViewModel);
        _logs.ViewModel = ViewModel.MsgViewModel;
        _logs.SetCompact(false);
        _servers = new ServerWorkspaceView { ViewModel = ViewModel.ProfilesViewModel };
        _servers.SetServerMenus(CreateServerMenu(true), CreateServerMenu(false));
        var status = new StatusBarView
        {
            ViewModel = ViewModel.StatusBarViewModel,
            ConnectRequested = () => ViewModel.Reload("用户连接"),
            DisconnectRequested = ViewModel.DisconnectAsync,
            SpeedDisplayChanged = ViewModel.SetRealTimeSpeedAsync,
            ManageServersRequested = () => OpenSettingsAsync("Servers"),
            RoutingRequested = () => OpenSettingsAsync("Routing"),
            DiagnosticsRequested = () => OpenSettingsAsync("Logs"),
            ApplyRequested = () => ViewModel.Reload("用户应用已保存设置")
        };
        status.RuntimeSummaryChanged += text => { if (_settingsConnectionState != null) _settingsConnectionState.Text = text; };
        contentStatusBarView.Content = status;
        _connectionFeedback = new ConnectionFeedbackView();
        _connectionFeedback.CancelRequested += () => ViewModel?.CancelConnectionTransition();
        _connectionFeedback.RetryRequested += RetryConnectionFeedbackAsync;
        _connectionFeedback.DiagnosticsRequested += () => OpenSettingsAsync("Logs");
        status.FeedbackHost.Content = _connectionFeedback;
        _settingsFeedback = new ConnectionFeedbackView(state: _connectionFeedback.State, showRest: false);
        _settingsFeedback.CancelRequested += () => ViewModel?.CancelConnectionTransition();
        _settingsFeedback.RetryRequested += RetryConnectionFeedbackAsync;
        _settingsFeedback.DiagnosticsRequested += () => OpenSettingsAsync("Logs");
        _settingsFeedback.Present(false);
        Activated += (_, _) => RouteConnectionFeedback();
        PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty || args.Property == WindowStateProperty) RouteConnectionFeedback(); };
        status.SetWindowHeaderInset(56);
        _serverDrawer = new ConnectionDrawer(this, status, connectionHeader);
        BuildSettingsWorkspace();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            if (_searchPanel?.IsVisible == true) { settingsSearch.Text = string.Empty; settingsSearch.Focus(); e.Handled = true; }
            else if (status.CloseConnectionControls()) e.Handled = true;
            else if (_servers.CloseDetails()) e.Handled = true;
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

    public Task NavigateAsync(string key) => NavigateWorkspaceAsync(key == "Home" ? null : key == "Settings" ? _settingsKey : key);
    public Task OpenSettingsAsync(string key) => NavigateWorkspaceAsync(key);

    private void EnsureSettingsWindow()
    {
        if (_settingsWindow != null) return;
        // Reuse the complete management workspace and its bindings in a separate top level.
        ((Panel)settingsSearch.Parent!).Children.Remove(settingsSearch);
        pageHeader.Children.Remove(btnReturnConnection);
        pageHeader.Children.Remove(txtPageTitle);
        ((Panel)pageContent.Parent!).Children.Remove(pageContent);
        var header = new DockPanel { MinHeight = 56, Margin = new Thickness(16, 0) };
        DockPanel.SetDock(btnReturnConnection, Dock.Left);
        DockPanel.SetDock(settingsSearch, Dock.Right);
        btnReturnConnection.Margin = new Thickness(0, 0, 16, 0);
        settingsSearch.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(btnReturnConnection);
        header.Children.Add(settingsSearch);
        header.Children.Add(txtPageTitle);
        var bar = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        bar.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WorkspaceSurface"));
        bar.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WorkspaceBorder"));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(pageContent, 1);
        root.Children.Add(bar); root.Children.Add(pageContent);
        _settingsFeedbackHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Right,
            Width = 400, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(16, 0, 16, 12), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = _settingsFeedback };
        Grid.SetRow(_settingsFeedbackHost, 1); root.Children.Add(_settingsFeedbackHost);
        var window = new Window
        {
            Name = "workspaceSettingsWindow", Title = "v2rayN · " + ResUI.menuSetting,
            DataContext = ViewModel,
            Width = 1120, Height = 780, MinWidth = 800, MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new DialogHost { Identifier = "settingsWorkspace", CloseOnClickAway = false,
                DisableOpeningAnimation = true, Content = root }
        };
        if (ConfigHandler.GetWindowSizeItem(_config, "SettingsWindow") is { } size)
        { window.Width = Math.Max(window.MinWidth, size.Width); window.Height = Math.Max(window.MinHeight, size.Height); }
        var names = new NameScope();
        foreach (var control in new Control[] { settingsSearch, btnReturnConnection, txtPageTitle, pageContent }) names.Register(control.Name!, control);
        NameScope.SetNameScope(window, names);
        _settingsWindow = window;
        window.Opened += (_, _) =>
        {
            if ((window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is not { } screen) return;
            var scale = screen.Scaling > 0 ? screen.Scaling : 1;
            window.Width = Math.Max(window.MinWidth, Math.Min(window.Width, screen.WorkingArea.Width / scale));
            window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, screen.WorkingArea.Height / scale - 32));
        };
        _settingsNotifications = new WindowNotificationManager(window) { MaxItems = 3, Position = NotificationPosition.TopRight };
        window.KeyDown += MainWindow_KeyDown;
        window.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            if (_searchPanel?.IsVisible == true) { settingsSearch.Text = string.Empty; settingsSearch.Focus(); e.Handled = true; }
            else if (_servers?.CloseDetails() == true) e.Handled = true;
        }, RoutingStrategies.Bubble);
        window.SizeChanged += (_, _) => ResizeWorkspace();
        window.Activated += (_, _) => { HotkeyManager.Instance.IsPause = _settingsKey == "Hotkeys"; RouteConnectionFeedback(); };
        window.PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty || args.Property == WindowStateProperty) RouteConnectionFeedback(); };
        window.Deactivated += (_, _) => HotkeyManager.Instance.IsPause = false;
        window.Closing += async (_, e) =>
        {
            StoreSettingsWindowSize();
            if (AllowsShutdown || AppManager.Instance.IsExiting) return;
            e.Cancel = true;
            if (_settingsClosePending) return;
            _settingsClosePending = true;
            try { await NavigateAsync("Home"); }
            finally { _settingsClosePending = false; }
        };
    }

    private void StoreSettingsWindowSize()
    {
        if (_settingsWindow is { WindowState: WindowState.Normal } window)
            ConfigHandler.SaveWindowSizeItem(_config, "SettingsWindow", window.Width, window.Height);
    }

    private void ShowSettingsWindow()
    {
        EnsureSettingsWindow();
        _settingsWindow!.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private async Task NavigateWorkspaceAsync(string? key)
    {
        if (!_workspaceInitialized || ViewModel == null) return;
        if (key != null && !WorkspacePages.Available.Any(p => p.Key == key)) return;
        await _navigationGate.WaitAsync();
        try
        {
            if (AppManager.Instance.IsExiting) return;
            if (key == null && _currentPage == "Home" || key == _settingsKey && _currentPage == "Settings")
            {
                if (key != null) ShowSettingsWindow();
                else ShowHideWindow(true);
                settingsSearch.Text = string.Empty; FocusWorkspaceNavigation(); return;
            }
            if (!await ResolveDraftsAsync()) return;
            if (_currentPage == "Settings") await FlushManagementPageAsync(_settingsKey);
            // A clean editor must reopen from the latest saved values, including changes
            // made by home switches or another settings page while it was detached.
            foreach (var session in _sessions.Values.Where(s => s.IsDraft).ToArray()) _sessions.Remove(session.Key);
            _servers?.CloseDetails();
            (contentStatusBarView.Content as StatusBarView)?.CloseConnectionControls();
            _settingsBody!.Content = null;
            _settingsActions!.Children.Clear();
            settingsSearch.Text = string.Empty;
            if (key == null)
            {
                _currentPage = "Home";
                pageContent.Content = null;
                StoreSettingsWindowSize();
                _settingsWindow?.Hide();
                ShowHideWindow(true);
                HotkeyManager.Instance.IsPause = false;
            }
            else
            {
                _currentPage = "Settings";
                _settingsKey = key;
                EnsureSettingsWindow();
                pageContent.Content = _settingsWorkspace;
                await SelectSettingsAsync(key);
                ShowSettingsWindow();
            }
            SetWorkspaceChrome();
            ResizeWorkspace();
            FocusWorkspaceNavigation();
        }
        catch (Exception ex) { Logging.SaveLog("Workspace navigation", ex); NoticeManager.Instance.Enqueue(ex.Message); }
        finally { _navigationGate.Release(); }
    }

    private void FocusWorkspaceNavigation()
    {
        var page = _currentPage;
        var key = _settingsKey;
        Dispatcher.UIThread.Post(() =>
        {
            // A newer navigation can finish before this layout callback runs.
            if (_currentPage != page || _settingsKey != key || AppManager.Instance.IsExiting) return;
            // AppKit may activate a newly shown settings window after this callback.
            // Keep selection visible regardless; only keyboard focus needs activation.
            if (page != "Home" && _navigationItems.TryGetValue(key, out var selected)) selected.BringIntoView();
            if (!WorkspaceWindow.IsActive) return;
            if (WorkspaceWindow.GetVisualDescendants().OfType<DialogHost>().Any(host => host.IsOpen)) return;
            if (page == "Home") btnSettings.Focus();
            else
            {
                _settingsPageTitle?.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    private void SetWorkspaceChrome()
    {
        homeBrand.IsVisible = btnSettings.IsVisible = true;
        btnReturnConnection.IsVisible = settingsSearch.IsVisible = txtPageTitle.IsVisible = true;
        contentStatusBarView.Height = double.NaN;
        contentStatusBarView.IsHitTestVisible = contentStatusBarView.IsEnabled = true;
        pageContent.IsVisible = _currentPage == "Settings";
        foreach (var (key, item) in _navigationItems) item.Classes.Set("selected", key == _settingsKey);
    }

    private PageSession GetSession(string key)
    {
        if (_sessions.TryGetValue(key, out var existing)) return existing;
        object model = key switch
        {
            "Subscriptions" => new SubSettingViewModel(), "Routing" => new RoutingSettingViewModel(),
            "Options" => new OptionSettingViewModel(false), "DNS" => new DNSSettingViewModel(),
            "Tun" => new TunSettingsViewModel { DeferApply = true, ApplyAsync = reason => ViewModel!.ReloadWithResultAsync(reason, onlyIfChanged: true) },
            "Templates" => new FullConfigTemplateViewModel(), "Hotkeys" => new GlobalHotkeySettingViewModel(),
            "Backup" => new BackupAndRestoreViewModel(), "Appearance" => new ThemeSettingViewModel(editAsDraft: true),
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        var view = model is ThemeSettingViewModel theme ? new ThemeSettingView(theme) : SimpleViewLocator.Instance.Build(model);
        view.DataContext = model;
        if (view is TunSettingsWindow tun)
        {
            tun.IsEmbedded = true;
            tun.NavigateRequested += section => _ = OpenSettingsAsync(section);
        }
        if (view is SubSettingWindow subscriptions)
        {
            subscriptions.IsEmbedded = true;
            subscriptions.UpdateSubscriptionAsync = ViewModel!.UpdateSubscriptionProcess;
        }
        var session = new PageSession(key, view, model);
        _sessions.Add(key, session);
        session.Ready = InitializeSessionAsync(session);
        if (model is ServiceLib.Base.ICloseable editable)
            editable.RequestClose += (_, _) => _pendingSave = SaveSessionAsync(session);
        if (view is ThemeSettingView appearance)
            appearance.Saved += () => _pendingSave = SaveSessionAsync(session);
        if (view is IWorkspacePage page)
            page.CloseRequested += result =>
            {
                if (result is true && session.IsDraft) _pendingSave = SaveSessionAsync(session);
                else if (session.IsDraft) _ = DiscardSessionAsync(session);
            };
        if (session.IsDraft && view is not TunSettingsWindow and not ThemeSettingView)
        {
            if (view.FindControl<Button>("btnSave") is { } save) WorkspaceIcons.SetButton(save, "save", WorkspaceText.T("保存", "Save"), "WorkspaceOnAccent");
            if (view.FindControl<Button>("btnCancel") is { } cancel)
            {
                WorkspaceIcons.SetButton(cancel, "close", WorkspaceText.T("放弃修改", "Discard changes"));
                cancel.IsCancel = false;
            }
        }
        return session;
    }

    private static async Task InitializeSessionAsync(PageSession session)
    {
        session.View.IsEnabled = false;
        try
        {
            await (session.Model switch
            {
                OptionSettingViewModel vm => vm.Initialization,
                TunSettingsViewModel vm => vm.Initialization,
                DNSSettingViewModel vm => vm.Initialization,
                FullConfigTemplateViewModel vm => vm.Initialization,
                _ => Task.CompletedTask
            });
            session.Snapshot = EditorSnapshot.Capture(session.Model);
            session.IsReady = true;
        }
        finally { session.View.IsEnabled = true; }
    }

    private async Task DiscardSessionAsync(PageSession session)
    {
        if (session.Model is ThemeSettingViewModel appearance) appearance.RevertPreview();
        _sessions.Remove(session.Key);
        _settingsBody!.Content = null;
        await SelectSettingsAsync(_settingsKey);
    }

    private async Task SaveSessionAsync(PageSession session)
    {
        try
        {
            if (session.Model is not ThemeSettingViewModel and not OptionSettingViewModel { OnlySourcesChanged: true })
                await ViewModel!.ApplySettingsChangesAsync(session.Key, applyConnection: false);
            session.Snapshot = EditorSnapshot.Capture(session.Model);
            NoticeManager.Instance.Enqueue(WorkspaceText.T("已保存", "Saved"));
        }
        catch (Exception ex) { Logging.SaveLog("Save workspace settings", ex); NoticeManager.Instance.Enqueue(ex.Message); }
    }

    private async Task FlushManagementPageAsync(string key)
    {
        if (key is not ("Subscriptions" or "Routing") || !_sessions.TryGetValue(key, out var session)) return;
        if (session.Model is SubSettingViewModel { IsModified: true } or RoutingSettingViewModel { IsModified: true })
            await ViewModel!.ApplySettingsChangesAsync(key, applyConnection: false);
        _sessions.Remove(key);
    }

    private async Task<bool> ResolveDraftsAsync()
    {
        await _pendingSave;
        await Task.WhenAll(_sessions.Values.Select(s => s.Ready));
        var dirty = _sessions.Values.Where(s => s.IsDirty).ToArray();
        if (dirty.Length == 0) return true;
        var choice = await UI.ShowUnsavedChanges();
        if (choice == 0) return false;
        foreach (var session in dirty)
        {
            if (choice == 1)
            {
                await SaveDraftAsync(session);
                if (session.IsDirty) return false;
            }
            else
            {
                if (session.Model is ThemeSettingViewModel theme) theme.RevertPreview();
                _sessions.Remove(session.Key);
            }
        }
        return true;
    }

    private async Task SaveDraftAsync(PageSession session)
    {
        await session.Ready;
        switch (session.Model)
        {
            case OptionSettingViewModel vm: await vm.SaveCmd.Execute().ToTask(); break;
            case TunSettingsViewModel vm: await vm.SaveCmd.Execute().ToTask(); break;
            case DNSSettingViewModel vm: await vm.SaveCmd.Execute().ToTask(); break;
            case FullConfigTemplateViewModel vm: await vm.SaveCmd.Execute().ToTask(); break;
            case GlobalHotkeySettingViewModel vm: await vm.SaveCmd.Execute().ToTask(); break;
            case BackupAndRestoreViewModel vm: await vm.SaveCmd.Execute().ToTask(); break;
            case ThemeSettingViewModel vm: await vm.SaveAsync(); await SaveSessionAsync(session); break;
        }
        await _pendingSave;
    }

    private async Task SaveActiveDraftAsync()
    {
        var key = _settingsKey is "Core" or "Application" or "SystemProxy" or "Engines" or "Sources" ? "Options" : _settingsKey;
        if (!_sessions.TryGetValue(key, out var session) || !session.IsDraft) return;
        try { await SaveDraftAsync(session); }
        catch (Exception ex) { Logging.SaveLog("Save workspace shortcut", ex); NoticeManager.Instance.Enqueue(ex.Message); }
    }

    private void BuildSettingsWorkspace()
    {
        if (_settingsWorkspace != null) return;
        _settingsNavigation = new StackPanel { Spacing = 12, Margin = new Thickness(12, 16) };
        foreach (var group in WorkspacePages.Available.GroupBy(p => p.Group))
        {
            var items = new StackPanel { Spacing = 3 };
            foreach (var entry in group)
            {
                var item = new Button { Name = "nav" + entry.Key, Tag = entry.Key, Classes = { "workspacePage" },
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 42 };
                WorkspaceIcons.SetButton(item, entry.Icon, entry.Title);
                ToolTip.SetTip(item, entry.Title);
                item.Click += async (_, _) => await OpenSettingsAsync(entry.Key);
                _navigationItems.Add(entry.Key, item);
                items.Children.Add(item);
            }
            var groupTitle = WorkspacePages.GroupTitle(group.Key);
            var section = new Expander { Name = "group" + group.Key,
                Header = new TextBlock { Text = groupTitle, TextWrapping = TextWrapping.Wrap }, Content = items,
                IsExpanded = group.Key == "app", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(section, groupTitle);
            ToolTip.SetTip(section, groupTitle);
            _navigationGroups.Add(group.Key, section);
            _settingsNavigation.Children.Add(section);
        }
        _settingsConnectionState = new TextBlock { Text = WorkspaceText.T("正在读取运行状态", "Reading runtime status"), TextWrapping = TextWrapping.Wrap, Classes = { "caption", "muted" } };
        var back = new Button { Classes = { "rowAction" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        WorkspaceIcons.SetButton(back, "back", WorkspaceText.T("连接窗口", "Connection window"));
        back.Click += (_, _) => ShowHideWindow(true);
        var rail = new DockPanel();
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 12),
            Child = new StackPanel { Spacing = 8, Children = { _settingsConnectionState, back } } };
        footer.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WorkspaceBorder"));
        DockPanel.SetDock(footer, Dock.Bottom); rail.Children.Add(footer);
        rail.Children.Add(new ScrollViewer { Name = "settingsNavigationScroll", Content = _settingsNavigation, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _settingsRail = new ScrollViewer { Content = rail, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _settingsRail.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WorkspaceSurface"));
        _settingsBody = new ContentControl { Name = "settingsBody", HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        _settingsPageTitle = new TextBlock { Name = "settingsPageTitle", Classes = { "title" }, Focusable = true, TextWrapping = TextWrapping.Wrap };
        _settingsDescription = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        _settingsActions = new StackPanel { Spacing = 8 };
        // Expanded runtime details must not consume the editor and its save actions.
        _settingsActionsScroll = new ScrollViewer { Content = _settingsActions,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var pageHeader = new StackPanel { Margin = new Thickness(8, 12, 8, 12), Spacing = 6, Children = { _settingsPageTitle, _settingsDescription, _settingsActionsScroll } };
        var page = new DockPanel { Margin = new Thickness(20, 8, 20, 16) };
        DockPanel.SetDock(pageHeader, Dock.Top); page.Children.Add(pageHeader); page.Children.Add(_settingsBody);
        _settingsWorkspace = new Grid { Name = "settingsWorkspace", ColumnDefinitions = new ColumnDefinitions("224,*") };
        _settingsWorkspace.Children.Add(_settingsRail); Grid.SetColumn(page, 1); _settingsWorkspace.Children.Add(page);
        _searchResults = new StackPanel { Spacing = 4 };
        _searchPanel = new Border { Name = "settingsSearchResults", Classes = { "surface" }, Padding = new Thickness(8), IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Width = 360, Margin = new Thickness(8), ZIndex = 20,
            Child = new ScrollViewer { Content = _searchResults, MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        Grid.SetColumn(_searchPanel, 1); _settingsWorkspace.Children.Add(_searchPanel);
    }

    private void RenderSettingsSearch()
    {
        if (_searchPanel == null || _searchResults == null) return;
        _searchResults.Children.Clear();
        _searchPanel.IsVisible = _currentPage == "Settings" && !string.IsNullOrWhiteSpace(settingsSearch.Text);
        if (!_searchPanel.IsVisible) return;
        foreach (var entry in WorkspacePages.Search(settingsSearch.Text!))
        {
            var result = new Button { Tag = entry.Key, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            WorkspaceIcons.SetButton(result, entry.Icon, entry.Title);
            result.Click += async (_, _) => await OpenSettingsAsync(entry.Key);
            _searchResults.Children.Add(result);
        }
        if (_searchResults.Children.Count == 0) _searchResults.Children.Add(new TextBlock
        { Text = WorkspaceText.T("没有匹配的设置。服务器请在“更换服务器”中搜索。", "No matching settings. Search servers in Change server."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) });
    }

    private async Task SelectSettingsAsync(string key)
    {
        if (_settingsBody == null || ViewModel == null) return;
        var entry = WorkspacePages.Available.Single(p => p.Key == key);
        _settingsKey = key;
        _settingsBody.Content = null;
        _settingsActions!.Children.Clear();
        _navigationGroups[entry.Group].IsExpanded = true;
        _settingsPageTitle!.Text = entry.Title;
        _settingsDescription!.Text = entry.Description;
        txtPageTitle.Text = entry.Title;
        HotkeyManager.Instance.IsPause = key == "Hotkeys";
        switch (key)
        {
            case "Servers": _settingsBody.Content = _servers; break;
            case "ProxyCheck": _settingsBody.Content = _proxyCheck ??= new ProxyCheckView(); break;
            case "Logs": _settingsBody.Content = _logs; break;
            case "Proxies":
                var proxies = (ClashProxiesView)SimpleViewLocator.Instance.Build(ViewModel.ClashProxiesViewModel);
                proxies.ViewModel = ViewModel.ClashProxiesViewModel;
                proxies.ChangeServer = () => OpenSettingsAsync("Servers");
                proxies.EditServer = async id =>
                {
                    var item = await AppManager.Instance.GetProfileItem(id); if (item == null) return;
                    await OpenSettingsAsync("Servers");
                    ViewModel.ProfilesViewModel.SelectedProfile = new ProfileItemModel { IndexId = id, Remarks = item.Remarks, ConfigType = item.ConfigType };
                    await ViewModel.ProfilesViewModel.EditServerCmd.Execute().ToTask();
                };
                _settingsBody.Content = proxies; break;
            case "Connections":
                var connections = (ClashConnectionsView)SimpleViewLocator.Instance.Build(ViewModel.ClashConnectionsViewModel);
                connections.ViewModel = ViewModel.ClashConnectionsViewModel;
                _settingsBody.Content = connections;
                UpdateProxyNavigation(ViewModel.ShowClashUI);
                break;
            case "Updates":
                _checkUpdateView ??= new CheckUpdateView { ViewModel = ViewModel.CheckUpdateViewModel };
                _settingsBody.Content = _checkUpdateView; break;
            case "Utilities": _settingsBody.Content = BuildApplicationTools(); break;
            case "Help": _settingsBody.Content = BuildHelpPage(); break;
            case "Core" or "Application" or "SystemProxy" or "Engines" or "Sources":
                var optionSession = GetSession("Options");
                var options = (OptionSettingWindow)optionSession.View;
                _settingsBody.Content = options;
                options.SelectSection(Array.IndexOf(new[] { "Core", "Application", "SystemProxy", "Tun", "Engines", "Sources" }, key));
                await optionSession.Ready;
                if (key is "Core" or "SystemProxy") _settingsActions.Children.Add((contentStatusBarView.Content as StatusBarView)!.CreateSettingsControls(key));
                break;
            default:
                var session = GetSession(key);
                _settingsBody.Content = session.View;
                await session.Ready;
                if (session.Model is TunSettingsViewModel tun) await tun.RefreshRuntimeAsync();
                break;
        }
        SetWorkspaceChrome();
    }

    private Control BuildApplicationTools()
    {
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(8) };
        body.Children.Add(new TextBlock { Text = WorkspaceText.T("应用与本地数据", "Application and local data"), Classes = { "sectionTitle" } });
        foreach (var source in new[] { menuOpenTheFileLocation, menuClearServerStatistics, menuRebootAsAdmin, menuSettingsSetUWP }) body.Children.Add(ToolAction(source));
        body.Children.Add(new TextBlock { Text = ResUI.menuRegionalPresets, Classes = { "sectionTitle" }, Margin = new Thickness(0, 16, 0, 0) });
        foreach (var source in new[] { menuRegionalPresetsDefault, menuRegionalPresetsRussia, menuRegionalPresetsIran }) body.Children.Add(ToolAction(source));
        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private Control BuildHelpPage()
    {
        var body = new StackPanel { Margin = new Thickness(8), Spacing = 16 };
        body.Children.Add(WorkspaceIcons.Create("brand", 42, "WorkspaceAccent"));
        body.Children.Add(new SelectableTextBlock { Text = Utils.GetVersion(), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = System.Runtime.InteropServices.RuntimeInformation.OSDescription + " · " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "Avalonia · .NET " + Environment.Version, TextWrapping = TextWrapping.Wrap });
        foreach (var source in menuHelp.Items.OfType<MenuItem>()) body.Children.Add(ToolAction(source));
        body.Children.Add(ToolAction(menuPromotion)); body.Children.Add(ToolAction(menuClose));
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
        if (_navigationItems.TryGetValue("Connections", out var item))
            ToolTip.SetTip(item, enabled ? WorkspaceText.T("查看当前连接", "View live sessions") : WorkspaceText.Unavailable);
        if (_currentPage == "Settings" && _settingsKey == "Connections"
            && _settingsBody?.Content is ClashConnectionsView connections)
        {
            connections.IsEnabled = enabled;
            _settingsDescription!.Text = enabled
                ? WorkspacePages.Available.Single(page => page.Key == "Connections").Description
                : WorkspaceText.T("当前内核未提供连接 API；会话操作已暂停，列表仅为上次快照。",
                    "The current core does not provide a connections API. Session actions are paused; the list is only the last snapshot.");
        }
    }

    private void ResizeWorkspace()
    {
        var compact = (_settingsWindow?.Bounds.Width ?? 1120) < 1000;
        pageHeader.Margin = new Thickness(Utils.IsMacOS() ? 80 : 16, 0, Utils.IsWindows() ? 140 : 16, 0);
        settingsSearch.Width = compact ? 210 : 280;
        if (_settingsWorkspace != null) _settingsWorkspace.ColumnDefinitions[0].Width = new GridLength(compact ? 188 : 224);
        if (_settingsActionsScroll != null) _settingsActionsScroll.MaxHeight = Math.Clamp(((_settingsWindow?.Bounds.Height ?? 780) - 56) * .3, 96, 260);
        if (contentStatusBarView.Content is StatusBarView status) status.SetCompact((_serverDrawer?.CollapsedWidth ?? Bounds.Width) < 700 || Bounds.Height < 760);
        _servers?.SetCompact(compact);
    }

    private void ShowConnectionProgress(ServiceLib.Models.Dto.ConnectionTransition? state)
    {
        if (state == null || AppManager.Instance.IsExiting || _connectionFeedback == null) return;
        RouteConnectionFeedback();
        ConnectionFeedback?.Update(state, CanPresentFeedback(this) || CanPresentFeedback(_settingsWindow));
    }

    private static bool CanPresentFeedback(Window? window) =>
        window is { IsVisible: true, WindowState: not WindowState.Minimized };

    private void RouteConnectionFeedback()
    {
        if (_connectionFeedback == null || _settingsFeedback == null) return;
        var mainVisible = CanPresentFeedback(this);
        var settingsVisible = CanPresentFeedback(_settingsWindow);
        var settings = settingsVisible && (_settingsWindow!.IsActive || !mainVisible);
        if (!mainVisible && !settingsVisible) _connectionFeedback.Dismiss();
        // Each top level owns its visual tree. Only state crosses windows; moving a live
        // control during activation can leave it queued in the previous LayoutManager.
        _connectionFeedback.Present(mainVisible && !settings);
        _settingsFeedback.Present(settings);
    }

    private async Task RetryConnectionFeedbackAsync()
    {
        if (ViewModel == null) return;
        if (ViewModel.StatusBarViewModel.Transition?.Disconnecting == true || CoreManager.Instance.HasUnconfirmedProcesses) await ViewModel.DisconnectAsync();
        else if (ViewModel.StatusBarViewModel.Transition?.UpdatingProxy == true)
            await ViewModel.StatusBarViewModel.ChangeSystemProxyAsync(AppManager.Instance.Config.SystemProxyItem.SysProxyType, true);
        else await ViewModel.Reload("用户重试连接");
    }
}
