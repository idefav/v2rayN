using System.Text.RegularExpressions;
using System.Windows.Input;
using ServiceLib.Services;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Common;

internal enum TrayState { Off, On, Unknown }
internal enum ProxyState { Off, Local, Other, Unknown }
internal sealed record TraySnapshot(TrayState Core, TrayState Tun, ProxyState Proxy, string? Server = null);

/// <summary>Menu-bar facts are observed runtime state, never the user's requested mode.</summary>
internal sealed class WorkspaceTray : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly Func<CancellationToken, Task<TraySnapshot>> _read;
    private readonly Func<bool> _busy;
    private readonly Func<bool> _selectedTun;
    private readonly Func<ESysProxyType> _selectedProxy;
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<NativeMenuItem> _mutations = [];
    private readonly Dictionary<ESysProxyType, NativeMenuItem> _proxyChoices = [];
    private readonly NativeMenuItem _status, _server, _proxy, _tun, _connect, _toggleTun;
    private bool _reading, _executing, _disposed;
    private TraySnapshot _snapshot = new(TrayState.Unknown, TrayState.Unknown, ProxyState.Unknown);
    private string? _iconKey;
    internal NativeMenu Menu { get; }
    internal TraySnapshot Snapshot => _snapshot;

    internal WorkspaceTray(TrayIcon tray, Func<CancellationToken, Task<TraySnapshot>> read,
        Func<bool> busy, Func<bool> selectedTun, Func<ESysProxyType> selectedProxy,
        Func<string, Task> action, bool start = true)
    {
        _tray = tray; _read = read; _busy = busy; _selectedTun = selectedTun; _selectedProxy = selectedProxy;
        // App.axaml's menu has already been exported to AppKit. Avalonia's native
        // exporter keeps that root's identity; replacing it throws during startup.
        // Rebuild its entries in place, and assign a root only for a new tray.
        Menu = tray.Menu ?? new NativeMenu();
        Menu.Items.Clear();
        _status = Info(); _server = Info(); _proxy = Info(); _tun = Info();
        Menu.Items.Add(new NativeMenuItemSeparator());
        _connect = Item("connect", "连接", "Connect", true);
        _toggleTun = Item("tun", "开启 TUN", "Enable TUN", true);
        var proxyMenu = new NativeMenu();
        foreach (var (type, zh, en) in new[] {
            (ESysProxyType.ForcedChange, "自动配置系统代理", "Configure system proxy"),
            (ESysProxyType.ForcedClear, "清除系统代理", "Clear system proxy"),
            (ESysProxyType.Unchanged, "不改变系统代理", "Leave system proxy unchanged"),
            (ESysProxyType.Pac, "PAC 模式", "PAC mode") })
        {
            if (type == ESysProxyType.Pac && !OperatingSystem.IsWindows()) continue;
            var item = Item("proxy:" + (int)type, zh, en, true, proxyMenu);
            item.ToggleType = MenuItemToggleType.Radio;
            _proxyChoices[type] = item;
        }
        Menu.Items.Add(new NativeMenuItem(WorkspaceText.T("系统代理策略", "System proxy policy")) { Menu = proxyMenu });
        Item("restart", "重新连接", "Reconnect", true);
        Menu.Items.Add(new NativeMenuItemSeparator());
        Item("home", "打开连接窗口", "Open connection window");
        Item("settings", "打开设置…", "Open settings…");
        Item("copy", "复制终端代理命令", "Copy terminal proxy command");
        var tools = new NativeMenu();
        Item("import", "从剪贴板导入", "Import from clipboard", false, tools);
        Item("subscriptions", "更新全部订阅（直连）", "Update all subscriptions (direct)", false, tools);
        Item("subscriptions-proxy", "更新全部订阅（通过代理）", "Update all subscriptions (proxy)", false, tools);
        Menu.Items.Add(new NativeMenuItem(WorkspaceText.T("服务器与订阅", "Servers and subscriptions")) { Menu = tools });
        Menu.Items.Add(new NativeMenuItemSeparator());
        Item("exit", "退出 v2rayN", "Quit v2rayN");
        Menu.NeedsUpdate += OnMenuUpdate;
        if (tray.Menu == null) tray.Menu = Menu;
        Apply(_snapshot);
        if (start) tray.IsVisible = true;
        _timer.Tick += OnTick;
        if (start) { _timer.Start(); _ = RefreshAsync(); }

        NativeMenuItem Info()
        {
            var item = new NativeMenuItem { IsEnabled = false }; Menu.Items.Add(item); return item;
        }
        NativeMenuItem Item(string id, string zh, string en, bool mutation = false, NativeMenu? parent = null)
        {
            var command = new TrayCommand(async () =>
            {
                if (_disposed || AppManager.Instance.IsExiting || mutation && (_busy() || _executing)) return;
                if (mutation) _executing = true;
                UpdateActions();
                try { await action(id); }
                catch (Exception ex) { Logging.SaveLog("Menu bar " + id, ex); }
                finally { if (mutation) _executing = false; if (!_disposed) { UpdateActions(); await RefreshAsync(); } }
            });
            var item = new NativeMenuItem(WorkspaceText.T(zh, en)) { Command = command };
            if (mutation) _mutations.Add(item);
            (parent ?? Menu).Items.Add(item); return item;
        }
    }

    internal static WorkspaceTray Attach(TrayIcon tray, MainWindow main)
    {
        var vm = main.ViewModel!;
        var status = vm.StatusBarViewModel;
        return new(tray, ReadSnapshotAsync,
            () => !vm.BlReloadEnabled || status.IsChangingConnection || status.Transition is { IsTerminal: false },
            () => status.EnableTun, () => AppManager.Instance.Config.SystemProxyItem.SysProxyType,
            async id =>
            {
                switch (id)
                {
                    case "home": main.ShowHideWindow(true); break;
                    case "settings": await main.NavigateAsync("Settings"); break;
                    case "connect":
                        if ((await ReadSnapshotAsync(AppManager.Instance.ExitToken)).Core != TrayState.Off)
                            await vm.DisconnectAsync();
                        else await vm.Reload("菜单栏连接");
                        break;
                    case "tun": status.EnableTun = !status.EnableTun; break;
                    case "restart": await vm.Reload("菜单栏重新连接"); break;
                    case "copy": await status.CopyProxyCmdToClipboardCmd.Execute().ToTask(); break;
                    case "import": await main.NavigateAsync("Servers"); await status.AddServerViaClipboardCmd.Execute().ToTask(); break;
                    case "subscriptions": await status.SubUpdateCmd.Execute().ToTask(); break;
                    case "subscriptions-proxy": await status.SubUpdateViaProxyCmd.Execute().ToTask(); break;
                    case "exit": await main.RequestExitAsync(); break;
                    default:
                        if (id.StartsWith("proxy:") && int.TryParse(id[6..], out var value))
                            await status.ChangeSystemProxyAsync((ESysProxyType)value, true);
                        break;
                }
            });
    }

    private void OnTick(object? sender, EventArgs args) { UpdateActions(); _ = RefreshAsync(); }
    private void OnMenuUpdate(object? sender, EventArgs args) { UpdateActions(); _ = RefreshAsync(); }
    private void UpdateActions()
    {
        foreach (var item in _mutations) item.IsEnabled = !_disposed && !_executing && !_busy() && !AppManager.Instance.IsExiting;
        _toggleTun.Header = _selectedTun() ? WorkspaceText.T("关闭 TUN", "Disable TUN") : WorkspaceText.T("开启 TUN", "Enable TUN");
        foreach (var (type, item) in _proxyChoices) item.IsChecked = type == _selectedProxy();
    }

    internal async Task RefreshAsync()
    {
        if (_reading || _disposed) return;
        _reading = true;
        try { Apply(await _read(_stop.Token)); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logging.SaveLog("Menu bar status", ex);
            Apply(new(TrayState.Unknown, TrayState.Unknown, ProxyState.Unknown));
        }
        finally { _reading = false; }
    }

    internal void Apply(TraySnapshot snapshot)
    {
        if (_disposed) return;
        _snapshot = snapshot;
        var state = snapshot.Core switch { TrayState.On => WorkspaceText.T("内核已就绪", "Core ready"), TrayState.Off => WorkspaceText.T("未连接", "Disconnected"), _ => WorkspaceText.T("运行状态待确认", "Runtime status unknown") };
        var proxy = snapshot.Proxy switch { ProxyState.Local => WorkspaceText.T("已开启 · 指向本机", "On · local endpoint"), ProxyState.Other => WorkspaceText.T("已开启 · 其他代理 / PAC", "On · other proxy / PAC"), ProxyState.Off => WorkspaceText.T("未开启", "Off"), _ => WorkspaceText.T("未知", "Unknown") };
        var tun = snapshot.Tun switch { TrayState.On => WorkspaceText.T("运行中", "Running"), TrayState.Off => WorkspaceText.T("未运行", "Not running"), _ => WorkspaceText.T("待确认", "Unknown") };
        _status.Header = state;
        _server.Header = Short(snapshot.Server ?? WorkspaceText.T("尚无实际连接", "No active connection"));
        _proxy.Header = "P · " + WorkspaceText.T("系统代理：", "System proxy: ") + proxy;
        _tun.Header = "T · TUN: " + tun;
        _connect.Header = snapshot.Core == TrayState.Off ? WorkspaceText.T("连接", "Connect") : WorkspaceText.T("断开连接", "Disconnect");
        _tray.ToolTipText = $"v2rayN · {state}\n{_proxy.Header}\n{_tun.Header}";
        // The menu bar follows the OS appearance, not a forced application theme.
        var dark = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark;
        var key = $"{snapshot.Proxy}/{snapshot.Tun}/{dark}";
        if (_iconKey != key)
        {
            // Template images are recolored by AppKit, which would erase the two
            // independent state colors. Keep the original full-color raster.
            if (OperatingSystem.IsMacOS()) MacOSProperties.SetIsTemplateIcon(_tray, false);
            _tray.Icon = CreateIcon(snapshot.Proxy == ProxyState.Unknown ? TrayState.Unknown : snapshot.Proxy == ProxyState.Off ? TrayState.Off : TrayState.On, snapshot.Tun, dark);
            _iconKey = key;
        }
        UpdateActions();
    }

    internal static string Short(string text) => text.Length > 45 ? text[..44] + "…" : text;

    internal static WindowIcon CreateIcon(TrayState proxy, TrayState tun, bool dark = false)
    {
        using var bitmap = RenderIcon(proxy, tun, dark: dark);
        using var stream = new MemoryStream(); bitmap.Save(stream, PngBitmapEncoderOptions.Default); stream.Position = 0; return new WindowIcon(stream);
    }

    internal static RenderTargetBitmap RenderIcon(TrayState proxy, TrayState tun, int scale = 2, bool dark = false)
    {
        // Avalonia 12 creates an NSSquareStatusItemLength slot (22 pt on macOS).
        // A wide P/T strip gets scaled to 40x17 inside that 22x22 slot. Use a
        // square, font-independent mark. Each letter has its own state tile:
        // blue P, green T, outlined gray off, amber plus '?' when unconfirmed.
        var bitmap = new RenderTargetBitmap(new PixelSize(18 * scale, 18 * scale), new Vector(96 * scale, 96 * scale));
        using (var context = bitmap.CreateDrawingContext())
        {
            Draw(proxy, .5, true); Draw(tun, 9.5, false);
            void Draw(TrayState state, double x, bool isProxy)
            {
                var off = state == TrayState.Off;
                IBrush ink = off ? new SolidColorBrush(Color.Parse(dark ? "#B7C1D1" : "#596579")) : Brushes.White;
                var fill = new SolidColorBrush(Color.Parse(state == TrayState.Unknown ? "#9D5B10" : isProxy ? "#126CE5" : "#0D8057"));
                var pen = new Pen(ink, 1.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                using (context.PushTransform(Matrix.CreateTranslation(x, 1)))
                {
                    context.DrawRectangle(off ? null : fill, new Pen(off ? ink : fill, 1), new Rect(.5, .5, 7, 15), 2, 2);
                    var letter = state == TrayState.Unknown
                        ? isProxy ? "M2.5,7 V3 h1.6 c2,0 2,2.2 0,2.2 H2.5" : "M2,3 h4 M4,3 v4"
                        : isProxy ? "M2.5,10.5 V5 h1.8 c2.6,0 2.6,3 0,3 H2.5" : "M2,5 h4 M4,5 v5.5";
                    context.DrawGeometry(null, pen, StreamGeometry.Parse(letter));
                    if (state == TrayState.Unknown)
                        context.DrawGeometry(null, pen, StreamGeometry.Parse("M2.5,10 c0,-1.8 3,-1.8 3,0 c0,1.1 -1.5,1.2 -1.5,2.3 M4,14 h.01"));
                }
            }
        }
        return bitmap;
    }

    internal static async Task<TraySnapshot> ReadSnapshotAsync(CancellationToken token)
    {
        var core = CoreManager.Instance;
        var runtime = core.RuntimeSummary;
        var health = await core.GetRecoveryProcessHealthAsync(token);
        var running = runtime != null && health.Main == LocalHealth.Healthy && health.Pre == LocalHealth.Healthy;
        var state = running ? TrayState.On : runtime == null && !core.HasUnconfirmedProcesses && health.Main == LocalHealth.Failed ? TrayState.Off : TrayState.Unknown;
        var proxy = ProxyState.Unknown;
        if (OperatingSystem.IsMacOS())
        {
            try { proxy = ParseProxy(await MacRecoveryPlatform.CommandAsync("/usr/sbin/scutil", ["--proxy"], token), AppManager.Instance.GetLocalPort(EInboundProtocol.socks)); }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) { Logging.SaveLog("Menu bar proxy query", ex); }
        }
        return new(state, running ? runtime!.Tun ? TrayState.On : TrayState.Off : state == TrayState.Off ? TrayState.Off : TrayState.Unknown, proxy, running ? runtime!.Server : null);
    }

    internal static ProxyState ParseProxy(string text, int localPort)
    {
        if (!text.TrimStart().StartsWith("<dictionary> {", StringComparison.Ordinal) || !text.TrimEnd().EndsWith('}')) return ProxyState.Unknown;
        // Scoped dictionaries are not the default global proxy. Restrict keys to the
        // top-level dictionary rather than accidentally reporting a scoped service.
        var fields = new Dictionary<string, string>(); var depth = 0;
        foreach (var line in text.Split('\n'))
        {
            if (depth == 1)
            {
                var match = Regex.Match(line, @"^\s*(\w+)\s*:\s*(.*?)\s*$");
                if (match.Success) fields[match.Groups[1].Value] = match.Groups[2].Value;
            }
            depth += line.Count(c => c == '{') - line.Count(c => c == '}');
        }
        string Value(string key) => fields.GetValueOrDefault(key, "0");
        if (depth != 0 || fields.Any(pair => pair.Key.EndsWith("Enable", StringComparison.Ordinal) && pair.Value is not ("0" or "1"))) return ProxyState.Unknown;
        if (Value("ProxyAutoConfigEnable") == "1" || Value("ProxyAutoDiscoveryEnable") == "1") return ProxyState.Other;
        var active = new[] { "HTTP", "HTTPS", "SOCKS", "FTP", "RTSP", "Gopher" }.Where(p => Value(p + "Enable") == "1").ToArray();
        if (active.Length == 0) return ProxyState.Off;
        return localPort > 0 && active.All(p => Value(p + "Proxy") is "127.0.0.1" or "localhost" or "::1" && Value(p + "Port") == localPort.ToString(CultureInfo.InvariantCulture)) ? ProxyState.Local : ProxyState.Other;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _timer.Stop(); _timer.Tick -= OnTick; Menu.NeedsUpdate -= OnMenuUpdate; _stop.Cancel();
        // In-flight platform reads observe cancellation before the source is disposed.
    }

    private sealed class TrayCommand(Func<Task> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public async void Execute(object? parameter) => await execute();
    }
}
