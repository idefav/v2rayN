namespace ServiceLib.Resx;

public static partial class SshStrings
{
    public static string DetectUdp => ProxyCheckText.T("检测 UDP 能力", "Detect UDP capabilities");
    public static string DeployGateway => ProxyCheckText.T("部署 tun2proxy 网关", "Deploy tun2proxy gateway");
    public static string SudoPassword => ProxyCheckText.T("sudo 密码（仅本次操作；root 或免密 sudo 留空）", "sudo password (this operation only; leave empty for root/passwordless sudo)");
    public static string StopOperation => ProxyCheckText.T("取消操作", "Cancel operation");
    public static string UdpUnknown => ProxyCheckText.T("UDP 能力未知；检测失败不会改变已有模式。", "UDP capability unknown; failed probes do not change the configured mode.");
    public static string UdpCredentialsRequired => ProxyCheckText.T("请先填写完整凭据并确认服务器公钥。", "Complete authentication and confirm the server host key first.");
    public static string DeployPreflight => ProxyCheckText.T("正在预检目标服务器、下载并校验部署资源…", "Checking the target server and verifying deployment resources…");
    public static string DeployInstalling => ProxyCheckText.T("正在安装并验证；当前代理继续运行。", "Installing and verifying; the current proxy keeps running.");
    public static string DeployCompleted => ProxyCheckText.T("部署及随机令牌 UDP 回显已验证，已回填草稿。保存并启用节点后才应用。公网 DNS、STUN、HTTP3 请分别检测。", "Deployment and token UDP echo verified; draft updated. Save and activate the node to apply. Test public DNS, STUN and HTTP3 separately.");
    public static string DeployCancelled => ProxyCheckText.T("操作已取消。远端状态已尝试核对；无法确认时保留事务记录，下次部署先核对，不视为已回滚。", "Operation cancelled. Remote reconciliation was attempted; uncertain transactions are retained and checked before retrying.");

    public static string UdpCapabilityResult(SshUdpCapabilitySnapshot result)
    {
        var custom = result.Custom switch
        {
            "enabled" => ProxyCheckText.T("自定义 UDP：服务端声明已启用", "Custom UDP: declared enabled"),
            "unsupported" => ProxyCheckText.T("自定义 UDP：服务端明确不支持", "Custom UDP: explicitly unsupported"),
            "disabled" => result.Reason switch
            {
                "E_UDP_ALLOW_DISABLED" => ProxyCheckText.T("自定义 UDP：服务端未开启 AllowUdpForwarding", "Custom UDP: AllowUdpForwarding is disabled"),
                "E_UDP_DISABLE_FORWARDING" => ProxyCheckText.T("自定义 UDP：DisableForwarding 禁止转发", "Custom UDP: DisableForwarding denies forwarding"),
                "E_UDP_KEY_RESTRICTED" => ProxyCheckText.T("自定义 UDP：当前认证密钥禁止端口转发", "Custom UDP: the authenticated key forbids port forwarding"),
                _ => ProxyCheckText.T("自定义 UDP：被配置或密钥权限禁用", "Custom UDP: disabled by configuration or key permissions"),
            },
            "upstream_unsupported" => ProxyCheckText.T("自定义 UDP：上游代理类型不支持", "Custom UDP: upstream proxy type unsupported"),
            _ => UdpUnknown + " " + (result.Reason switch
            {
                "E_SSH_TIMEOUT" or "E_PROBE_TIMEOUT" => ProxyCheckText.T("检测超时。", "Probe timed out."),
                "E_SSH_UNREACHABLE" => ProxyCheckText.T("无法连接服务器。", "Server unreachable."),
                "E_SSH_AUTH_OR_HANDSHAKE" => ProxyCheckText.T("认证或 SSH 握手失败。", "Authentication or SSH handshake failed."),
                "E_SSH_HOST_KEY_MISMATCH" => ProxyCheckText.T("服务器公钥与已确认记录不匹配。", "Server host key does not match the confirmed key."),
                "E_SSH_PRIVATE_KEY_READ" or "E_SSH_PRIVATE_KEY_PARSE" => ProxyCheckText.T("无法读取私钥或私钥口令不正确。", "Private key unavailable or passphrase incorrect."),
                _ => ProxyCheckText.T("检测失败，请重试。", "Probe failed; retry."),
            }),
        };
        var gateway = result.Gateway == "ready" ? ProxyCheckText.T("网关心跳正常", "Gateway heartbeat verified")
            : ProxyCheckText.T("网关尚未验证就绪", "Gateway readiness not verified");
        return $"{custom}\n{gateway} · {result.CheckedAt.ToLocalTime():HH:mm:ss}\n" +
            ProxyCheckText.T("能力声明与心跳不代表 UDP 数据、DNS 或 HTTP3 已可用。", "Declaration and heartbeat do not establish UDP data, DNS or HTTP3 availability.");
    }

