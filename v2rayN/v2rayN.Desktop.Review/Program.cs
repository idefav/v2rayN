using System.Globalization;
using ReactiveUI.Primitives.Extensions;
using ReactiveUI.Primitives;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI.Avalonia;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Events;
using ServiceLib.Handler;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using ServiceLib.Resx;
using Semi.Avalonia;
using ServiceLib.ViewModels;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

using System.Reflection;

internal static class Program
{
    public static string Output = "";
    public static bool Headless;
    public static bool ScreensOnly;
    public static bool Interactive;
    public static bool WorkflowOnly;
    public static bool LegacyColumns;
    public static string Language = "zh-Hans";
    public static string Theme = "Light";
    public static int FontSize = 14;
    public static int Width = 1440;
    public static double Scale = 1;
    [STAThread]
    public static void Main(string[] args)
    {
        // Block all OS proxy mutations, including startup, exit, failure cleanup and interactive controls.
        SysProxyHandler.ChangesEnabled = false;
        Output = Path.GetFullPath(args.FirstOrDefault() ?? "ui-review");
        Directory.CreateDirectory(Output);
        if (File.Exists(Path.Combine(Output, "failure.txt"))) File.Delete(Path.Combine(Output, "failure.txt"));
        Headless = args.Contains("--headless");
        ScreensOnly = args.Contains("--screens-only");
        Interactive = args.Contains("--interactive");
        WorkflowOnly = args.Contains("--workflow-only");
        LegacyColumns = args.Contains("--legacy-columns");
        foreach (var argument in args.Skip(1))
        {
            var option = argument.Split('=', 2);
            if (option.Length != 2) continue;
            switch (option[0])
            {
                case "--language": Language = option[1]; break;
                case "--theme": Theme = option[1]; break;
                case "--font": FontSize = int.Parse(option[1], CultureInfo.InvariantCulture); break;
                case "--width": Width = int.Parse(option[1], CultureInfo.InvariantCulture); break;
                case "--scale": Scale = double.Parse(option[1], CultureInfo.InvariantCulture); break;
            }
        }
        Environment.SetEnvironmentVariable(Global.LocalAppData, "0");
        if (!Utils.HasWritePermission())
            throw new InvalidOperationException("UI review needs its own writable directory; it must never fall back to production user configuration.");
        if (!File.Exists(Utils.GetBaseDirectory("v2rayN.Desktop.Review.dll")))
            throw new InvalidOperationException("UI review can only initialize fixtures beside its own executable.");
        // Discard this executable's disposable fixture database, not the user's saved configuration.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var fixtureDatabase = Utils.GetConfigPath("guiNDB.db" + suffix);
            if (File.Exists(fixtureDatabase)) File.Delete(fixtureDatabase);
        }
        var builder = AppBuilder.Configure<ReviewApp>();
        builder = Headless ? builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }) : builder.UsePlatformDetect();
        builder.WithFontByDefault().UseReactiveUI(_ => { }).StartWithClassicDesktopLifetime(args);
    }
}

