# 浏览器 WebRTC 出口检查

代理检查页工具栏新增“浏览器 WebRTC”。这会创建独立的一轮报告，打开系统默认浏览器进行 1–3 次采样，不自动修改系统代理、TUN、节点或浏览器策略。原有“开始检查”仍运行套接字/代理入口矩阵；它不能代替真实浏览器流量。

使用前，在“配置服务”弹窗导入独立 egress-probe 的公开 profile，并填写仅驻留内存的 API 凭证。服务端必须支持 `webrtc` 能力，HTTPS 443 和 UDP 3480 必须可达。保留浏览器页面打开，结果会自动回到 v2rayN，可导出报告。取消、应用退出或检测到核心/路由变化时删除会话并释放连接；本轮最长等待 165 秒。

每个样本分别显示：

- 真实 ICE → DTLS → SCTP 双向 DataChannel 通信是否成功。
- 服务器签名的已选中 ICE 对端公网 IP/端口。
- 对预期出口 IP/CIDR 的比较；没有预期时显示“已观测，未配置预期”。
- 浏览器自报的候选地址、浏览器标识、耗时及失败阶段；这些自报数据不属于签名证据。

单独设置该路径预期的示例：

```text
api.check.idefav.com:3480|webrtc|browser=43.98.191.120
```

私网 host 候选本身不能证明公网泄漏，`.local` 候选是 mDNS 隐私地址。当前不使用第三方 STUN/TURN，因此不枚举它们可能产生的全部 srflx/relay 候选。本功能不覆盖 TURN、IPv6 WebRTC 传输、音视频媒体或浏览器 DoH。没有摄像头或麦克风权限请求。永久 API 凭证不传给网页；网页只接收短期票据和公钥，不保存 SDP 或 ICE 密码。

2026-10-25 公网验收：Chrome 155 三次签名数据通道全部通过，出口为 `124.79.235.121`。同轮 SOCKS 抽检 15/15 通过，其中 TCP/TLS、HTTP(S)、HTTP3、UDP、STUN 出口为 `43.98.191.120`；权威 DNS 看到的是递归解析器地址，不能拿来冒充数据通道出口。浏览器出口与 SOCKS 出口确实不同；配置上述预期后，浏览器样本应显示“通信通过、出口失败”。

服务器升级注意：systemd 单元需允许 AF_NETLINK 以枚举 ICE 网卡/路由，保留非 root 身份及 CAP_NET_BIND_SERVICE；详见独立 egress-probe 仓库的 `deploy/webrtc.md` 和 `deploy/check.idefav.com.md`。

验证：`ServiceLib.Tests` 的 `WebRtcTests` 检查签名协议绑定、错误出口、CIDR 规则、候选与真实出口隔离；Go 服务端测试覆盖真实 ICE/DTLS/SCTP、回执确认、认证、重放与会话清理。公共验收和测试报告保留在工作区 `artifacts/egress-probe-us-deploy-20261025`，不包含永久凭证。
