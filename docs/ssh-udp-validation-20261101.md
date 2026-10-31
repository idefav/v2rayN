# SSH UDP 双模式与自动部署验证记录

日期：2026-11-01（Asia/Shanghai）。本记录对应工作区尚未发布的修改；不是线上验收证明。旧的 427 项通过仅为基线，不能代表新增部署功能已验收。使用两台专用、无主机目录挂载的 Ubuntu 25.10 VM：ARM64 原生虚拟化、x86_64 QEMU 仿真。没有连接生产服务器或替换正在运行的代理客户端。

## 已完成

| 验证 | 结果与边界 |
| --- | --- |
| .NET 共享服务回归 | 445 项：434 通过、11 环境跳过、0 失败。新增两个真实部署测试要求显式环境变量，单独运行如下 |
| Go worker、部署助手、sing-box SSH | race 测试通过；认证失败、超时、策略解析、校验和、事务恢复、三种 NAT 映射、多目标、边界与取消均有回归 |
| 并发建立限制 | 最大同时 4 个建立任务；取消竞态修复后压力用例连续 30 次通过。64 关联上限与队列/空闲回收保留 |
| Avalonia | 中文浅色、英文深色各 11 项界面检查通过，包含实际 ComboBoxItem 禁选、手动刷新/部署入口和凭据变化失效；截图为 headless Skia 渲染 |
| WPF | macOS 交叉编译 Release 和 Windows x64 自包含发布构建通过；配套核心/worker/助手已打包，未执行 Windows GUI |
| Linux ARM64 | 官方 GNU 资产、原版 SSH + 免密 sudo、修改版 SSH + root、需密码 sudo，均完成真实 SFTP、systemd、心跳及随机令牌 UDP 回显 |
| Linux x86_64 | 官方 musl 资产、原版 SSH + sudo，部署和重复部署通过；运行在 QEMU 仿真环境 |
| 重复部署 | 保留安装标识和端口，健康同版本复用；当前节点配置未被改变 |
| 端口冲突 | 7300 由独立测试进程占用时自动选 7301，两个监听进程仍然存活 |
| 配置与更新回滚 | 错误二进制摘要被拒；测试专用“版本检查成功但启动失败”程序触发回滚，恢复先前托管服务和安装标识；无 UDP 令牌证据不能提交 |
| 断连、重启 | 客户端留下未提交事务后关闭连接、重启 VM，持久定时器到期后状态为 rolled_back；此前已提交网关随系统启动 |
| 新连接验证 | 修改版 sshd 自动添加当前 root 账号的精确例外、配置检查、平滑重载，再通过新连接和 UDP 回显提交 |
| 实际 ACL | 不可达 HTTP ForwardProxy + 正确例外可以到达本机网关；错误端口、PermitOpen 不允许、AllowTcpForwarding no、DisableForwarding yes、密钥 no-port-forwarding 均不能绕过权限 |
| 实际能力响应 | 原版明确不支持；修改版启用、AllowUdpForwarding no、DisableForwarding、密钥限制及 HTTP 上游不支持按协议区分；网络/认证错误保持未知 |
| 实际报文 | 自定义和 tun2proxy 均完成 IPv4、IPv6、域名、空报文及 60,000 字节 UDP 回显 |
| 慢 DNS | LD_PRELOAD 将直接目标、SOCKS5 主机、UDP relay 的解析分别延迟 10 秒；8 个并发通道在约 2.0–2.6 秒被拒绝，同一 SSH 上已有 TCP 回显不中断（单次期限 500 ms） |

源码和编译不等于完整实机验收。尚需真实 Windows 部署窗口、系统 TUN、两种模式的公网 DNS/STUN/HTTP3、真实透明中继、长时间网络切换和并发性能基准；Linux x86_64 为仿真验证。NAT 回归调用实际映射/路由组件，但没有创建系统 TUN。旧的公网结果不计入本次验收。因此**整套功能尚未标记为全部验收完成**。

