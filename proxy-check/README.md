# 内置代理检查组件

SSH UDP 扩展、三个源码仓库、独立服务器部署、中继目标端口、客户端开关和升级排障见 [SSH UDP 部署与扩展记录](../docs/ssh-udp-deployment.zh-Hans.md)。

Avalonia 左侧“代理检查”检查当前运行状态，不启动隔离节点、不改动系统代理或 TUN。TCP/HTTP CONNECT、SOCKS5、普通套接字和系统实际请求分别显示。进入高级选项填写预期出口（IP/CIDR），可按目标指定分流出口，格式为每行 `host=IP/CIDR` 或 `host:port=IP/CIDR`；完整 URL 也可作为精确匹配项。

先关闭代理运行检查并“保存为基线”，再开启代理检查并对比。保存基线不会关闭代理；启用 TUN 时普通套接字仍可能经过 TUN。通信正常不代表出口正确；没有预期出口或服务端出口证据时显示“无法判定”。DNS 调试查询仅说明该 DNS 请求在目标服务处观察到的来源，不能证明所有系统 DNS 都没有泄漏。IPv6 字面量探测不使用双栈回退。

默认公共目标：Cloudflare trace、`stun.cloudflare.com:3478`、`1.1.1.1:53`；高级选项可替换域名目标，固定 IPv4/IPv6 trace 探测单独显示。对公共服务进行有限采样，不以超时推断泄漏。普通 HTTP CONNECT 不承载 UDP、STUN/UDP、DNS/UDP、QUIC/HTTP3；这些固定限制放在独立的“路径能力说明”中，不生成检测结果、不计入统计，也不会回退直连。SOCKS5 和普通套接字保留实际探测，运行中发现的不支持或失败继续显示。HTTP3 使用真正的 QUIC，不允许回退 HTTP2。尚未覆盖浏览器 WebRTC/DoH、断网保护、网络切换、长连接、LAN/.local、大包和 MTU。

结果分类默认折叠，点击标题或用键盘展开；标题保留整个分类的完成进度与需关注数量，筛选时另显示匹配数量。实时更新、搜索、筛选和失败重试保留展开状态，完整检查重置为折叠。普通套接字路径按本轮快照标明 TUN 开启或关闭；开启不等于已证明接管，仍需结合该流量的出口观测。预期出口的多个 IP/CIDR 用英文逗号分隔，命中任意一个即可；每次采样均须符合允许集合。

## 构建

需要 .NET 10 与 Go 1.26。先 `dotnet publish v2rayN/v2rayN.Desktop -c Release -r osx-arm64 --self-contained -o /tmp/v2rayn-check`，再在此目录执行 `CGO_ENABLED=0 go build -trimpath -o /tmp/v2rayn-check/proxy-check .`。运行时无需 Go。其他平台设置匹配的 GOOS/GOARCH，Windows 文件名为 `proxy-check.exe`。CI 的共享桌面构建流程自动打包组件；其他手动构建必须同样复制它。

组件不监听端口；每进程从 stdin 接收一个最长 64 KiB 的版本 1 JSON 请求，向 stdout 返回一个版本 1 JSON 结果。字段为 `version,kind,target,proxy,family,query,timeout`；kind 支持 `tcp,http,https,h3,stun,dns-udp,dns-tcp`，family 为 `4/6/空`，query 为 `A/AAAA/NXDOMAIN/WHOAMI`。结果包含 `state,elapsed,egress,protocol,evidence,error`，elapsed 单位为毫秒。凭证只经 stdin，不写入报告。

高级选项与基线存入应用配置目录的 `proxy-check-options.json` / `proxy-check-baseline.json`；导出报告为版本 1 JSON。运行配置变化会取消任务，取消或退出会终止探测子进程。

测试：本目录运行 `go test -race ./...`；共享层运行 `dotnet test --project v2rayN/ServiceLib.Tests`。UI 隔离审阅：`v2rayN.Desktop.Review <output> --headless --proxy-check-only --language=zh-Hans --theme=Light --width=1000`。
