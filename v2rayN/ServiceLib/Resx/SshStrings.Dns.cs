namespace ServiceLib.Resx;

public static partial class SshStrings
{
    public static string DnsTitle => ProxyCheckText.T("服务端 DNS", "Server DNS");
    public static string DnsDeploy => ProxyCheckText.T("部署并启用 / 接管", "Deploy and enable / Adopt");
    public static string DnsCheck => ProxyCheckText.T("检测 DNS", "Check DNS");
    public static string DnsRollback => ProxyCheckText.T("回退", "Roll back");
    public static string DnsDetailsLabel => ProxyCheckText.T("查看详情", "Details");
    public static string DnsDeploymentPhase(string? phase) => ProxyCheckText.T("部署状态：", "Deployment: ") + (phase switch
    {
        "absent" => ProxyCheckText.T("未部署", "Not deployed"),
        "legacy" => ProxyCheckText.T("发现手工部署，待校验接管", "Manual installation; adoption not yet validated"),
        "committed" => ProxyCheckText.T("已部署", "Deployed"),
        "rolled_back" => ProxyCheckText.T("已回退", "Rolled back"),
        _ => ProxyCheckText.T("未确认", "Unconfirmed")
    });
    public static string DnsNotice => ProxyCheckText.T(
        "Ubuntu 24.04 · 服务端即时生效，关闭编辑窗口不会撤销。仅验证 SSH 远端 IPv4 解析路径，不修改客户端 TUN、DNS 或浏览器 DoH。非 root 用户使用上方 sudo 密码。",
        "Ubuntu 24.04 · Server changes take effect immediately; closing the editor does not undo them. Checks cover SSH remote IPv4 resolution, not client TUN, DNS or browser DoH. Non-root users use the sudo password above.");
    public static string DnsResult(SshDnsCheckResult result)
    {
        var verdict = result.Verdict switch
        {
            SshDnsVerdict.Match => ProxyCheckText.T("DNS 与出口一致", "DNS matches egress"),
            SshDnsVerdict.Mismatch => ProxyCheckText.T("DNS 与出口不一致", "DNS differs from egress"),
            SshDnsVerdict.Cancelled => ProxyCheckText.T("已取消", "Cancelled"),
            _ => ProxyCheckText.T("无法确认", "Inconclusive")
        };
        return $"{verdict}\n{ProxyCheckText.T("IPv4 出口", "IPv4 egress")}: {result.ExitIp ?? "—"}\nDNS: {string.Join(", ", result.DnsIps)}\n"
            + $"{result.Probes}/3 · {result.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            + (result.Error == null ? "" : "\n" + DnsError(result.Error));
    }
    public static string DnsError(string code) => code + " · " + (code switch
    {
        "E_EXIT_MISMATCH" => ProxyCheckText.T("节点实际出口与此 SSH 主机不同，未修改转发拓扑。", "Node egress differs from this SSH host; forwarding was not changed."),
        "E_DNS_MISMATCH" => ProxyCheckText.T("DNS 来源与出口不一致，部署未提交。", "DNS sources differ from egress; deployment was not committed."),
        "E_DNS_QUERY_TIMEOUT" => ProxyCheckText.T("递归 DNS 查询超时，部署未提交；请检查服务端公网 TCP/UDP 53 连通性。", "Recursive DNS timed out; deployment was not committed. Check server access to public TCP/UDP port 53."),
        "E_UBUNTU_2404" or "E_NETPLAN_LAYOUT" or "E_RESOLVER_LAYOUT" => ProxyCheckText.T("此系统或解析布局暂不支持自动部署，可继续检测。", "This system or resolver layout does not support deployment; checks remain available."),
        "E_CONFIGURATION_DRIFT" => ProxyCheckText.T("配置已被其他操作修改，未覆盖。请检查服务端备份。", "Configuration changed externally and was not overwritten. Inspect the server backup."),
        "E_LEGACY_BACKUP" or "E_LEGACY_CONFIG" => ProxyCheckText.T("手工部署或原始备份未通过校验，仅允许检测。", "Legacy installation or original backup could not be validated; use checks only."),
        "E_PORT_OCCUPIED" => ProxyCheckText.T("回环 DNS 端口已占用，未停止其他服务。", "Loopback DNS port is occupied; other services were not stopped."),
        "E_PENDING_TRANSACTION" => ProxyCheckText.T("有未完成的部署，请先回退或等待自动回退。", "A deployment is pending. Roll back or wait for automatic rollback."),
        "E_ROLLBACK_UNCONFIRMED" => ProxyCheckText.T("回退结果尚未确认；服务端保留事务及定时保护，请恢复 SSH 后检查或重试回退。", "Rollback is unconfirmed. The server retains its journal and timer; reconnect and check or retry rollback."),
        "E_SPLIT_DNS_UNSUPPORTED" => ProxyCheckText.T("检测到无法安全接管的 DNS 分流，未修改配置。", "Split DNS cannot be safely adopted; configuration was not changed."),
        "E_NOT_INSTALLED" => ProxyCheckText.T("没有可回退的受管部署。手工部署须先校验接管。", "No managed deployment to roll back. Validate and adopt a legacy installation first."),
        _ => ProxyCheckText.T("操作未完成；检测站异常不代表当前连接故障。", "Operation incomplete; observer failure does not mean the current connection is down.")
    });
}
