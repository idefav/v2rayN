# SSH UDP、HTTP3 与代理检查：部署和维护记录

记录日期：2026-10-25。此文同时保存实现原理、可重复部署步骤及本次机器的实际配置，便于以后排查和迁移。这里不包含登录密码、私钥或完整用户配置。

## 三个源码仓库

| 项目 | 仓库与 feature 分支 | 本次实现 |
| --- | --- | --- |
| v2rayN | [idefav/v2rayN · feature/native-ssh-profiles](https://github.com/idefav/v2rayN/tree/feature/native-ssh-profiles) | Avalonia 检查页、SSH 开关、分流规则、Go worker、专用核心选择和打包 |
| sing-box | [idefav/sing-box · feature/ssh-udp-forwarding](https://github.com/idefav/sing-box/tree/feature/ssh-udp-forwarding) | 原生 SSH 出站增加 UDP，基于上游 v1.14.3；固定提交 e0ee4f251e66a29bda3c961ae11443c6cbf72978 |
| OpenSSH | [idefav/openssh-portable · feature/ssh-udp-forwarding](https://github.com/idefav/openssh-portable/tree/feature/ssh-udp-forwarding) | 自定义 UDP 能力与通道；实现提交 8a8cc0d7ef3c27ca93d6b3bc013d4eab86224402，独立服务部署说明提交 341d9f889b2d333cdf41acd24d9ea82a8509af66 |

本地工作区中三个仓库相邻：v2rayN、sing-box，以及 `/Users/idefav/Documents/sources/openssh-portable`。修改都提交到 feature，不推 master。sing-box 的 origin 为用户 fork，upstream 为 SagerNet。

`packaging/build-ssh-core.sh` 从用户 fork 拉取精确提交，而非浮动分支；版本为 `1.14.3-ssh-udp.1`，保留 Naive、QUIC 等原有平台功能标签。`core-patches/sing-box-v1.14.3-ssh-udp.patch` 保留相对上游 7054cac5124465ffdb145870d7c01885566711b1 的实现补丁，方便审计和移植；正常打包直接使用上述 fork 提交，不重复应用补丁。

macOS 使用 CGO；Linux/Windows 使用上游支持的 with_purego，并从 go.sum 固定的 Cronet 平台模块将 libcronet.so/libcronet.dll 放到核心同目录，保留 Naive。Linux Cronet 静态库需要 Chromium 专用工具链，不能用普通 GCC 直接链接；Windows TFO 标签还要求 -checklinkname=0。不能为了编译通过静默去掉 Naive 功能，也不能只打包纯 Go 核心而漏掉共享库。

## 为什么需要两端扩展

原版 sing-box 1.14.3 的 SSH 出站只提供 TCP。SOCKS5 UDP ASSOCIATE 成功仅说明本地入口接受了请求，不能说明 SSH 出站支持普通 UDP。DNS 可能被 hijack-dns 转为 DoH/TCP，因此 DNS 成功也不能代替 STUN 或其他普通 UDP 验证。HTTP3 除了依赖 UDP，还可能被内置“阻断udp443”规则拦截。

```text
应用 / 检查组件
  → 本机 mixed 127.0.0.1:10808（SOCKS5 UDP）
  → sing-box SSH UDP 出站
  → 加密 SSH/TCP（可以经过透明中继）
  → 最终 OpenSSH 的 UDP 扩展
  → STUN / DNS / QUIC HTTP3 服务
```

UDP 报文封装在 SSH 的 TCP 字节流内，仍然受 TCP 丢包和队头阻塞影响，不能等同原生 UDP/QUIC 的抗丢包性能。透明中继只转发加密 TCP，不需要安装 UDP 扩展；最终出口必须提供扩展。

### 协议与限制

- 能力请求：`udp-forwarding-v1@idefav.github.io`。accepted=false 表示未协商到扩展；accepted=true 后必须有一个状态字节，0=启用、1=配置或密钥权限禁用、2=上游不支持。
- 数据通道：`direct-udpip-v1@idefav.github.io`，建立参数为 SSH string 目标主机和 uint32 目标端口。每个 UDP 包使用四字节大端长度前缀，不能依赖 SSH Read 的边界。
- 保留 FQDN、IPv4、IPv6、空数据报，最大负载 65507 字节；64 通道、3 秒建立超时、60 秒空闲回收。关闭、取消和异常会释放连接与计数，不重放已发送报文。
- sing-box 必须直接实现 sing 的 NetPacketConn/ReadPacket/WritePacket。其通用 net.PacketConn 适配器将 FQDN 转成 UDPAddr 时会丢掉域名，导致 E_UDP_DESTINATION_MISMATCH；本次已修复并有回归测试。
- 默认关闭 AllowUdpForwarding；DisableForwarding、authorized_keys 限制与 PermitOpen 仍有效。ForwardProxy 使用 HTTP/SOCKS4 时不支持 UDP；SOCKS5 上游需要支持 UDP ASSOCIATE。失败不会改成直连。

## 最终服务器：独立部署，不替换管理 SSH

推荐做法是在独立前缀编译，使用独立 privsep 和 systemd 服务。可重复步骤和通用 unit 位于 OpenSSH feature 分支的 [README.udp-deployment.md](https://github.com/idefav/openssh-portable/blob/feature/ssh-udp-forwarding/README.udp-deployment.md)、[contrib/sshd-udp.service](https://github.com/idefav/openssh-portable/blob/feature/ssh-udp-forwarding/contrib/sshd-udp.service)。

Linux/PAM 示例：

```sh
sudo apt-get install build-essential autoconf automake libssl-dev zlib1g-dev libpam0g-dev
# 在上述 OpenSSH feature 源码目录执行
autoreconf
./configure --prefix=/opt/openssh-udp --sysconfdir=/etc/ssh \
  --with-privsep-path=/var/empty/sshd-udp --with-pam
make -j2 sshd sshd-session sshd-auth
sudo install -d -m 0755 /opt/openssh-udp/sbin /opt/openssh-udp/libexec /var/empty/sshd-udp
sudo install -m 0755 sshd /opt/openssh-udp/sbin/
sudo install -m 0755 sshd-session sshd-auth /opt/openssh-udp/libexec/
sudo /opt/openssh-udp/sbin/sshd -t -f /etc/ssh/sshd_config -p 10026 -oAllowUdpForwarding=yes
sudo install -m 0644 contrib/sshd-udp.service /etc/systemd/system/
sudo systemd-analyze verify /etc/systemd/system/sshd-udp.service
sudo systemctl daemon-reload
sudo systemctl enable --now sshd-udp.service
```

复用原认证和主机密钥前，核对原配置是否在 ListenAddress 中写死了端口；不要让独立服务重新占用原管理端口。privsep 目录必须 root 所有且不可被其他用户写入。只放行 **TCP 10026**，无需开放 UDP 10026；主机防火墙和云安全组都要核对。本地监听正常不代表公网可达。

### 本次实际部署

| 项目 | 值 |
| --- | --- |
| 最终出口 | 43.98.191.120 |
| 新服务入口 | TCP 10026，IPv4/IPv6 监听 |
| 独立服务 | sshd-udp-10026.service，已开启自启动 |
| 独立安装前缀 | /opt/openssh-udp-20261025-independent |
| sshd 程序 | /opt/openssh-udp-20261025-independent/sbin/sshd |
| 辅助程序 | 同前缀 libexec/sshd-session、libexec/sshd-auth |
| privsep | /var/empty/sshd-udp-10026 |
| RuntimeDirectory | /run/sshd-udp-10026 |
| 认证、主机密钥配置 | 复用 /etc/ssh/sshd_config；启动参数覆盖 -p 10026 -oAllowUdpForwarding=yes |
| 原系统管理服务 | ssh.service + ssh.socket，仍监听 TCP 22，原二进制、配置和主机密钥保留 |
| 构建、备份与恢复记录 | /root/ssh-udp-20261025 |

```sh
sudo systemctl status sshd-udp-10026.service ssh.service ssh.socket
sudo journalctl -u sshd-udp-10026.service -n 50 --no-pager
sudo /opt/openssh-udp-20261025-independent/sbin/sshd -t \
  -f /etc/ssh/sshd_config -p 10026 -oAllowUdpForwarding=yes
sudo ss -lntp
```

此前曾经授权尝试替换 22，因停原服务时其 `/run/sshd` 被清理而失败，随后自动回滚恢复。最终按独立部署要求保留原 22，采用稳定、独立 privsep，避免共享目录生命周期问题。临时回环 2222 测试服务已停止，旧失败替换服务已停用；后续不要重新执行旧 cutover 脚本。

独立服务回滚只需停止/禁用 sshd-udp-10026.service，原管理服务继续保留。不要为回滚 UDP 服务停止原 ssh.service 或 ssh.socket。

## 国内和香港中继

两条入口分别直达最终服务，不串联：

| 入口 | 最终目标 | 中继服务 | 中继配置 |
| --- | --- | --- | --- |
| 国内 106.14.159.136:10024 | 43.98.191.120:10026 | sshd-relay-2.service | /etc/ssh/sshd_relay_2_config |
| 香港 47.56.255.129:10024 | 43.98.191.120:10026 | sshd-relay-sg.service | /etc/ssh/sshd_relay_sg_config |

核心配置行为：`SSHRelayTarget 43.98.191.120:10026`，`SSHRelayConnectTimeout 10s`。监听仍是 TCP 10024，客户端端口无需改为 10026。客户端核对的是最终出口的公钥和认证，不是透明中继的管理 SSH 公钥。

修改前备份配置和 unit，确认中继到新目标能取得 SSH banner；修改后执行 `/opt/openssh-relay/sbin/sshd -t -f <配置路径>`，再 `systemctl reload <中继服务>`，用 HUP 平滑重载并验证原管理入口仍能建立新连接。本次两台中继 PID 保持，原 10023、10025 和管理 SSH 配置通过 SHA-256 比对。验收后已取消自动回滚定时器，备份保留于各机器 `/root/ssh-relay-udp-20261025`。

若需恢复 10024 指向旧 22：国内运行 `sh /root/ssh-relay-udp-20261025/rollback.sh cn`，香港运行相同路径的 `rollback.sh hk`。这会恢复对应中继配置及 unit，不改变其他入口。当前线上状态为新 10026。

## 客户端使用和专用核心

1. 安装 feature 预览包，保留已有节点、密钥和固定主机公钥。
2. 直连使用出口 TCP 10026；国内/香港中继继续使用各自 TCP 10024。
3. 编辑 SSH 节点，在高级选项启用 **UDP over SSH** 和 **允许 HTTP3**，保存后启用节点。
4. 打开“代理检查”，填写当前节点的预期出口 IP/CIDR，本次为 43.98.191.120，再执行检查。按目标分流时可以配置不同预期出口。

两个选项默认关闭。允许 HTTP3 只在生成配置时跳过精确的内置通用“阻断udp443”规则，不删除数据库规则，不跳过附有域名/IP/进程条件的自定义拒绝规则。原 DNS/DoH 路径和 .local 局域网规则保留。

**安装升级陷阱**：原 CoreManager 初始化不会覆盖用户 bin，旧原版核心可能留在 Application Support 中；仅看到 App 已安装不代表实际启动了 App 内核心。典型报错是 `outbounds[0].udp_forwarding: json: unknown field "udp_forwarding"`。

修订版新增 BundledSshCore：只在 `bin/sing_box/ssh-udp-core.json` 的 schema、专用版本和二进制 SHA-256 均匹配时，优先于旧用户目录核心调用随包核心。通过[更新与资源来源](update-sources.zh-Hans.md)显式安装并验证成功的更新核心具有更高优先级；旧用户目录文件不会自动获得此优先级。启用 SSH UDP 时应使用兼容扩展的核心，例如将更新仓库配置为 `idefav/sing-box`；官方源为默认值，但无法加载当前扩展配置的更新会被拒绝。

macOS 核实实际路径，而非只查安装目录版本：

```sh
lsof -nP -iTCP:10808 -sTCP:LISTEN
# 把 PID 替换为上面实际核心进程
lsof -a -p PID -d txt
"/Applications/v2rayN.app/Contents/MacOS/bin/sing_box/sing-box" version
```

正确运行路径为 `/Applications/v2rayN.app/Contents/MacOS/bin/sing_box/sing-box`。签名可能改变二进制摘要，打包应在最终核心签名后生成清单，再签 App 外层并同时校验清单和 `codesign --verify --deep --strict`。ZIP/DMG 都必须保留核心、清单和执行权限。

本地未公证测试包的 macOS 拦截命令保留在安装盘背景与安装说明中；校验来源和 SHA-256 后，对已安装的 App 使用：

```sh
xattr -dr com.apple.quarantine "/Applications/v2rayN.app"
```

## 检查的含义和验收

检查页区分本地代理入口与系统实际路径。普通 UDP 用 STUN 事务匹配及映射地址验证双向通信；HTTP3 使用 quic-go 真正执行 QUIC/TLS 和 HTTP/3，禁止回退后报成功；DNS 覆盖 UDP/TCP、A/AAAA、NXDOMAIN 和截断重试。DNS 调试查询的出口只对应这条 DNS 请求，不能证明所有系统 DNS 都没有泄漏。

报告状态区分通信成功、出口符合预期、不支持、无法判定、跳过与取消。公共端点超时不直接当作泄漏。基线仅保存当前状态，不会绕过或关闭 TUN。

2026-10-25 实际验收：公网直连、国内中继、香港中继均通过连续三轮 HTTPS、STUN、真实 HTTP3 和 DNS；修订包安装后真实 10808 入口再次验收，香港和国内分别 30/30，通过项目的所有可观察出口均为 43.98.191.120，HTTP3 协商 h3，证书验证成功。系统 DNS A/AAAA 记录独立核对。服务器无 IPv6 默认路由，因此 AAAA 解析通过不代表公网 IPv6 通信已验收。

本次提交的独立源码快照 .NET 回归为 216 项、210 通过、6 环境跳过，Avalonia Release 构建通过；Go worker 和 SSH 扩展 race 测试通过。覆盖远端不支持/禁用、错误摘要、保留旧核心升级、错误出口、认证和主机公钥失败、报文边界、FQDN、并发限制、超时和清理。浏览器 WebRTC/DoH、断网保护、网络切换、长连接、LAN/.local 专项、UDP 大包和 MTU 不属于这次公共端点验收。

原始实测与本地包保存在工作区 artifacts/ssh-relay-udp-20261025、artifacts/v2rayn-ssh-udp-20261025，未将运行配置和私钥提交到源码仓库。

## 常见排障

| 现象 | 核对 |
| --- | --- |
| unknown field udp_forwarding | 实际运行路径、核心版本、随包清单摘要，而非只看 App 版本 |
| E_UDP_PROXY_UNSUPPORTED | 最终入口是否仍指向原 22 / 上游原版 SSH；能力是否 accepted=false |
| E_UDP_FORWARDING_DISABLED | AllowUdpForwarding、DisableForwarding、authorized_keys 和 PermitOpen |
| E_UDP_UPSTREAM_UNSUPPORTED | 最终服务是否配置了 HTTP/SOCKS4 或不支持 UDP 的 SOCKS5 上游 |
| DNS 正常、STUN 失败 | DNS 可能走 DoH/TCP；检查 SSH UDP 扩展，不要把 DNS 通过当普通 UDP 通过 |
| STUN 正常、HTTP3 失败 | 高级选项允许 HTTP3、UDP443 分流规则、真实 QUIC 握手阶段及公共端点 |
| 本地监听正常、公网超时 | 云安全组和主机防火墙、目标 TCP 端口；无需开放入口 UDP 同号端口 |
| 重启系统 SSH 后独立服务失败 | privsep 是否错误地共用了 /run/sshd，检查独立目录和运行权限 |

## 构建和触发测试包

```sh
bash packaging/build-ssh-core.sh darwin arm64 /tmp/ssh-core/sing-box
cd proxy-check && go test -race ./...
# 在仓库根目录
dotnet test --project v2rayN/ServiceLib.Tests -c Release
```

共享 CI 构建固定 fork 核心和 proxy-check，按平台架构交付。v2rayN 的 `v7.25.6-ssh-preview.N` 标签触发 release-feature；若该 workflow 尚未在默认分支被发现，使用已有 build-all.yml 入口，ref 指向预览标签、release_tag 输入同标签。不要对 master dispatch 普通发布分支。验收必须检查 Actions 完成、11 个发布资产、SHA256SUMS 和匿名下载，不能只看 push 成功。
