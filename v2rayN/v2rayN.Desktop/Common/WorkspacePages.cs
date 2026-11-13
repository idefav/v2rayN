namespace v2rayN.Desktop.Common;

/// <summary>The single, searchable settings map. Management views keep their original commands.</summary>
public sealed record WorkspacePage(string Key, string Group, string Title, string Icon, string Keywords, string Description);

public static class WorkspacePages
{
    public static IReadOnlyList<WorkspacePage> All =>
    [
        Page("Servers", "servers", "服务器管理", "Servers", "servers", "节点 导入 添加 编辑 分享 测速 SSH", "保留完整服务器表格、编辑、导入与测试操作。", "Manage, import, edit and test servers."),
        Page("Subscriptions", "servers", "分组与订阅", "Groups and subscriptions", "subscriptions", "分组 订阅 更新 URL", "管理订阅来源和分组；订阅更新即时执行。", "Manage sources and groups. Subscription updates run immediately."),
        Page("Proxies", "servers", "当前代理 / 策略组", "Proxies / policy groups", "strategy", "策略 当前代理 选择 分流", "查看实际内核提供的代理组与选择。", "Inspect groups and selections provided by the running core."),
        Page("Core", "network", "连接基础与监听", "Connection and listeners", "basic", "端口 监听 局域网 嗅探 重启 日志 Mux", "保存连接配置后，可在首页应用更改。", "Save connection settings, then apply changes from the home page."),
        Page("SystemProxy", "network", "系统代理", "System proxy", "proxy", "PAC bypass 绕过 脚本 自动配置", "代理模式切换即时生效；高级选项保存后应用。", "Proxy mode changes take effect immediately. Save advanced settings before applying."),
        Page("Tun", "network", "TUN", "TUN", "tun", "网卡 接口 MTU UDP 路由 接管 权限", "配置网络接管；保存不会自动开启 TUN。", "Configure traffic capture. Saving does not enable TUN."),
        Page("Routing", "network", "路由规则", "Routing rules", "routing", "路由 规则 分流 绕过 默认", "管理路由方案与匹配规则。", "Manage routing plans and match rules."),
        Page("DNS", "network", "DNS 与流量出口", "DNS and traffic egress", "dns", "DNS 域名 解析 hosts 缓存 出口 QUIC", "保留 DNS、逐节点出口和协议能力设置。", "Configure DNS, per-server egress and protocol capabilities."),
        Page("Connections", "diagnostics", "当前连接", "Connections", "connections", "会话 TCP UDP 域名 进程 连接", "查看实际内核连接；关闭会话即时执行。", "Inspect live core sessions. Closing a session takes effect immediately."),
        Page("ProxyCheck", "diagnostics", "代理检查", "Proxy checks", "check", "诊断 测试 TCP TLS UDP STUN HTTP", "按协议检测可用性，不支持的能力单独展示。", "Check protocol availability and report unsupported capabilities separately."),
        Page("Logs", "diagnostics", "运行日志", "Logs", "logs", "日志 诊断 错误 排查", "查看、筛选和复制实际运行日志。", "Inspect, filter and copy runtime logs."),
        Page("Application", "app", "应用设置", "Application", "application", "启动 托盘 关闭 统计 更新间隔 测试 超时", "设置启动、窗口、节点操作和测试参数。", "Configure startup, window behavior, server actions and tests."),
        Page("Appearance", "app", "外观与语言", "Appearance and language", "appearance", "主题 浅色 深色 字体 语言 缩放", "预览外观后保存，放弃会恢复原有外观。", "Preview appearance before saving. Discard restores the saved appearance."),
        Page("Updates", "app", "更新", "Updates", "updates", "版本 下载 升级 内核 资源", "选择组件检查更新，下载与安装显示实际结果。", "Check selected components and inspect download and installation results."),
        Page("Sources", "app", "更新与资源来源", "Update and resource sources", "sources", "来源 镜像 GitHub Geo SRS 下载", "管理应用、内核及规则资源来源。", "Manage application, core and rule resource sources."),
        Page("Backup", "app", "备份恢复", "Backup and restore", "backup", "备份 恢复 WebDAV 数据 导出", "备份和恢复现有配置；远程操作即时执行。", "Back up and restore configuration. Remote operations run immediately."),
        Page("Utilities", "app", "应用工具", "Application tools", "tools", "目录 统计 管理员 地区 UWP", "打开数据目录、清理统计和使用平台工具。", "Open data folders, clear statistics and access platform tools."),
        Page("Hotkeys", "app", "快捷键", "Keyboard shortcuts", "hotkeys", "快捷键 热键 全局 键盘", "配置 Windows 全局快捷键。", "Configure Windows global shortcuts."),
        Page("Help", "app", "关于与帮助", "About and help", "about", "帮助 关于 版本 文档 退出", "应用信息、帮助文档和退出入口。", "Application information, documentation and exit."),
        Page("Engines", "advanced", "内核分配", "Core assignment", "engines", "核心 内核 Xray sing-box 协议", "为支持的协议选择默认内核。", "Choose default cores for supported protocols."),
        Page("Templates", "advanced", "配置模板", "Configuration templates", "templates", "模板 JSON 自定义 配置", "编辑现有完整模板及协议出口选项。", "Edit full configuration templates and outbound options.")
    ];