    public static string DeployPreview(ProfileItem node, string fingerprint, SshGatewayPlan plan)
        => $"{node.Username}@{node.Address}:{node.Port}\n{fingerprint}\n\nLinux {plan.Architecture} · tun2proxy 0.8.7\nTCP 127.0.0.1:{plan.Port}\n/opt/v2rayn/udpgw\nv2rayn-udpgw.service\n\n" +
            (plan.Ssh.NeedsBypass ? $"{plan.Ssh.Config}\nMatch User {node.Username}\nForwardProxyBypass 127.0.0.1:{plan.Port}\n" + ProxyCheckText.T("将检查配置、平滑重载并通过新 SSH 连接验证。", "Configuration will be checked, gracefully reloaded and verified over a new SSH connection.")
                : ProxyCheckText.T("无需修改 SSH 配置。", "No SSH configuration change required.")) + "\n\n" +
            ProxyCheckText.T("网关以非 root 身份运行，开机启动，只监听回环地址。UDP 从该服务器本机出网，不继承 ForwardProxy 上游。失败将核对远端事务并回滚；不会替换 sshd。继续部署？", "The gateway runs without root, starts at boot and listens on loopback only. UDP exits this server directly and does not inherit the ForwardProxy upstream. Failures reconcile and roll back the transaction; sshd is never replaced. Deploy?");

    public static string DeployError(string code)
    {
        var explanation = code switch
        {
            "E_SSHD_UPGRADE_REQUIRED" => ProxyCheckText.T("此 sshd 启用了 ForwardProxy，但不支持精确网关例外。请先升级配套 OpenSSH；客户端不会替换 sshd。", "ForwardProxy is enabled but this sshd lacks precise gateway exceptions. Upgrade the paired OpenSSH first; the client will not replace sshd."),
            "E_SUDO_PASSWORD" => ProxyCheckText.T("需要正确的 sudo 密码或免密 sudo 权限。", "A valid sudo password or passwordless sudo permission is required."),
            "E_BINARY_INCOMPATIBLE" => ProxyCheckText.T("官方二进制与服务器运行环境不兼容，部署已停止。", "The official binary is incompatible with this server; deployment stopped."),
            "E_TCP_PERMIT_OPEN" or "E_TCP_FORWARDING_DISABLED" => ProxyCheckText.T("当前账号的 SSH TCP 转发策略不允许访问网关。请由管理员检查 PermitOpen、AllowTcpForwarding 和 DisableForwarding。", "SSH TCP forwarding policy denies this account access to the gateway. Ask the administrator to check PermitOpen, AllowTcpForwarding and DisableForwarding."),
            "E_SSHD_SERVICE_UNKNOWN" or "E_SSHD_COMMAND_AMBIGUOUS" or "E_SSHD_BINARY_UNKNOWN" => ProxyCheckText.T("无法可靠识别当前连接所属的 sshd 服务或配置，已停止自动配置。请由管理员检查服务启动方式。", "The sshd service or configuration owning this connection cannot be identified reliably. Automatic configuration stopped; ask the administrator to check the service launch configuration."),
            "E_PENDING_TRANSACTION" => ProxyCheckText.T("存在尚未完成或需人工核对的远端事务，请等待回滚期限后重试并检查事务记录。", "An unfinished remote transaction needs reconciliation. Retry after its rollback deadline and inspect the transaction record."),
            "E_DEPLOY_COMPONENT" => ProxyCheckText.T("安装包缺少 Linux 部署助手，请使用完整配套构建。", "The package is missing Linux deployment helpers; use the complete paired build."),
            "E_PREFLIGHT_CHANGED" or "E_SERVICE_LISTENER" => ProxyCheckText.T("目标状态或端口已变化，请重新预检。不会覆盖未知服务。", "Target state or port changed; run preflight again. Unknown services are never overwritten."),
            _ => ProxyCheckText.T("部署未完成。请查看错误代码和事务记录；检测或连接失败不代表远端已回滚。", "Deployment did not complete. Check the error code and transaction record; a failed connection does not establish that remote rollback finished."),
        };
        return explanation + "\n" + SshGatewayRemote.SafeCode(code);
    }
}
