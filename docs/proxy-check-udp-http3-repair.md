# 当前 SOCKS 普通 UDP / HTTP3 失败分析与修复方案

检查时间：2026-10-25。以下根因来自原进程与隔离复现。修复已实施，独立服务部署在 43.98.191.120:10026；原系统 SSH 22 保留。未替换本机正在运行的客户端。

## 已确认的根因

当前 `127.0.0.1:10808` 由 sing-box 1.14.3 提供 mixed 入站，默认出站为原生 `type: ssh`，运行模式为 `Rule`。

1. **普通 UDP：SSH 出站本身不支持。** 用相同配置在独立本地端口启动临时 sing-box，发送 STUN 后立即得到 `router: UDP is not supported by outbound: proxy`。上游 v1.14.3 的 SSH 出站只声明 TCP；`ListenPacket` 返回 `os.ErrInvalid`。因此 SOCKS5 握手和 UDP ASSOCIATE 成功，只能说明本地入口接受了请求，不能证明出站具有 UDP 能力。
2. **HTTP3：当前还命中 UDP 443 拒绝规则。** 隔离日志明确显示 `router: match[5] network=udp port=443 => reject`，随后 `connection closed: rejected`。这条规则也存在于项目内置路由模板。仅取消它仍会遇到第一项的 SSH UDP 能力缺失。
3. **最终 SSH 服务未提供已有自定义 UDP 能力。** 使用当前节点的密钥、固定主机公钥及配置中的主机密钥算法完成严格认证，发送 `udp-forwarding-v1@idefav.github.io`，返回 `accepted=false`、空负载。在已有自定义 OpenSSH 实现中，即使 UDP 被配置关闭，也会识别请求并返回 `accepted=true, status=1`；因此当前结果属于没有协商到该扩展，不能按“只打开 AllowUdpForwarding 就够了”处理。

DNS UDP 成功另有原因：入站 DNS 被 `hijack-dns` 接管，远端解析服务器使用 HTTPS/DoH，经 SSH 的 TCP 链路出站。这条路径不需要普通 UDP。隔离复现中 HTTPS 和 DNS UDP 均成功，而 STUN 和 HTTP3 失败；本机普通套接字的 STUN/HTTP3 成功，所以不能把这些失败归咎于公共端点不可用。

后续经授权盘点并构建 OpenSSH 10.2p1 扩展版，独立服务为 sshd-udp-10026.service，使用独立运行目录与安装前缀，复用原认证策略和主机密钥。公网 10026 已严格核对主机密钥，能力协商 accepted=true,status=0。

## 推荐修复：打通已有 UDP-over-SSH 协议

### 1. 最终 SSH 节点

- 先只读盘点最终节点的 sshd 二进制、服务单元、配置和现有管理通道，确认 relay 的真实目标。
- 将本工作区 `openssh-portable` 已有 `direct-udpip-v1@idefav.github.io` 实现构建为独立测试服务；保持原管理 SSH 和生产监听可用，保留备份与回滚入口。
- 启用 `AllowUdpForwarding yes`，同时核对 `DisableForwarding`、authorized_keys 转发限制和 `PermitOpen`。如果设置了 ForwardProxy，必须支持 SOCKS5 UDP ASSOCIATE；HTTP/SOCKS4 上游不能承载这一功能。
- 先在测试端口验证能力返回 `accepted=true,status=0` 和真实数据，最终使用独立 10026 入口。只转发加密 TCP 的中继无需部署 UDP 代码，但目标端口必须指向新服务。

### 2. sing-box 原生 SSH 出站