    public static IReadOnlyList<WorkspacePage> Available => All.Where(p => p.Key != "Hotkeys" || Utils.IsWindows()).ToArray();
    public static string GroupTitle(string group) => group switch
    {
        "servers" => WorkspaceText.T("服务器与订阅", "Servers and subscriptions"),
        "network" => WorkspaceText.T("网络与路由", "Network and routing"),
        "diagnostics" => WorkspaceText.T("诊断与状态", "Diagnostics and status"),
        "advanced" => WorkspaceText.T("高级", "Advanced"),
        _ => WorkspaceText.T("应用", "Application")
    };

    public static IEnumerable<WorkspacePage> Search(string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Available.Where(p => words.All(word =>
                (p.Title + " " + p.Key + " " + p.Keywords).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.Title.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.Title.StartsWith(query.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // Keep both languages searchable even before a language change takes effect.
    private static string SearchAliases(string key) => key switch
    {
        "Servers" => "剪贴板 二维码 截图 扫描 图片 复制 导出 clipboard QR scan image import export edit latency speed",
        "Subscriptions" => "subscription group URL refresh update",
        "Proxies" => "policy selector traffic split",
        "Core" => "port listener LAN sniffing restart logging mux bandwidth fragment interface",
        "SystemProxy" => "automatic PAC bypass script exceptions",
        "Tun" => "interface MTU stack UDP capture permission route",
        "Routing" => "route rule split bypass default",
        "DNS" => "resolver domain hosts cache egress QUIC",
        "Connections" => "session TCP UDP domain process close",
        "ProxyCheck" => "diagnostic test TCP TLS UDP STUN HTTP",
        "Logs" => "log diagnostic error filter copy",
        "Application" => "startup tray close statistics interval test timeout",
        "Appearance" => "theme light dark system font size language scale",
        "Updates" => "version download upgrade core resource",
        "Sources" => "source mirror GitHub Geo SRS download",
        "Backup" => "backup restore WebDAV data export",
        "Utilities" => "folder directory statistics administrator regional UWP",
        "Hotkeys" => "shortcut hotkey keyboard global",
        "Help" => "help about version documentation exit runtime",
        "Engines" => "engine core Xray sing-box protocol assignment",
        "Templates" => "template JSON custom outbound configuration",
        _ => string.Empty
    };

    private static WorkspacePage Page(string key, string group, string chinese, string english, string icon, string keywords, string description, string englishDescription)
        => new(key, group, WorkspaceText.T(chinese, english), icon,
            $"{chinese} {english} {keywords} {SearchAliases(key)}", WorkspaceText.T(description, englishDescription));
}