public class ReviewApp : v2rayN.Desktop.App
{
    private MainWindow _main = null!;
    private MainWindowViewModel _vm = null!;
    private readonly List<object> _shots = [];
    private readonly List<string> _checks = [];
    private readonly List<object> _layouts = [];
    public override void OnFrameworkInitializationCompleted()
    {
        // This executable owns a separate output directory and never launches a core.
        if (!AppManager.Instance.InitApp()) throw new InvalidOperationException("Review config initialization failed");
        AppManager.Instance.InitComponents();
        AppManager.Instance.Config.SpeedTestItem.AutoDetectExit = false;
        AppManager.Instance.WindowDialog = new v2rayN.Desktop.Manager.WindowDialog();
        AppManager.Instance.Config.UiItem.CurrentTheme = Program.Theme;
        AppManager.Instance.Config.UiItem.CurrentFontSize = Program.FontSize;
        AppManager.Instance.Config.UiItem.CurrentLanguage = Program.Language;
        AppManager.Instance.Config.UiItem.MainColumnItem.Clear();
        if (Program.LegacyColumns)
        {
            var columns = new[] { ("ConfigType", 80), ("Remarks", 300), ("Address", 120), ("Port", 64), ("Network", 88), ("StreamSecurity", 64), ("SubRemarks", 110), ("DelayVal", 100), ("SpeedVal", 112), ("IpInfo", -1) };
            AppManager.Instance.Config.UiItem.MainColumnItem = columns.Select((column, index) => new ColumnItem { Name = column.Item1, Width = column.Item2, Index = index }).ToList();
        }
        AppManager.Instance.Config.UiItem.WindowSizeItem.Clear();
        AppManager.Instance.Config.WebDavItem = new WebDavItem();
        CultureInfo.CurrentUICulture = new CultureInfo(Program.Language);
        ResUI.Culture = CultureInfo.CurrentUICulture;
        DataTemplates.Add(SimpleViewLocator.Instance);
        _vm = new MainWindowViewModel(true);
        _main = new MainWindow { ViewModel = _vm, Width = Program.Width };
        ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).MainWindow = _main;
        if (Program.Interactive)
        {
            void RecordWindow(string kind) => File.AppendAllText(Path.Combine(Program.Output, "native-window-events.jsonl"),
                JsonSerializer.Serialize(new { time = DateTimeOffset.Now, kind, x = _main.Position.X, y = _main.Position.Y, state = _main.WindowState.ToString(), _main.IsVisible }) + Environment.NewLine);
            _main.PositionChanged += (_, _) => RecordWindow("position");
            _main.GetObservable(Window.WindowStateProperty).Subscribe(_ => RecordWindow("state"));
            void RecordPointer(string kind, PointerEventArgs e)
            {
                var point = e.GetPosition(_main);
                File.AppendAllText(Path.Combine(Program.Output, "native-pointer-events.jsonl"),
                    JsonSerializer.Serialize(new { time = DateTimeOffset.Now, kind, point.X, point.Y,
                        source = (e.Source as Control)?.Name ?? e.Source?.GetType().Name, e.Handled,
                        left = e.GetCurrentPoint(_main).Properties.IsLeftButtonPressed }) + Environment.NewLine);
            }
            _main.AddHandler(InputElement.PointerPressedEvent, (_, e) => RecordPointer("pressed", e), RoutingStrategies.Tunnel, true);
            _main.AddHandler(InputElement.PointerReleasedEvent, (_, e) => RecordPointer("released", e), RoutingStrategies.Tunnel, true);
        }
        _main.Opened += async (_, _) =>
        {
            try { await ReviewAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Program.Output, "failure.txt"), ex.ToString());
                Console.Error.WriteLine(ex);
                Environment.ExitCode = 1;
            }
            finally { if (!Program.Interactive || Environment.ExitCode != 0) ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Shutdown(Environment.ExitCode); }
        };
    }

    private static ProfileItem Profile(EConfigType type) => new()
    {
        ConfigType = type, Remarks = $"示例 · {type}", Address = "server.example.com", Port = type == EConfigType.SSH ? 22 : 443,
        Username = "demo", Password = type == EConfigType.VMess || type == EConfigType.VLESS ? "00000000-0000-0000-0000-000000000001" : "example-only",
        Network = "ws", StreamSecurity = "tls", Sni = "server.example.com"
    };

    private async Task ReviewAsync()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ConfigHandler.InitBuiltinRouting(AppManager.Instance.Config)));
        var builtins = await AppManager.Instance.RoutingItems();
        Check(builtins.Count == 3 && builtins.Select(item => item.Remarks).Distinct().Count() == 3,
            "concurrent initial routing load creates each built-in rule set once");
        await ConfigHandler.InitBuiltinDNS(AppManager.Instance.Config);
        await ConfigHandler.InitBuiltinFullConfigTemplate(AppManager.Instance.Config);
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = "review-sub", Remarks = "自建服务器", Url = "" });
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = "review-backup", Remarks = "备用订阅", Url = "https://backup.example.com/list", AutoUpdateInterval = 240 });
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = "review-disabled", Remarks = "停用订阅", Url = "https://disabled.example.com/list", Enabled = false });
        AppManager.Instance.Config.IndexId = "review-sg.example.com";
        _vm.ProfilesViewModel.ProfileItems.Clear();
        _vm.ProfilesViewModel.SubItems.Clear();
        _vm.ProfilesViewModel.SubItems.Add(new SubItem { Id = "", Remarks = ResUI.AllGroupServers });
        foreach (var sub in await AppManager.Instance.SubItems()) _vm.ProfilesViewModel.SubItems.Add(sub);
        _vm.ProfilesViewModel.SelectedSub = _vm.ProfilesViewModel.SubItems[0];
        foreach (var (name, type, host, delay) in new[]
        {
            ("新加坡 · SSH", EConfigType.SSH, "sg.example.com", 38),
            ("日本 · 东京", EConfigType.VLESS, "jp.example.com", 62),
            ("香港 · 高速", EConfigType.Trojan, "hk.example.com", 24),
            ("美国 · 洛杉矶", EConfigType.SSH, "us.example.com", 168),
            ("备用 · SSH", EConfigType.SSH, "backup.example.com", 0)
        })
        {
            var saved = Profile(type);
            saved.IndexId = "review-" + host; saved.Remarks = name; saved.Address = host; saved.Subid = type == EConfigType.SSH ? "review-sub" : "review-backup";
            await SQLiteHelper.Instance.ReplaceAsync(saved);
            _vm.ProfilesViewModel.ProfileItems.Add(new ProfileItemModel
            {
                IndexId = "review-" + host, Remarks = name, ConfigType = type, Address = host,
                Port = type == EConfigType.SSH ? 22 : 443, Delay = delay, DelayVal = delay > 0 ? delay + " ms" : "—",
                Subid = saved.Subid, SubRemarks = type == EConfigType.SSH ? "自建服务器" : "备用订阅", Network = "ws", StreamSecurity = "tls",
                IsActive = host == "sg.example.com"
            });
        }
        await Task.Delay(250);
        // A blocked startup proxy action is expected in this process. The normal-state
        // fixture omits that deliberate guard error; the guard itself remains enabled.
        _vm.StatusBarViewModel.ConnectionError = null;
        _vm.ProfilesViewModel.SelectedProfile = _vm.ProfilesViewModel.ProfileItems[0];
        _vm.ProfilesViewModel.SelectedProfiles = [_vm.ProfilesViewModel.SelectedProfile];
        _main.GetLogicalDescendants().OfType<ProfilesView>().Single().RefreshServersBiz();
        AppEvents.SendMsgViewRequested.Publish("[UI review] Isolated fixture configuration loaded.\n[UI review] Core launch disabled; network status remains untested.\n");
        await CheckExitPresentationAsync();
        if (!Program.WorkflowOnly)
        {
        await CaptureAsync("00", "服务器主界面", _main);
        await _main.NavigateAsync("Subscriptions");
        await Task.Delay(300);
        await _main.GetLogicalDescendants().OfType<SubSettingWindow>().Single().ViewModel!.RefreshSubItems();
        var subscriptions = _main.GetLogicalDescendants().OfType<SubSettingWindow>().Single().FindControl<DataGrid>("lstSubscription")!;
        subscriptions.SelectedItem = subscriptions.ItemsSource.Cast<object>().First();
        await CaptureAsync("01", "订阅管理", _main);
        await ShowAsync("02", "编辑订阅", new SubEditViewModel(new SubItem { Remarks = "机场订阅", Url = "https://subscription.example.com/list" }));
        _vm.ShowClashUI = true;
        _vm.ClashProxiesViewModel.AutoRefresh = false;
        _vm.ClashProxiesViewModel.ClashModes.ReplaceRange(["Rule", "Global", "Direct"]);
        var group = new ClashProxyModel { Name = "自动选择", Type = "URLTest", Now = "日本 · 东京" };
        _vm.ClashProxiesViewModel.ProxyGroups.Add(group);
        _vm.ClashProxiesViewModel.SelectedGroup = group;
        foreach (var node in _vm.ProfilesViewModel.ProfileItems)
            _vm.ClashProxiesViewModel.ProxyDetails.Add(new ClashProxyModel { Name = node.Remarks, Type = node.ConfigType.ToString(), Delay = node.Delay, DelayName = node.DelayVal, IsActive = node.ConfigType == EConfigType.VLESS });
        _vm.ClashConnectionsViewModel.AutoRefresh = false;
        foreach (var host in new[] { "github.com", "example.com", "downloads.example.com" })
            _vm.ClashConnectionsViewModel.ConnectionItems.Add(new ClashConnectionModel { Id = host, Host = host + ":443", Network = "tcp", Type = "HTTP", Chain = "自动选择 → 日本 · 东京", Elapsed = "12s", ProcessPath = "browser" });
        await _main.NavigateAsync("Proxies");
        await Task.Delay(100);
        var proxyView = _main.GetLogicalDescendants().OfType<ClashProxiesView>().Single();
        var ruleMode = proxyView.FindControl<ComboBox>("cmbRulemode")!;
        Check(!string.IsNullOrWhiteSpace(_vm.ClashProxiesViewModel.RuleModeSelected)
            && ruleMode.SelectedItem?.ToString() == _vm.ClashProxiesViewModel.RuleModeSelected,
            "proxy rule-mode selection displays the actual view-model value");
        Check(ruleMode.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == WorkspaceText.T("规则", "Rule")),
            "proxy rule mode is painted as visible text, not an empty selection");
        await CaptureAsync("03", "当前代理", _main);
        await _main.NavigateAsync("Connections");
        await Task.Delay(300);
        _main.GetLogicalDescendants().OfType<ClashConnectionsView>().Single().FindControl<DataGrid>("lstConnections")!.SelectedItem =
            _vm.ClashConnectionsViewModel.ConnectionItems.First();
        await CaptureAsync("04", "当前连接", _main);
        await _main.NavigateAsync("Logs");
        await Task.Delay(1100); // Drain initialization messages before making shareable fixture screenshots.
        _main.GetLogicalDescendants().OfType<MsgView>().Single().ClearMsg();
        AppEvents.SendMsgViewRequested.Publish("[INFO] Fixture log view ready.\n[WARN] Core launch disabled in UI review.\n[ERROR] Example diagnostic only; no network request was made.\n");
        await Task.Delay(1100);
        await CaptureAsync("05", "运行日志", _main);
        await ShowAsync("06", "SSH 节点编辑", new AddSshServerViewModel(Profile(EConfigType.SSH)));
        var target = new SshSetupTarget("server.example.com", 22, "demo", new SshHostKey("example-public-key", "ssh-ed25519", "SHA256:example"));
        var keySetup = new SshKeySetupViewModel(target, [], new SshKeyStore(Path.Combine(AppContext.BaseDirectory, "review-keys")));
        await CaptureWindowAsync("07", "SSH 免密配置", new SshKeySetupWindow(keySetup));
        var protocols = new[] { EConfigType.VLESS, EConfigType.VMess, EConfigType.Shadowsocks, EConfigType.Trojan,
            EConfigType.Hysteria2, EConfigType.TUIC, EConfigType.WireGuard, EConfigType.SOCKS, EConfigType.HTTP,
            EConfigType.Anytls, EConfigType.Naive, EConfigType.MASQUE };
        for (var index = 0; index < protocols.Length; index++)
        {
            var item = Profile(protocols[index]);
            item.IndexId = "review-protocol-" + protocols[index];
            item.CoreType = protocols[index] is EConfigType.VMess or EConfigType.VLESS or EConfigType.Shadowsocks or EConfigType.Trojan ? ECoreType.Xray : ECoreType.sing_box;
            item.Alpn = "h2";
            item.SetProtocolExtra(item.GetProtocolExtra() with { SsMethod = "aes-128-gcm" });
            await ShowAsync((index + 8).ToString("00"), protocols[index].ToString(), new AddServerViewModel(item));
        }
        var fixtureFile = Utils.GetConfigPath("review-config.json");
        await File.WriteAllTextAsync(fixtureFile, "{\n  \"log\": { \"level\": \"info\" },\n  \"outbounds\": [{ \"type\": \"direct\", \"tag\": \"direct\" }]\n}");
        foreach (var type in new[] { EConfigType.Custom, EConfigType.Outbound })
        {
            var custom = Profile(type); custom.Address = "review-config.json"; custom.CoreType = ECoreType.sing_box;
            await ShowAsync(type == EConfigType.Custom ? "20" : "21", type.ToString(), new AddServer2ViewModel(custom));
        }
        foreach (var type in new[] { EConfigType.PolicyGroup, EConfigType.ProxyChain })
        {
            var item = Profile(type);
            item.SetProtocolExtra(item.GetProtocolExtra() with
            {
                ChildItems = "review-sg.example.com,review-jp.example.com",
                SubChildItems = type == EConfigType.PolicyGroup ? "review-sub" : null
            });
            await ShowAsync(type == EConfigType.PolicyGroup ? "22" : "23", type.ToString(), new AddGroupServerViewModel(item));
        }
        await ShowAsync("24", "节点选择", new ProfilesSelectViewModel { MultiSelect = true });
        await _main.NavigateAsync("Routing");
        var routings = _main.GetLogicalDescendants().OfType<RoutingSettingWindow>().Single().FindControl<DataGrid>("lstRoutings")!;
        await Task.Delay(100);
        routings.SelectedItem = routings.ItemsSource.Cast<object>().FirstOrDefault();
        await CaptureAsync("25", "路由规则集", _main);
        var exampleRouting = new RoutingItem { Remarks = "示例规则集" };
        exampleRouting.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Remarks = "本地网络直连", OutboundTag = "direct", Ip = ["geoip:private"], Enabled = true },
            new() { Remarks = "示例域名代理", OutboundTag = "proxy", Domain = ["domain:example.com"], Enabled = true }
        });
        exampleRouting.Id = "review-rules-editor";
        await ShowAsync("26", "规则集编辑", new RoutingRuleSettingViewModel(exampleRouting));
        await ShowAsync("27", "路由规则详情", new RoutingRuleDetailsViewModel(new RulesItem { Remarks = "示例规则", OutboundTag = "proxy" }));
        for (var index = 0; index < 4; index++)
        {
            await _main.OpenSettingsAsync("DNS");
            var tabs = _main.GetLogicalDescendants().OfType<DNSSettingWindow>().Single().GetLogicalDescendants().OfType<TabControl>().First();
            tabs.SelectedIndex = index;
            var dns = _main.GetLogicalDescendants().OfType<DNSSettingWindow>().Single().ViewModel!;
            dns.NormalDNSCompatible = "{\n  \"servers\": [\"https://dns.example.com/dns-query\"]\n}";
            dns.TunDNSCompatible = dns.NormalDNSCompatible;
            dns.NormalDNS2Compatible = "{\n  \"servers\": [{\"tag\": \"remote\", \"type\": \"https\", \"server\": \"dns.example.com\"}]\n}";
            dns.TunDNS2Compatible = dns.NormalDNS2Compatible;
            await CaptureAsync((28 + index).ToString("00"), "DNS " + index, _main);
        }
        // Editing fixtures above is visual only; discard those settings before functional checks.
        var discardNavigation = _main.NavigateAsync("Servers");
        await ChooseDraftAsync(WorkspaceText.Discard);
        await discardNavigation;
        var settings = new[] { "Core", "Application", "SystemProxy", "Tun", "Engines", "Templates", "Templates", "Appearance", "Backup", "Updates", "Hotkeys" };
        for (var index = 0; index < settings.Length; index++)
        {
            if (settings[index] == "Hotkeys" && !Utils.IsWindows())
            {
                await ShowAsync("42", "Windows 全局快捷键 · 跨平台控件预览", new GlobalHotkeySettingViewModel());
                continue;
            }
            if (index == 7)
            {
                var leavingTemplates = _main.NavigateAsync("Servers");
                await ChooseDraftAsync(WorkspaceText.Discard);
                await leavingTemplates;
            }
            await _main.OpenSettingsAsync(settings[index]);
            if (index is 5 or 6)
            {
                var templates = _main.GetLogicalDescendants().OfType<FullConfigTemplateWindow>().Single();
                await templates.ViewModel!.Initialization;
                templates.ViewModel.FullConfigTemplate4Ray = "{\n  \"routing\": { \"rules\": [] },\n  \"outbounds\": []\n}";
                templates.ViewModel.FullTunConfigTemplate4Ray = templates.ViewModel.FullConfigTemplate4Ray;
                templates.ViewModel.FullConfigTemplate4Singbox = "{\n  \"route\": { \"rules\": [] },\n  \"outbounds\": []\n}";
                templates.ViewModel.FullTunConfigTemplate4Singbox = templates.ViewModel.FullConfigTemplate4Singbox;
                templates.GetLogicalDescendants().OfType<TabControl>().First().SelectedIndex = index - 5;
            }
            await CaptureAsync((32 + index).ToString("00"), settings[index], _main);
            if (index < 5)
            {
                var tabs = _main.GetLogicalDescendants().OfType<OptionSettingWindow>().Single().FindControl<TabControl>("pageTabs")!;
                var selected = (TabItem)tabs.Items[index]!;
                if (tabs.SelectedIndex != index || !ReferenceEquals(tabs.SelectedContent, selected.Content)
                    || !tabs.GetVisualDescendants().Contains((Control)selected.Content!))
                    throw new InvalidOperationException($"Settings section {settings[index]} did not display its actual content (selected {tabs.SelectedIndex}).");
            }
        }
        await CaptureControlAsync("43", "二维码分享", new QrcodeView("ssh://example-only@server.example.com:22#" + Uri.EscapeDataString("示例 · SSH")));
        await CaptureControlAsync("44", "权限密码", new SudoPasswordInputView());
        await CaptureWindowAsync("45", "删除确认", new MessageBoxDialog("删除节点", "确认删除所选的 2 个节点？此操作无法撤销。", WorkspaceText.Delete, true));
        await CaptureWindowAsync("45-host-key", "SSH 公钥信任", new MessageBoxDialog("SSH 公钥信任",
            "server.example.com:22\nssh-ed25519\nSHA256:disposable-example-fingerprint\n\n仅信任已通过独立渠道核对的公钥。", WorkspaceText.Trust));
        await CaptureControlAsync("46", "JSON 编辑器", new JsonEditor { Text = "{\n  \"log\": {\"level\": \"info\"},\n  \"outbounds\": [{\"type\": \"direct\"}]\n}" });
        await CaptureControlAsync("46-invalid", "JSON 校验错误", new JsonEditor { Text = "{\n  \"log\": invalid\n}" });
        }
        await CheckPolishAsync();
        if (!Program.WorkflowOnly) await CheckSecurityVariantsAsync();
        if (!Program.ScreensOnly && !Program.WorkflowOnly) await CheckScenariosAsync();
        File.WriteAllText(Path.Combine(Program.Output, "manifest.json"), JsonSerializer.Serialize(new { fixtureData = true, renderer = Program.Headless ? "Avalonia.Headless + Skia" : "native", Program.Language, Program.Theme, Program.FontSize, Program.Scale, screenshots = _shots, checks = _checks }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(Program.Output, "layout.json"), JsonSerializer.Serialize(_layouts, new JsonSerializerOptions { WriteIndented = true }));
        var gallery = string.Join("", _shots.Select(s => { var value = JsonSerializer.SerializeToElement(s); return $"<article><h2>{value.GetProperty("id")} · {value.GetProperty("title")}</h2><img src='{value.GetProperty("file")}'></article>"; }));
        File.WriteAllText(Path.Combine(Program.Output, "index.html"), "<!doctype html><meta charset='utf-8'><title>v2rayN UI review</title><style>body{font:16px system-ui;background:#eef2f7;margin:32px}article{background:white;padding:20px;margin:24px 0;border-radius:12px}img{width:100%;height:auto}</style><h1>v2rayN 实际界面截图</h1><p>测试进程使用独立配置和示例数据；截图来自实际 Avalonia 控件。网络状态并非生产检测结果。</p>" + gallery);
        WriteComparison();
        Console.WriteLine($"Reviewed {_shots.Count} screens; {_checks.Count} scenarios passed.");
        if (Program.Interactive) await _main.NavigateAsync("Servers");
    }

    private void WriteComparison()
    {
        var parent = new DirectoryInfo(Program.Output);
        while (parent != null && !Directory.Exists(Path.Combine(parent.FullName, "v2rayn-ui-polish-20261011"))) parent = parent.Parent;
        if (parent == null) return;
        var designFolder = Path.Combine(parent.FullName, "v2rayn-ui-polish-20261011");
        if (!File.Exists(Path.Combine(designFolder, "manifest.json"))) return;
        using var designs = JsonDocument.Parse(File.ReadAllText(Path.Combine(designFolder, "manifest.json")));
        var entries = designs.RootElement.GetProperty("designs").EnumerateArray().ToArray();
        var revisionFile = Path.Combine(designFolder, "revisions", "manifest.json");
        var overrides = new Dictionary<string, string>();
        if (File.Exists(revisionFile))
        {
            using var revisions = JsonDocument.Parse(File.ReadAllText(revisionFile));
            foreach (var revision in revisions.RootElement.GetProperty("overrides").EnumerateObject())
                overrides[revision.Name] = Path.Combine("revisions", revision.Value.GetString()!);
        }
        var articles = new List<string>();
        var options = new List<string>();
        foreach (var shot in _shots.Select(s => JsonSerializer.SerializeToElement(s))
            .Where(s => s.GetProperty("id").GetString()!.Length == 2 || s.GetProperty("id").GetString() is "workflow-home" or "workflow-subscriptions")
            .GroupBy(s => s.GetProperty("id").GetString()! switch { "workflow-home" => "00", "workflow-subscriptions" => "01", var id => id })
            .OrderBy(group => group.Key).Select(group => group.Last()))
        {
            var shotId = shot.GetProperty("id").GetString()!;
            var id = shotId switch { "workflow-home" => "00", "workflow-subscriptions" => "01", _ => shotId };
            var design = entries.Single(e => e.GetProperty("id").GetString()!.StartsWith(id + "-", StringComparison.Ordinal));
            var title = System.Net.WebUtility.HtmlEncode(design.GetProperty("title").GetString());
            var original = Path.GetRelativePath(Program.Output, Path.Combine(designFolder,
                overrides.GetValueOrDefault(id, design.GetProperty("file").GetString()!))).Replace('\\', '/');
            options.Add($"<option value='{id}'>{id} · {title}</option>");
            articles.Add($"<article data-id='{id}'><h2>{id} · {title}</h2><div class='pair'><figure><img src='{original}'><figcaption>设计稿</figcaption></figure><figure><img src='{shot.GetProperty("file")}'><figcaption>实际控件 · {shot.GetProperty("width")} × {shot.GetProperty("height")}</figcaption></figure></div></article>");
        }
        File.WriteAllText(Path.Combine(Program.Output, "comparison.html"), "<!doctype html><meta charset='utf-8'><title>v2rayN 设计与实现</title><style>body{font:16px system-ui;margin:24px;background:#eef2f7}nav{position:sticky;top:0;padding:16px;background:white}article{padding:20px;background:white;border-radius:12px;margin:24px 0}.pair{display:grid;grid-template-columns:1fr 1fr;gap:20px}figure{margin:0}img{width:100%}figcaption{color:#667386;padding:12px}@media(max-width:900px){.pair{grid-template-columns:1fr}}</style><h1>设计稿与实际实现对照</h1><p>截图来自测试进程和示例数据。状态成功必须以实际连接检测为准；高级字段保留，长表单可滚动。</p><nav><select onchange=\"document.querySelectorAll('article').forEach(a=>a.hidden=this.value!=='all'&&a.dataset.id!==this.value)\"><option value='all'>全部页面</option>" + string.Join("", options) + "</select> · <a href='index.html'>实际截图</a> · <a href='README.md'>验收记录</a></nav>" + string.Join("", articles));
    }

    private async Task CheckScenariosAsync()
    {
        Check(!_main.DirtyDraftFields().Any(), "visiting settings does not create dirty drafts: " + string.Join(", ", _main.DirtyDraftFields()));
        await CheckDraftNavigationAsync();
        await CheckJsonValidationAsync();
        await CheckCustomConfigurationAsync();
        var settings = new OptionSettingViewModel();
        await settings.Initialization;
        var snapshot = EditorSnapshot.Capture(settings);
        settings.LocalPort++;
        Check(snapshot != EditorSnapshot.Capture(settings), "dirty settings tracked");
        settings.LocalPort--;
        Check(snapshot == EditorSnapshot.Capture(settings), "reverting draft restores clean state");
        var originalSniffing = AppManager.Instance.Config.Inbound[0].DestOverride?.ToArray() ?? [];
        settings.DestOverride.Clear();
        Check(originalSniffing.SequenceEqual(AppManager.Instance.Config.Inbound[0].DestOverride ?? []), "sniffing draft does not mutate saved configuration");
        AppManager.Instance.RunningCoreType = ECoreType.Xray;
        Check(!CoreManager.Instance.IsCoreRunning, "configured core is not reported as running");
        await _main.NavigateAsync("Servers");
        for (var index = 0; index < 1000; index++) _vm.ProfilesViewModel.ProfileItems.Add(new ProfileItemModel
        { IndexId = $"load-{index}", Remarks = $"节点 {index}", ConfigType = EConfigType.VLESS, Address = "example.com", Port = 443 });
        await Task.Delay(200);
        var table = _main.GetLogicalDescendants().OfType<ProfilesView>().Single().FindControl<DataGrid>("lstProfiles")!;
        var realizedRows = table.GetVisualDescendants().OfType<DataGridRow>().Count();
        Check(realizedRows is > 0 and < 100, "1000 nodes retain row virtualization");
        var profiles = _main.GetLogicalDescendants().OfType<ProfilesView>().Single();
        var search = profiles.FindControl<TextBox>("txtServerFilter")!;
        search.Text = "东京";
        await Task.Delay(100);
        Check(table.ItemsSource.Cast<object>().Count() == 1, $"name search filters nodes ({table.ItemsSource.Cast<object>().Count()} results)");
        search.Text = "";
        await Task.Delay(100);
        var protocol = profiles.FindControl<ComboBox>("cmbProtocolFilter")!;
        protocol.SelectedItem = nameof(EConfigType.SSH);
        await Task.Delay(100);
        Check(table.ItemsSource.Cast<ProfileItemModel>().All(n => n.ConfigType == EConfigType.SSH), "protocol filter matches SSH only");
        protocol.SelectedIndex = 0;
        table.SelectedItems.Clear();
        table.SelectedItems.Add(_vm.ProfilesViewModel.ProfileItems[0]);
        table.SelectedItems.Add(_vm.ProfilesViewModel.ProfileItems[1]);
        profiles.RefreshServersBiz();
        Check(table.SelectedItems.Count == 2, "multi-selection survives table refresh");
        _main.FindControl<Button>("btnDiagnosticLogs")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var originalLog = _main.GetLogicalDescendants().OfType<MsgView>().Single();
        await _main.NavigateAsync("Logs"); await _main.NavigateAsync("Servers");
        _main.FindControl<Button>("btnDiagnosticLogs")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ReferenceEquals(originalLog, _main.GetLogicalDescendants().OfType<MsgView>().Single()), "log drawer and page share one view");
        ConfigHandler.SaveWindowSizeItem(AppManager.Instance.Config, nameof(SubSettingWindow), 850, 700);
        var legacyDialog = AppManager.Instance.WindowDialog.ShowDialogAsync(new SubSettingViewModel());
        await Task.Delay(250);
        var legacyHost = ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Windows
            .OfType<v2rayN.Desktop.Base.WindowBase<SubSettingViewModel>>().Single(w => w.IsVisible);
        var legacySizeRestored = Math.Abs(legacyHost.Width - 850) < 1 && Math.Abs(legacyHost.Height - 700) < 1;
        legacyHost.Close();
        await legacyDialog;
        Check(legacySizeRestored, "embedded page window preserves its legacy size preference");
        await Task.Delay(6000); // Let validation notices expire before visual acceptance shots.
        foreach (var (width, theme, file) in new[] { (600, ThemeVariant.Light, "checks-small"), (1100, ThemeVariant.Light, "checks-medium"), (1440, ThemeVariant.Dark, "checks-dark") })
        {
            _main.Width = width; RequestedThemeVariant = theme;
            await CaptureAsync(file, file, _main);
        }
        Check(_main.MinWidth == 600, "minimum window width preserved");
        _main.Width = 600;
        await Task.Delay(250);
        _main.UpdateLayout();
        _main.FindControl<Button>("btnDiagnosticLogs")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await CaptureAsync("checks-small-logs", "Narrow log drawer", _main);
        Check(_main.FindControl<ContentControl>("drawerLogs")!.Bounds.Height > 0, "log drawer can expand in narrow windows");
        _main.FindControl<Button>("btnToggleLogs")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _main.Width = 1440;
        RequestedThemeVariant = ThemeVariant.Light;
        foreach (var theme in new[] { SemiTheme.Aquatic, SemiTheme.Desert, SemiTheme.Dusk, SemiTheme.NightSky })
        {
            RequestedThemeVariant = theme;
            await CaptureAsync("checks-theme-" + theme.Key, theme.Key.ToString()!, _main);
        }
        RequestedThemeVariant = ThemeVariant.Light;
        var appearance = new v2rayN.Desktop.ViewModels.ThemeSettingViewModel { CurrentFontSize = 24 };
        await CaptureAsync("checks-font-24", "24pt", _main);
        appearance.CurrentFontSize = 14;
        await CaptureAsync("checks-dpi-2x", "200% rendering", _main, 2);
        CultureInfo.CurrentUICulture = new CultureInfo("en-US");
        ResUI.Culture = CultureInfo.CurrentUICulture;
        var english = new MainWindow { ViewModel = new MainWindowViewModel(true) };
        english.Show();
        await CaptureAsync("checks-english", "English", english);
        await english.OpenSettingsAsync("Core");
        await CaptureAsync("checks-english-settings", "English settings", english);
        english.Close();
    }

    private async Task CheckExitPresentationAsync()
    {
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, ExitInfoItem>)typeof(ExitInfoService).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ExitInfoService.Instance)!;
        var index = 0;
        foreach (var node in _vm.ProfilesViewModel.ProfileItems)
        {
            var countries = new[] { "SG", "JP", "HK", "US", "" };
            var cities = new[] { "Singapore", "Tokyo", "Hong Kong", "Los Angeles", "" };
            node.ExitInfo = new ExitInfoItem { IndexId = node.IndexId, Ip = index < 4 ? $"203.0.113.{index + 1}" : null, CountryCode = countries[index], City = cities[index], State = index == 3 ? "failed" : index == 4 ? "checking" : "success", Error = index == 3 ? "Fixture: timeout" : null, CheckedAt = index < 4 ? DateTime.UtcNow.AddMinutes(-3) : default, Source = "https://lookup.example.com" };
            cache[node.IndexId] = node.ExitInfo;
            index++;
        }
        await CaptureAsync("exit-servers", "出口地区与固定操作列", _main);
        var profiles = _main.GetLogicalDescendants().OfType<ProfilesView>().Single();
        var test = profiles.FindControl<Button>("btnTestScope")!;
        test.Flyout!.ShowAt(test);
        await CaptureAsync("exit-menu", "测试与出口检测菜单", _main);
        test.Flyout.Hide();
        await _main.NavigateAsync("Proxies");
        await CaptureAsync("exit-single", "单节点代理状态", _main);
        var view = _main.GetLogicalDescendants().OfType<ClashProxiesView>().Single();
        Check(view.FindControl<Border>("singleNode")!.IsVisible && !view.FindControl<Grid>("proxyWorkspace")!.IsVisible,
            "single-node proxy page hides synthetic internal groups");
        var activeId = AppManager.Instance.Config.IndexId;
        var groupNode = new ProfileItem { IndexId = "review-policy-exit", ConfigType = EConfigType.PolicyGroup, Remarks = "故障转移 · 示例" };
        await SQLiteHelper.Instance.ReplaceAsync(groupNode);
        AppManager.Instance.Config.IndexId = groupNode.IndexId;
        _vm.ClashProxiesViewModel.ProxyGroups.ReplaceRange([new ClashProxyModel { Name = groupNode.Remarks, Type = "Fallback", Now = "日本 · 东京" }]);
        _vm.ClashProxiesViewModel.SelectedGroup = _vm.ClashProxiesViewModel.ProxyGroups[0];
        _vm.ClashProxiesViewModel.ProxyDetails.ReplaceRange(_vm.ProfilesViewModel.ProfileItems.Select(n => new ClashProxyModel { Name = n.Remarks, Type = n.ConfigType.ToString(), DelayName = n.DelayVal, IsActive = n.ConfigType == EConfigType.VLESS }));
        _vm.ClashProxiesViewModel.LoadState = "ready";
        await Task.Delay(100);
        await CaptureAsync("exit-policy", "自动策略与候选节点", _main);
        Check(!view.FindControl<Button>("btnActivateProxy")!.IsVisible, "automatic strategy does not offer manual activation");
        var manual = new ClashProxyModel { Name = "手动选择", Type = "Selector", Now = "日本 · 东京" };
        _vm.ClashProxiesViewModel.ProxyGroups.Add(manual);
        _vm.ClashProxiesViewModel.SelectedGroup = manual;
        await Task.Delay(50);
        Check(view.FindControl<Button>("btnActivateProxy")!.IsVisible, "manual strategy offers explicit switching");
        await CaptureAsync("exit-selector", "手动策略", _main);
        AppManager.Instance.Config.IndexId = activeId;
        await SQLiteHelper.Instance.DeleteAsync(groupNode);
        _vm.ClashProxiesViewModel.LoadState = "unavailable";
        _vm.ClashProxiesViewModel.ProxyGroups.Clear(); _vm.ClashProxiesViewModel.ProxyDetails.Clear();
        await _main.NavigateAsync("Servers");
    }

    private async Task CheckPrimaryWorkflowAsync()
    {
        await _main.NavigateAsync("Servers");
        var profiles = _main.GetLogicalDescendants().OfType<ProfilesView>().Single();
        var table = profiles.FindControl<DataGrid>("lstProfiles")!;
        var groupFilter = profiles.FindControl<ComboBox>("cmbGroupFilter")!;
        Check(groupFilter.IsVisible && !_main.FindControl<Menu>("legacyMenu")!.IsVisible,
            "group selector remains visible at full width and duplicated header menu is removed");
        Check(!profiles.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "btnDetails"),
            "node details are row and context actions rather than a toolbar button");
        var preferences = (MenuFlyout)profiles.FindControl<Button>("btnMore")!.Flyout!;
        Check(preferences.Items.OfType<MenuItem>().Count() == 3,
            "table-settings menu contains only column visibility, compact columns and column widths");
        var fixtureNodes = _vm.ProfilesViewModel.ProfileItems.ToArray();
        var savedSelection = _vm.ProfilesViewModel.SelectedProfile;
        var selectedGroup = _vm.ProfilesViewModel.SubItems.Single(sub => sub.Id == "review-backup");
        groupFilter.SelectedItem = selectedGroup;
        await _vm.ProfilesViewModel.RefreshServersBiz();
        await Task.Delay(120);
        Check(_vm.ProfilesViewModel.SelectedSub?.Id == "review-backup"
            && table.ItemsSource.Cast<ProfileItemModel>().Count() == 2
            && table.ItemsSource.Cast<ProfileItemModel>().All(node => node.Subid == "review-backup"),
            $"group selector loads only the chosen subscription's saved servers (selected={_vm.ProfilesViewModel.SelectedSub?.Id}, config={AppManager.Instance.Config.SubIndexId}, nodes={string.Join(',', table.ItemsSource.Cast<ProfileItemModel>().Select(node => node.Subid))})");
        Check(_main.FindControl<ListBox>("sidebarGroups") == null,
            "duplicate sidebar groups removed; toolbar owns group filtering");
        profiles.FindControl<Button>("btnAllServers")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await _vm.ProfilesViewModel.RefreshServersBiz();
        await Task.Delay(120);
        Check(string.IsNullOrEmpty(_vm.ProfilesViewModel.SelectedSub?.Id) && table.ItemsSource.Cast<ProfileItemModel>().Count() == 5,
            "all-servers entry restores servers from local and subscription groups");
        var pending = new List<Task>();
        for (var index = 0; index < 8; index++)
        {
            _vm.ProfilesViewModel.SelectedSub = _vm.ProfilesViewModel.SubItems.Single(sub => sub.Id == (index % 2 == 0 ? "review-sub" : "review-backup"));
            pending.Add(_vm.ProfilesViewModel.RefreshServersBiz());
        }
        await Task.WhenAll(pending); await Task.Delay(150);
        Check(_vm.ProfilesViewModel.SelectedSub.Id == "review-backup" && table.ItemsSource.Cast<ProfileItemModel>().Count() == 2 && table.ItemsSource.Cast<ProfileItemModel>().All(node => node.Subid == "review-backup"),
            "rapid group changes cannot be overwritten by an older asynchronous refresh");
        profiles.FindControl<Button>("btnAllServers")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await _vm.ProfilesViewModel.RefreshServersBiz(); await Task.Delay(150);
        _vm.ProfilesViewModel.ProfileItems.ReplaceRange(fixtureNodes);
        _vm.ProfilesViewModel.SelectedProfile = savedSelection;
        profiles.RefreshServersBiz();
        await Task.Delay(100);
        _main.UpdateLayout();
        if (Program.Headless) AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var row = table.GetVisualDescendants().OfType<DataGridRow>().First(item => item.IsVisible && item.DataContext is ProfileItemModel model && model.IndexId == fixtureNodes[0].IndexId);
        var badge = row.GetVisualDescendants().OfType<Label>().Single(label => label.Name is "activeBadge" or "activeStatusBadge" && label.IsVisible);
        var badgeOrigin = badge.TranslatePoint(default, row)!.Value;
        Check(badge.IsVisible && Math.Abs(badgeOrigin.Y + badge.Bounds.Height / 2 - row.Bounds.Height / 2) < 2,
            "active-server badge is vertically centered across the full node row");
        var pinned = profiles.GetLogicalDescendants().OfType<RightPinnedColumn>().Single();
        var details = pinned.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "btnRowDetails" && button.DataContext == row.DataContext);
        var rowEdit = pinned.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "btnRowEdit" && button.DataContext == row.DataContext);
        var actionOrigin = rowEdit.TranslatePoint(default, profiles)!.Value;
        Check(actionOrigin.X + rowEdit.Bounds.Width <= profiles.Bounds.Width + 1,
            "default and adaptive tables keep complete row actions inside the visible viewport");
        details.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var workspace = _main.GetLogicalDescendants().OfType<ServerWorkspaceView>().Single();
        Check(workspace.IsDetailsOpen && _vm.ProfilesViewModel.SelectedProfile.IndexId == fixtureNodes[0].IndexId,
            "row details opens the clicked server rather than a previously selected row");
        workspace.CloseDetails();
        var edit = pinned.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "btnRowEdit" && button.DataContext == row.DataContext);
        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(160);
        var nodeEditor = ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Windows.OfType<AddSshServerWindow>().Single(window => window.IsVisible);
        Check(nodeEditor.ViewModel!.SelectedSource.IndexId == fixtureNodes[0].IndexId,
            "row edit opens the clicked existing SSH server");
        nodeEditor.Close();
        await CaptureAsync("workflow-home", "首页操作与全部分组", _main);
        await CheckPinnedActionsAsync(profiles, table, pinned);

        await _main.NavigateAsync("Subscriptions");
        await Task.Delay(120);
        var subscriptions = _main.GetLogicalDescendants().OfType<SubSettingWindow>().Single();
        await subscriptions.ViewModel!.RefreshSubItems();
        var groups = subscriptions.FindControl<DataGrid>("lstSubscription")!;
        Check(subscriptions.IsEmbedded && !subscriptions.FindControl<Button>("menuClose")!.IsVisible
            && ReferenceEquals(subscriptions.UpdateSubscriptionAsync?.Target, _vm),
            "embedded subscription page omits close and uses the existing main update workflow");
        var kind = subscriptions.FindControl<ComboBox>("cmbSubscriptionKind")!;
        kind.SelectedIndex = 2;
        Check(groups.ItemsSource.Cast<SubItem>().Count() == 1 && groups.ItemsSource.Cast<SubItem>().All(sub => string.IsNullOrEmpty(sub.Url)),
            $"local-group filter excludes subscriptions (kind={kind.SelectedIndex}, items={string.Join(',', groups.ItemsSource.Cast<SubItem>().Select(sub => sub.Id + ':' + (string.IsNullOrEmpty(sub.Url) ? "local" : "subscription")))})");
        kind.SelectedIndex = 1;
        Check(groups.ItemsSource.Cast<SubItem>().Count() == 2 && groups.ItemsSource.Cast<SubItem>().All(sub => !string.IsNullOrEmpty(sub.Url)),
            "subscription filter includes enabled and disabled subscriptions");
        kind.SelectedIndex = 0;
        await Task.Delay(100);
        var localRow = groups.GetVisualDescendants().OfType<DataGridRow>().First(item => item.IsVisible && item.DataContext is SubItem { Id: "review-sub" });
        Check(!localRow.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "btnRowUpdateSubscription").IsVisible,
            "local groups do not offer meaningless subscription updates");
        var originalUpdate = subscriptions.UpdateSubscriptionAsync;
        var calls = new List<(string Id, bool Proxy)>();
        subscriptions.UpdateSubscriptionAsync = (id, proxy) => { calls.Add((id, proxy)); return Task.CompletedTask; };
        var subscriptionRow = groups.GetVisualDescendants().OfType<DataGridRow>().First(item => item.IsVisible && item.DataContext is SubItem { Id: "review-backup" });
        subscriptionRow.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "btnRowUpdateSubscription").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(120);
        Check(calls.SequenceEqual(new[] { ("review-backup", false) }), "row update targets only that subscription ID");
        var savedGroups = subscriptions.ViewModel.SubItems.ToArray();
        Check(savedGroups.Single(sub => sub.Id == "review-backup").Url == "https://backup.example.com/list"
            && savedGroups.Single(sub => sub.Id == "review-disabled").Url == "https://disabled.example.com/list",
            "read-only table rendering and recycling preserve subscription addresses");
        groups.SelectedItem = savedGroups.Single(sub => sub.Id == "review-backup");
        groups.SelectedItems.Add(savedGroups.Single(sub => sub.Id == "review-disabled"));
        subscriptions.FindControl<MenuItem>("menuUpdateSelectedViaProxy")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(120);
        Check(calls.Count == 2 && calls.Last() == ("review-backup", true),
            "selected subscription update retains proxy choice and excludes disabled subscriptions");
        subscriptions.FindControl<MenuItem>("menuUpdateAllViaProxy")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(120);
        Check(calls.Last() == ("", true), "update-all via proxy preserves the existing all-subscription scope");
        var waiting = new TaskCompletionSource();
        subscriptions.UpdateSubscriptionAsync = (_, _) => waiting.Task;
        subscriptions.FindControl<Button>("btnUpdateAll")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!subscriptions.FindControl<Button>("btnUpdateAll")!.IsEnabled && !groups.IsEnabled,
            "in-progress subscription updates disable repeated actions");
        waiting.SetResult(); await Task.Delay(120);
        subscriptions.UpdateSubscriptionAsync = (_, _) => Task.FromException(new InvalidOperationException("[Fixture] update failed"));
        subscriptions.FindControl<Button>("btnUpdateAll")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(120);
        Check(subscriptions.FindControl<TextBlock>("txtSubscriptionStatus")!.Text == "[Fixture] update failed"
            && subscriptions.FindControl<Button>("btnUpdateAll")!.IsEnabled,
            "subscription failure displays its reason and permits retry");
        await CaptureAsync("workflow-subscription-error", "订阅更新失败与重试 · 示例状态", _main);
        subscriptions.UpdateSubscriptionAsync = originalUpdate;
        subscriptions.FindControl<TextBlock>("txtSubscriptionStatus")!.IsVisible = false;
        await CaptureAsync("workflow-subscriptions", "分组与订阅的行操作", _main);
        await _main.OpenSettingsAsync("Utilities");
        Check(_main.GetLogicalDescendants().OfType<Button>().Any(button => button.Content?.ToString() == ResUI.menuOpenTheFileLocation && button.IsVisible),
            "unique application tools remain reachable after removing the header menu");
        await CaptureAsync("workflow-utilities", "应用工具入口", _main);
    }

    private async Task CheckPinnedActionsAsync(ProfilesView profiles, DataGrid table, RightPinnedColumn pinned)
    {
        var columns = table.Columns.ToDictionary(column => column, column => (column.Width, column.IsVisible));
        var nodes = _vm.ProfilesViewModel.ProfileItems.ToArray();
        var selected = table.SelectedItems.Cast<ProfileItemModel>().ToArray();
        var current = table.SelectedItem;
        var first = nodes[0];
        table.SelectedItems.Add(nodes[1]);
        foreach (var column in table.Columns)
        {
            column.IsVisible = true;
            column.Width = new DataGridLength(column.Tag?.ToString() == "Remarks" ? 900 : Math.Max(180, column.MinWidth));
        }
        await CaptureAsync("fixed-actions-start", "操作列固定 · 横向起点", _main);
        var header = pinned.GetVisualDescendants().OfType<DataGridColumnHeader>().Single();
        var headerX = header.TranslatePoint(default, profiles)!.Value.X;
        var button = pinned.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "btnRowEdit" && control.DataContext == first);
        var buttonX = button.TranslatePoint(default, profiles)!.Value.X;
        var scrollingColumns = table.Columns.Where(column => column.Tag != null).OrderBy(column => column.DisplayIndex).ToArray();
        table.ScrollIntoView(first, scrollingColumns[scrollingColumns.Length / 2]);
        await CaptureAsync("fixed-actions-middle", "操作列固定 · 横向中部", _main);
        Check(Math.Abs(button.TranslatePoint(default, profiles)!.Value.X - buttonX) < 1
            && Math.Abs(header.TranslatePoint(default, profiles)!.Value.X - headerX) < 1,
            "actions and header stay fixed at the right edge while data columns scroll to the middle");
        table.ScrollIntoView(first, scrollingColumns[^1]);
        await CaptureAsync("fixed-actions-end", "操作列固定 · 横向末端", _main);
        var horizontal = table.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Horizontal);
        Check(horizontal.Maximum > 0 && horizontal.Value > 0,
            "fixed-column regression exercises a real overflowing, horizontally scrolled data viewport");
        Check(Math.Abs(button.TranslatePoint(default, profiles)!.Value.X - buttonX) < 1
            && buttonX + button.Bounds.Width <= profiles.Bounds.Width + 1,
            "complete row actions remain visible at the horizontal scroll endpoint, including legacy columns");
        var firstRow = table.GetVisualDescendants().OfType<DataGridRow>().Single(row => row.DataContext == first && row.IsVisible);
        var rowY = firstRow.TranslatePoint(default, profiles)!.Value.Y;
        Check(Math.Abs(button.TranslatePoint(default, profiles)!.Value.Y + button.Bounds.Height / 2 - rowY - firstRow.Bounds.Height / 2) < 2,
            "fixed actions align vertically with their original DataGrid row");
        using (var pixels = new Bitmap(Path.Combine(Program.Output, "fixed-actions-end.png")))
        {
            var centerY = firstRow.TranslatePoint(new Point(0, firstRow.Bounds.Height / 2), table)!.Value.Y;
            var dataPixel = table.TranslatePoint(new Point(table.Bounds.Width - 32, centerY), _main)!.Value;
            var actionPixel = pinned.TranslatePoint(new Point(pinned.Bounds.Width - 6, centerY), _main)!.Value;
            var left = ReadPixel(pixels, dataPixel * Program.Scale);
            var right = ReadPixel(pixels, actionPixel * Program.Scale);
            Check(left.Zip(right).All(pair => Math.Abs(pair.First - pair.Second) <= 1),
                "rendered selected-row background matches across fixed and scrolling cells in the current theme");
        }

        for (var index = 0; index < 1000; index++) _vm.ProfilesViewModel.ProfileItems.Add(new ProfileItemModel
        { IndexId = $"pinned-load-{index}", Remarks = $"示例节点 {index}", ConfigType = EConfigType.VLESS, Address = "example.com", Port = 443 });
        await Task.Delay(150);
        var last = _vm.ProfilesViewModel.ProfileItems[^1];
        table.ScrollIntoView(last, scrollingColumns[^1]);
        await CaptureAsync("fixed-actions-1000", "操作列固定 · 1000 节点滚动", _main);
        var buttons = pinned.GetVisualDescendants().OfType<Button>().Where(control => control.Name == "btnRowDetails").ToArray();
        Check(buttons.Length is > 0 and < 100 && table.GetVisualDescendants().OfType<DataGridRow>().Count() < 100,
            "fixed actions reuse only realized rows with 1000 servers; no second items view or full list of controls");
        Check(table.SelectedItems.Count == 2 && table.SelectedItems.Contains(first) && table.SelectedItems.Contains(nodes[1]),
            "horizontal and vertical scrolling preserve the table's multi-selection");
        var lastDetails = buttons.Single(control => control.DataContext == last);
        lastDetails.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var workspace = _main.GetLogicalDescendants().OfType<ServerWorkspaceView>().Single();
        Check(workspace.IsDetailsOpen && ReferenceEquals(_vm.ProfilesViewModel.SelectedProfile, last),
            "recycled fixed actions open the current row after scrolling to the 1000th server");
        workspace.CloseDetails();
        if (Program.Headless)
        {
            await Task.Delay(100);
            var vertical = table.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical);
            var offset = vertical.Value;
            var point = pinned.TranslatePoint(new Point(pinned.Bounds.Width / 2, pinned.Bounds.Height / 2), _main)!.Value;
            _main.MouseWheel(point, new Vector(0, 3));
            await Task.Delay(150);
            Check(vertical.Value < offset, "wheel input over the fixed actions scrolls the original DataGrid viewport");
        }
        // DataGrid requires scrolling back before replacing column widths (the same
        // ordering used by the application's auto-width command).
        table.ScrollIntoView(last, table.Columns.First(column => column.DisplayIndex == 0));
        await Task.Delay(150);
        _main.UpdateLayout();
        _vm.ProfilesViewModel.ProfileItems.ReplaceRange(nodes);
        foreach (var (column, state) in columns) { column.Width = state.Width; column.IsVisible = state.IsVisible; }
        table.SelectedItems.Clear();
        table.SelectedItem = current;
        foreach (var node in selected) if (!table.SelectedItems.Contains(node)) table.SelectedItems.Add(node);
        await Task.Delay(150);
        _main.UpdateLayout();
        table.ScrollIntoView(first, table.Columns.First(column => column.DisplayIndex == 0));
        await Task.Delay(150);
    }

    private async Task CheckPolishAsync()
    {
        await CheckPrimaryWorkflowAsync();
        await _main.OpenSettingsAsync("Updates");
        var updates = _main.GetLogicalDescendants().OfType<CheckUpdateView>().Single();
        Check(_main.FindControl<TextBlock>("txtPageTitle")!.Text == ResUI.menuCheckUpdate,
            "settings update navigation displays the update page and matching title");
        if (!Utils.IsWindows())
        {
            await _main.OpenSettingsAsync("Hotkeys");
            Check(ReferenceEquals(updates, _main.GetLogicalDescendants().OfType<CheckUpdateView>().Single())
                && !_main.GetLogicalDescendants().OfType<GlobalHotkeySettingWindow>().Any()
                && _main.FindControl<TextBlock>("txtPageTitle")!.Text == ResUI.menuCheckUpdate,
                "unsupported hotkey navigation cannot replace macOS or Linux update content");
        }
        await _main.NavigateAsync("Servers");
        var servers = _main.GetLogicalDescendants().OfType<ServerWorkspaceView>().Single();
        var profiles = _main.GetLogicalDescendants().OfType<ProfilesView>().Single();
        var table = profiles.FindControl<DataGrid>("lstProfiles")!;
        table.SelectedItems.Clear();
        await Task.Delay(100);
        var firstRow = table.GetVisualDescendants().OfType<DataGridRow>().First(row => row.IsEffectivelyVisible && row.DataContext == _vm.ProfilesViewModel.ProfileItems[0]);
        var secondRow = table.GetVisualDescendants().OfType<DataGridRow>().First(row => row.IsEffectivelyVisible && row.DataContext == _vm.ProfilesViewModel.ProfileItems[1]);
        foreach (var row in new[] { firstRow, secondRow })
        {
            var check = row.GetVisualDescendants().OfType<CheckBox>().First();
            if (check.IsChecked != true)
                ActivateCheckbox(check);
        }
        Check(table.SelectedItems.Count == 2, "checkboxes select multiple rows without clearing earlier selection: " + string.Join(",", table.SelectedItems.Cast<ProfileItemModel>().Select(node => node.IndexId)));
        var firstCheck = firstRow.GetVisualDescendants().OfType<CheckBox>().First();
        ActivateCheckbox(firstCheck);
        Check(table.SelectedItems.Count == 1 && table.SelectedItems.Contains(secondRow.DataContext), "unchecking one row preserves other selections");
        profiles.FindControl<Button>("btnClearSelection")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(table.SelectedItems.Count == 0, "clear selection exits the batch toolbar");
        table.SelectedItem = _vm.ProfilesViewModel.ProfileItems[0];
        Check(!servers.IsDetailsOpen && !_main.FindControl<Border>("logDrawer")!.IsVisible, "home starts with detail and log panels closed");
        Check(table.Bounds.Height >= _main.ClientSize.Height * .65 || Program.Width < 1000 || Program.FontSize > 14, "home table occupies at least 65% height with default layout");
        Check(ReferenceEquals(profiles.FindControl<Button>("menuMixedTestServer")!.Command, _vm.ProfilesViewModel.RealPingServerCmd), "toolbar testing uses selected servers, not the full group");
        Check(profiles.FindControl<Button>("btnAddServers")!.Flyout is MenuFlyout { Items.Count: > 10 }, "add-server menu preserves protocol and advanced commands");
        var addFlyout = (MenuFlyout)profiles.FindControl<Button>("btnAddServers")!.Flyout!;
        ((Avalonia.Automation.Provider.IInvokeProvider)new Avalonia.Automation.Peers.ButtonAutomationPeer(profiles.FindControl<Button>("btnAddServers")!)).Invoke();
        await Task.Delay(100);
        Check(addFlyout.IsOpen && ReferenceEquals(TopLevel.GetTopLevel(addFlyout.Popup.Child!), _main), "add menu opens inside the window through button activation");
        await CaptureAsync("checks-add-menu", "添加服务器菜单", _main);
        var addSsh = addFlyout.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == SshStrings.Title);
        addFlyout.Hide();
        if (addSsh.Command is { } command) command.Execute(addSsh.CommandParameter);
        else addSsh.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(250);
        var addedEditor = ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Windows.OfType<AddSshServerWindow>().Single(window => window.IsVisible);
        Check(addedEditor.Title == SshStrings.Title, "toolbar add menu opens a real SSH add editor");
        addedEditor.Close();
        _vm.BlReloadEnabled = true;
        var status = _main.GetLogicalDescendants().OfType<StatusBarView>().Single();
        var connectionButton = status.FindControl<Button>("btnConnectionControls")!;
        connectionButton.Flyout!.ShowAt(connectionButton);
        await Task.Delay(100);
        var routing = status.FindControl<ComboBox>("cmbRoutings2")!;
        Check(routing.SelectedItem is RoutingItem selectedRouting && selectedRouting.IsActive
            && routing.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == selectedRouting.Remarks),
            "connection routing paints the actual active rule-set name");
        await CaptureAsync("48", "连接控制浮层", _main);
        status.CloseConnectionControls();
        var metricProfile = _vm.ProfilesViewModel.SelectedProfile;
        var savedDelay = metricProfile.DelayVal;
        var savedSpeed = metricProfile.SpeedVal;
        metricProfile.DelayVal = "12.5"; metricProfile.SpeedVal = "8.4";
        await Task.Delay(100);
        Check(servers.FindControl<TextBlock>("txtLatency")!.Text == "12.5 ms" && servers.FindControl<TextBlock>("txtSpeed")!.Text == "8.4 MB/s", "detail metrics update with explicit measurement units");
        metricProfile.DelayVal = metricProfile.SpeedVal = "[Fixture] timed out";
        await Task.Delay(100);
        Check(servers.FindControl<TextBlock>("txtLatency")!.Text == "[Fixture] timed out" && servers.FindControl<TextBlock>("txtSpeed")!.Text == "[Fixture] timed out", "detail metric failures remain errors without measurement units");
        metricProfile.DelayVal = savedDelay; metricProfile.SpeedVal = savedSpeed;
        servers.OpenDetails();
        await Task.Delay(100);
        Check(servers.FindControl<Button>("btnSshKey")!.IsVisible && servers.FindControl<TextBlock>("txtAuthentication")!.Text != "—",
            "SSH details expose key setup and authentication from the selected saved profile");
        await CaptureAsync("49", "节点详情抽屉", _main);
        var inspector = servers.FindControl<Border>("inspector")!;
        Check(inspector.Bounds.Width <= 360 && inspector.Bounds.Width <= servers.Bounds.Width, "detail drawer stays inside workspace");
        _main.FindControl<Button>("btnDiagnosticLogs")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!servers.IsDetailsOpen, "opening diagnostics closes server details");
        servers.OpenDetails();
        Check(!_main.FindControl<Border>("logDrawer")!.IsVisible, "opening details closes diagnostics");
        servers.CloseDetails();
        table.SelectedItems.Clear();
        table.SelectedItems.Add(_vm.ProfilesViewModel.ProfileItems[0]);
        table.SelectedItems.Add(_vm.ProfilesViewModel.ProfileItems[1]);
        await CaptureAsync("50", "多选批量操作", _main);
        Check(profiles.FindControl<Button>("btnBatchRemove")!.IsVisible && !profiles.FindControl<Button>("btnAddServers")!.IsVisible, "multiple selection switches to batch toolbar");
        table.SelectedItems.Clear(); table.SelectedItem = _vm.ProfilesViewModel.ProfileItems[0];
        var width = _main.Width;
        _main.Width = 600;
        await CaptureAsync("51", "600 宽窗口", _main);
        Check(profiles.FindControl<ComboBox>("cmbGroupFilter")!.IsVisible, "narrow navigation exposes a group selector");
        _main.Width = width;
        var fixtureNodes = _vm.ProfilesViewModel.ProfileItems.ToArray();
        _vm.ProfilesViewModel.ProfileItems.Clear(); profiles.RefreshServersBiz();
        await CaptureAsync("52", "空节点列表", _main);
        foreach (var node in fixtureNodes) _vm.ProfilesViewModel.ProfileItems.Add(node);
        profiles.RefreshServersBiz();
        var loading = new AddSshServerViewModel(Profile(EConfigType.SSH)) { IsBusy = true, IsTesting = true,
            LastTestResult = new("https://example.com", new(SshTestState.Running), new(SshTestState.Waiting)) };
        await ShowAsync("53", "SSH 处理中 · UI 状态夹具", loading);
        var failed = new AddSshServerViewModel(Profile(EConfigType.SSH)) { LastTestResult = new("https://example.com", new(SshTestState.Failed, Error: "authentication"), new(SshTestState.Skipped)) };
        await ShowAsync("54", "SSH 错误 · UI 状态夹具", failed);
        foreach (var (id, login, proxy) in new[]
        {
            ("variants-ssh-host-key-changed", new SshTestStage(SshTestState.Failed, Error: "hostKey"), new SshTestStage(SshTestState.Skipped)),
            ("variants-ssh-proxy-failed", new SshTestStage(SshTestState.Passed, 28), new SshTestStage(SshTestState.Failed, Error: "forwarding")),
            ("variants-ssh-cancelled", new SshTestStage(SshTestState.Cancelled, Error: "cancelled"), new SshTestStage(SshTestState.Skipped))
        })
            await ShowAsync(id, "SSH 检测状态 · UI 夹具", new AddSshServerViewModel(Profile(EConfigType.SSH)) { LastTestResult = new("https://example.com", login, proxy) });
        var config = AppManager.Instance.Config;
        var oldTheme = config.UiItem.CurrentTheme; var oldFont = config.UiItem.CurrentFontSize;
        config.UiItem.CurrentTheme = null!; config.UiItem.CurrentFontSize = 0;
        var defaults = new v2rayN.Desktop.ViewModels.ThemeSettingViewModel();
        Check(defaults.CurrentTheme == nameof(ETheme.FollowSystem) && defaults.CurrentFontSize == 14, "legacy appearance values display system theme and 14");
        Check(config.UiItem.CurrentTheme == null && config.UiItem.CurrentFontSize == 0, "opening appearance does not write effective defaults to configuration");
        config.UiItem.CurrentTheme = oldTheme; config.UiItem.CurrentFontSize = oldFont;
        _ = new v2rayN.Desktop.ViewModels.ThemeSettingViewModel();
        foreach (var forceDisable in new[] { false, true })
        {
            string? blocked = null;
            var result = await SysProxyHandler.UpdateSysProxy(config, forceDisable, error => blocked = error);
            Check(!result && blocked?.Contains("disabled in UI review") == true,
                forceDisable ? "UI review cannot clear OS proxy on exit" : "UI review cannot change OS proxy");
        }
        var source = fixtureNodes.First(n => n.ConfigType == EConfigType.SSH);
        var existing = await AppManager.Instance.GetProfileItem(source.IndexId);
        var sshEditor = new AddSshServerWindow { ViewModel = new AddSshServerViewModel(existing!) };
        sshEditor.Show(_main); await Task.Delay(100);
        Check(sshEditor.Title == WorkspaceText.EditSsh, "existing SSH node has edit title");
        var original = JsonUtils.Serialize(existing);
        sshEditor.ViewModel.Remarks = "discarded fixture edit"; sshEditor.Close();
        Check(JsonUtils.Serialize(await AppManager.Instance.GetProfileItem(source.IndexId)) == original, "cancel SSH editor leaves stored node unchanged");
        _vm.ProfilesViewModel.SelectedProfile = fixtureNodes[0];
    }

    private async Task CheckDraftNavigationAsync()
    {
        await _main.OpenSettingsAsync("Core");
        await Task.Delay(100);
        var options = _main.GetLogicalDescendants().OfType<OptionSettingWindow>().Single().ViewModel!;
        var port = options.LocalPort;
        options.LocalPort = port + 1;
        await _main.OpenSettingsAsync("DNS");
        await _main.OpenSettingsAsync("Core");
        Check(options.LocalPort == port + 1, "draft survives settings subpage navigation");
        var leaving = _main.NavigateAsync("Servers");
        await ChooseDraftAsync(WorkspaceText.Stay);
        await leaving;
        Check(_main.GetLogicalDescendants().OfType<OptionSettingWindow>().Any(), "keep editing cancels navigation");
        leaving = _main.NavigateAsync("Servers");
        await ChooseDraftAsync(WorkspaceText.Discard);
        await leaving;
        await _main.OpenSettingsAsync("Core");
        options = _main.GetLogicalDescendants().OfType<OptionSettingWindow>().Single().ViewModel!;
        await options.Initialization;
        Check(options.LocalPort == port, $"discard restores saved settings (expected {port}, draft {options.LocalPort}, saved {AppManager.Instance.Config.Inbound[0].LocalPort})");
        options.LocalPort = port + 2;
        leaving = _main.NavigateAsync("Servers");
        await ChooseDraftAsync(WorkspaceText.Save);
        await leaving;
        Check(AppManager.Instance.Config.Inbound[0].LocalPort == port + 2, "save commits draft before navigation");
        await _main.OpenSettingsAsync("Core");
        options = _main.GetLogicalDescendants().OfType<OptionSettingWindow>().Single().ViewModel!;
        options.LocalPort = 0;
        leaving = _main.NavigateAsync("Servers");
        await ChooseDraftAsync(WorkspaceText.Save);
        await leaving;
        Check(_main.GetLogicalDescendants().OfType<OptionSettingWindow>().Any(), "invalid settings prevent save and navigation");
        Check(AppManager.Instance.Config.Inbound[0].LocalPort == port + 2, "invalid settings leave saved port unchanged");
        options.LocalPort = port + 2;
        await _main.NavigateAsync("Servers");
        await _main.OpenSettingsAsync("Backup");
        var backup = _main.GetLogicalDescendants().OfType<BackupAndRestoreView>().Single().ViewModel!;
        var url = AppManager.Instance.Config.WebDavItem.Url;
        backup.SelectedSource.Url = "https://webdav.example.com/ui-review";
        Check(AppManager.Instance.Config.WebDavItem.Url == url, "WebDAV draft leaves saved configuration unchanged");
        leaving = _main.NavigateAsync("Servers");
        await ChooseDraftAsync(WorkspaceText.Save);
        await leaving;
        Check(AppManager.Instance.Config.WebDavItem.Url == "https://webdav.example.com/ui-review", "WebDAV draft saves without a network check");
        backup.SelectedSource.Url = "https://changed.example.com";
        Check(AppManager.Instance.Config.WebDavItem.Url == "https://webdav.example.com/ui-review", "saved WebDAV configuration does not share mutable draft data");
        backup.SelectedSource.Url = "https://webdav.example.com/ui-review";
    }

    private async Task CheckSecurityVariantsAsync()
    {
        foreach (var type in new[] { EConfigType.VLESS, EConfigType.VMess })
        {
            var profile = Profile(type);
            profile.IndexId = "review-editor-" + type;
            profile.CoreType = ECoreType.Xray;
            profile.StreamSecurity = type == EConfigType.VLESS ? Global.StreamSecurityReality : Global.StreamSecurity;
            profile.Fingerprint = "chrome";
            profile.PublicKey = "example-public-key";
            var window = new AddServerWindow { ViewModel = new AddServerViewModel(profile) };
            window.Show(_main);
            await Task.Delay(100);
            var security = type == EConfigType.VLESS ? "gridRealityMore" : "gridTlsMore";
            var sni = type == EConfigType.VLESS ? "txtSNI2" : "txtSNI";
            Check(window.FindControl<Grid>(security)!.IsEffectivelyVisible
                && window.FindControl<TextBox>(sni)!.Text == profile.Sni
                && window.FindControl<ComboBox>("cmbNetwork")!.SelectedValue?.ToString() == profile.Network
                && window.ViewModel.SelectedSource.StreamSecurity == profile.StreamSecurity,
                type + " editor preserves existing transport and security bindings after layout changes");
            var id = type == EConfigType.VLESS ? "variants-vless-reality" : "variants-vmess-ws-tls";
            await CaptureAsync(id, type + " 已有配置的传输／安全变体 · 示例数据", window);
            var scroll = ((DockPanel)window.Content!).Children.OfType<ScrollViewer>().Single();
            scroll.Offset = new Vector(0, 10000);
            await CaptureAsync(id + "-security", type + " 安全区滚动状态 · 示例数据", window);
            window.Close();
        }
    }

    private async Task ChooseDraftAsync(string choice)
    {
        await Task.Delay(100);
        var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var dialog = lifetime.Windows.Single(w => w != _main && w.GetLogicalDescendants().OfType<Button>().Any(b => b.Content?.ToString() == choice));
        dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content?.ToString() == choice).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private async Task CheckJsonValidationAsync()
    {
        var dns = new DNSSettingViewModel();
        await dns.Initialization;
        var useHosts = AppManager.Instance.Config.SimpleDNSItem.UseSystemHosts;
        dns.UseSystemHosts = !(useHosts ?? false);
        dns.NormalDNSCompatible = "{ invalid json }";
        await dns.SaveCmd.Execute().ToTask();
        Check(AppManager.Instance.Config.SimpleDNSItem.UseSystemHosts == useHosts, "invalid DNS JSON leaves saved settings unchanged");
        var template = new FullConfigTemplateViewModel();
        await template.Initialization;
        var oldTemplate = (await AppManager.Instance.GetFullConfigTemplateItem(ECoreType.Xray))!.Config;
        template.FullConfigTemplate4Ray = "{ invalid json }";
        await template.SaveCmd.Execute().ToTask();
        Check((await AppManager.Instance.GetFullConfigTemplateItem(ECoreType.Xray))!.Config == oldTemplate, "invalid template JSON is rejected before saving");
    }

    private async Task CheckCustomConfigurationAsync()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "review-config.json");
        var originalText = await File.ReadAllTextAsync(file);
        var source = Profile(EConfigType.Outbound);
        source.IndexId = "review-custom-draft";
        source.CoreType = ECoreType.sing_box;
        source.Address = file;
        await SQLiteHelper.Instance.ReplaceAsync(source);
        var cancelled = new AddServer2ViewModel(source) { UseConfigurationDraft = true };
        await cancelled.LoadConfigurationDraftAsync();
        var paths = Directory.GetFiles(Utils.GetConfigPath());
        cancelled.ConfigurationDraft = "{\"type\": \"direct\"}";
        await cancelled.BrowseServer(file);
        Check((await AppManager.Instance.GetProfileItem(source.IndexId))!.Address == file
            && await File.ReadAllTextAsync(file) == originalText && paths.SequenceEqual(Directory.GetFiles(Utils.GetConfigPath())),
            "browsing and cancelling custom drafts writes neither stored node nor configuration file");
        var editing = new AddServer2ViewModel(source) { UseConfigurationDraft = true };
        await editing.LoadConfigurationDraftAsync();
        var closed = false;
        editing.RequestClose += (_, _) => closed = true;
        editing.ConfigurationDraft = "{ invalid json }";
        await editing.SaveServerCmd.Execute().ToTask();
        Check(!closed && (await AppManager.Instance.GetProfileItem(source.IndexId))!.Address == file,
            "invalid custom outbound JSON prevents save and close");
        editing.ConfigurationDraft = "{\"type\": \"direct\", \"tag\": \"ui-draft-test\"}";
        await editing.SaveServerCmd.Execute().ToTask();
        var saved = (await AppManager.Instance.GetProfileItem(source.IndexId))!;
        Check(closed && saved.Address != file
            && await File.ReadAllTextAsync(Utils.GetConfigPath(saved.Address)) == editing.ConfigurationDraft
            && await File.ReadAllTextAsync(file) == originalText,
            "custom outbound save stores validated draft in a new managed file and preserves source");
        await SQLiteHelper.Instance.DeleteAsync(saved);
        File.Delete(Utils.GetConfigPath(saved.Address));
    }

    private void ActivateCheckbox(CheckBox check)
    {
        if (Program.Headless)
        {
            _main.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var point = check.TranslatePoint(new Point(check.Bounds.Width / 2, check.Bounds.Height / 2), _main)!.Value;
            _main.MouseDown(point, MouseButton.Left);
            _main.MouseUp(point, MouseButton.Left);
        }
        else
        {
            ((Avalonia.Automation.Provider.IToggleProvider)Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(check)!).Toggle();
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    private void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); _checks.Add(message); }
    private static byte[] ReadPixel(Bitmap bitmap, Point point)
    {
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            bitmap.CopyPixels(new PixelRect((int)point.X, (int)point.Y, 1, 1), buffer, 4, 4);
            var bytes = new byte[4];
            System.Runtime.InteropServices.Marshal.Copy(buffer, bytes, 0, 4);
            return bytes;
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
    }
    private async Task ShowAsync(string id, string title, object model)
    {
        var view = SimpleViewLocator.Instance.Build(model);
        view.DataContext = model;
        if (view is Window window) await CaptureWindowAsync(id, title, window);
        else await CaptureControlAsync(id, title, view);
    }
    private async Task CaptureControlAsync(string id, string title, Control view)
    {
        var window = new Window { Title = title, Width = view is QrcodeView ? 520 : view is SudoPasswordInputView ? 560 : 1100,
            Height = view is QrcodeView ? 620 : view is SudoPasswordInputView ? 320 : 800, Content = view };
        await CaptureWindowAsync(id, title, window);
    }
    private async Task CaptureWindowAsync(string id, string title, Window window)
    {
        if (Program.Width < 1000 && window.Width > Program.Width) window.Width = Program.Width;
        window.Show(_main);
        await Task.Delay(100);
        if (Program.Width < 1000 && window.Width > Program.Width) window.Width = Program.Width;
        await CaptureAsync(id, title, window);
        window.Close();
    }
    private async Task CaptureAsync(string id, string title, Window window, double scale = 1)
    {
        if (Program.Width < 1000 && window != _main && window.Width > Program.Width)
            window.Width = Math.Max(window.MinWidth, Program.Width);
        await Task.Delay(250);
        window.UpdateLayout();
        if (Program.Headless) AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        scale *= Program.Scale;
        var size = window.ClientSize;
        foreach (var button in window.GetLogicalDescendants().OfType<Button>().Where(b => b.Name is "btnSave" or "btnCancel"))
        {
            var position = button.TranslatePoint(default, window);
            _layouts.Add(new { id, button.Name, x = position?.X, y = position?.Y, width = button.Bounds.Width, height = button.Bounds.Height, button.IsEffectivelyVisible });
        }
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)(size.Width * scale), (int)(size.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        var file = id + ".png";
        using (var stream = File.Create(Path.Combine(Program.Output, file))) bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        _shots.Add(new { id, title, file, width = size.Width, height = size.Height });
    }
}