- 建立固定版本的 sing-box 分支，以当前 v1.14.3 为基线；为 SSH options 增加可选 `udp_forwarding`，默认关闭，保持现有节点兼容。
- 开启时注册 TCP+UDP。实现 SSH 出站的 `ListenPacket`，复用 `ssh-tunnel/core/udptunnel` 的能力协商、4 字节长度帧、完整报文读写及超时语义，并封装为 sing-box 需要的 `net.PacketConn`。
- 按 SSH 连接缓存一次能力协商，重连后重新协商；按目标维护 UDP channel，保留报文边界、源/目标地址及 IPv4/IPv6，不把 SSH 字节流直接当 UDP。
- 沿用已有的报文上限 65507、建立超时 3 秒、空闲超时 60 秒及有界队列。管理 UDP channel 与 TCP stream 的共同连接生命周期，取消和异常时释放资源。
- 不支持、配置禁用、上游不支持分别返回明确错误。失败时不回退直连，不重放已发送报文；UDP 失败不能让原有 TCP 转发失效。
- 将经过上述测试的固定核心构建纳入客户端打包流程。不能只写新增 JSON 字段后仍打包官方原版 sing-box。

### 3. v2rayN 配置、路由和检查页

- 为 SSH 节点增加“UDP over SSH”选项，映射到支持该扩展的核心；保留当前认证、主机密钥固定及密钥口令处理。
- 在当前启用的路由规则集中禁用“阻断udp443”，或为允许 HTTP3 的目标添加优先于该规则的 UDP 443 代理规则。不要改变用户其他明确设置的拒绝或直连规则，也不要自动删除所有路由模板中的拦截项。
- 检查页区分“出站不支持 UDP”“远端扩展不支持/被禁用”“路由拒绝 UDP 443”“探测超时”。DNS 劫持转 DoH 的成功只能作为 DNS 成功，不汇总成普通 UDP 成功。
- 默认继续使用现有 DoH/TCP DNS 路径；不要借此改动现有 DNS、防泄漏或 `.local` 规则。

## 验收与回滚

1. 先通过本地受控 UDP echo / STUN / DNS / HTTP3 fixture，覆盖 TCP-only 服务器、扩展禁用、ACL 拒绝、上游 UDP 不支持、控制连接关闭、并发、取消、空报文与大报文。
2. 用相同目标和预期出口验证原生 SSH 的 TCP、DNS UDP/TCP、非 DNS STUN，以及真正的 HTTP3 请求。要求服务端观察的出口符合目标规则；HTTP3 必须协商 h3，禁止回退 HTTP2 后报通过。
3. 覆盖 Rule / Global / Direct、IPv4 / 可用 IPv6、多跳 relay，确认既有 TCP、证书校验、固定主机密钥及路由不回归。
4. 先交付固定核心的独立预览包，由用户安装；远端切换前保留旧 sshd 服务与旧客户端核心，出现问题可恢复原监听和路由。

修复包包含固定版本核心、UDP 选项、HTTP3 路由选项与能力检查。公网独立入口连续 3 轮 HTTPS、STUN、真实 HTTP3、DNS UDP A/AAAA、DNS TCP 检查全部通过，具有出口证据的项目均为 43.98.191.120。SSH 的承载仍是 TCP；即使 HTTP3 可用，也不保证具有原生 UDP/QUIC 的抗丢包性能。服务器没有 IPv6 默认路由，因此不宣称公网 IPv6 已验收。

## 证据与实现入口

- `v2rayN/ServiceLib/Services/CoreConfig/Singbox/SingboxOutboundService.cs`：当前 SSH 配置映射。
- `v2rayN/ServiceLib/Services/SshDnsPolicy.cs`：经 SSH 的自动 DNS 使用 TCP，并拒绝不支持的 UDP/QUIC DNS。
- `v2rayN/ServiceLib/Sample/custom_routing_white` 等：内置“阻断udp443”。
- 相邻 `ssh-tunnel/core/udptunnel/protocol.go`：已有扩展协议及客户端。
- 相邻 `openssh-portable/serverloop.c` / `udp-channel.c`：能力响应和服务器 UDP channel。
- [sing-box v1.14.3 SSH 出站源码](https://github.com/SagerNet/sing-box/blob/v1.14.3/protocol/ssh/outbound.go)：TCP-only 注册及 ListenPacket 行为。