## 构建与复现

三个工作区：`v2rayN`、相邻 `sing-box`、`/Users/idefav/Documents/sources/openssh-portable`。配套核心版本为 `1.14.3-ssh-udp.3`，由固定 fork 提交与仓库补丁构建，保留完整平台功能标签。

```sh
# v2rayN 仓库
bash packaging/build-ssh-core.sh darwin arm64 /tmp/ssh-build/bin/sing_box/sing-box
bash packaging/build-udpgw-helpers.sh /tmp/ssh-build
cd proxy-check
go test -race ./...
go build -o /tmp/ssh-build/proxy-check .
# sing-box 仓库
go test -race ./protocol/ssh/...
go test -race ./protocol/ssh -run '^TestUDPSetupQueueBoundAndCancellation$' -count=30
# v2rayN 仓库，.NET 10 SDK
dotnet build v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj -c Release
dotnet v2rayN/ServiceLib.Tests/bin/Release/net10.0/ServiceLib.Tests.dll
```

真实测试需要测试目录中的 `proxy-check`、`bin/sing_box/sing-box` 与清单，以及 `udpgw-deploy/` 下两架构助手和 SHA256SUMS。使用本地回环 TCP 转发到专用 VM，固定经过管理通道核对的主机公钥。Lima 的 vsock SSH 可能将 SSH_CONNECTION 报为 UNKNOWN，自动部署会停止，测试须使用真正 TCP 连接。

环境变量 `SSH_UDPGW_TEST_PROFILE` 指向私有 JSON（不要提交真实凭据）：

```json
{
  "disposableFixture": true,
  "port": 29423,
  "username": "testuser",
  "privateKeyPath": "/absolute/path/to/disposable-key",
  "hostKey": "ssh-ed25519 VERIFIED_PUBLIC_KEY",
  "needsBypass": false,
  "sudoPassword": ""
}
```

运行 `SshGatewayLiveTests/DisposableLinuxDeploymentAndRepeatedInstallVerifyTheRealGateway` 完成两次部署。修改版尚无例外时将 needsBypass 设为 true；已经存在有效例外则为 false。`SSH_UDPGW_LIFECYCLE=1` 配合 `DisposableLinuxFailureRollbackAndPersistentWatchdog` 会故意留下一个待提交事务，供重启/断连恢复测试；最后核对服务、实际监听、事务 record.json 和对应 timer，不能只看客户端连接退出。

OpenSSH 的 `regress/udp/` 提供 systemd、TCP/UDP/SOCKS echo 和慢解析夹具。sing-box 的 `TestNativeSSHDatagramsAndGateway`、`TestNativeSSHPolicyAndACL`、`TestNativeSlowDNSDoesNotBlockTCPOrLeakResolvers` 使用同一 JSON。策略测试通过 `SSH_UDP_EXPECT_CUSTOM=unsupported|0|1|2` 与 `SSH_UDP_EXPECT_GATEWAY=ready|denied` 设置预期；慢解析用例通过 `SSH_UDP_NATIVE_SLOW=direct|proxy|relay` 选择场景，必须先配置对应服务。

界面复现：

```sh
dotnet build v2rayN/v2rayN.Desktop.Review -c Release
dotnet v2rayN/v2rayN.Desktop.Review/bin/Release/net10.0/v2rayN.Desktop.Review.dll /tmp/ssh-ui \
  --ssh-udp-only --headless --language=zh-Hans --theme=light --width=1000
```

构建产物、摘要、日志和截图位于工作区 `artifacts/ssh-udp-deployment-20261101`。OpenSSH 二进制属于该测试环境的验证构建（Linux 前缀 `/opt/ssh-udp-fixture`，macOS 前缀 `/tmp/ssh-udp-openssh-install`），生产部署应依服务器 OpenSSL/glibc、PAM 和安装前缀重新编译。客户端本地构建未公证，未发布到 GitHub，也未安装覆盖现有应用。
