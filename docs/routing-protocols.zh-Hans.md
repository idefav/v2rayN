# 按核心配置路由协议

Avalonia 和 WPF 的路由规则详情列出全部协议，按当前节点的主核心启用可识别的选项。SSH 节点使用 sing-box。更改规则的出站目标不会改变负责识别流量的核心。

| 核心 | 可选择的协议 |
| --- | --- |
| sing-box | http、tls、quic、bittorrent、dns、stun、dtls、ssh、rdp、ntp |
| Xray / V2Fly | http、tls、quic、bittorrent |

协议识别需要开启流量嗅探。窗口会提示当前核心和嗅探状态，文档链接也随核心切换。

使用 Xray 时，`dns`、`stun`、`dtls`、`ssh`、`rdp`、`ntp` 灰显。已有规则里勾选的协议会保留勾选，仍可编辑其他字段并保存。切回 sing-box 后可以再次编辑这六项。导入的未知协议在两个核心下都灰显并保留原值。

切换节点或后台重新应用配置时，v2rayN 根据所选节点的核心筛选流量规则。只要规则中有一个协议不受支持，整条规则就会跳过，流量继续匹配下一条规则。原有协议、启用状态和规则顺序都会保留。规则详情会显示当前核心跳过该规则的原因，允许直接保存。

## STUN 分流

例如，可以一直启用 `STUN PROXY`。选择 sing-box 节点时，它按原顺序匹配 STUN 并转到 `proxy`。切到 Xray 或 V2Fly 时这条规则自动跳过，切回 sing-box 时恢复。`tls + stun` 这样的混合规则同样整条跳过，包括它引用的出站和派生的 DNS 规则。使用 Xray 节点加 sing-box 前置 TUN 时，两份配置都跟随 Xray 的协议能力。

仅用于 DNS 的规则按原有 DNS 逻辑处理。完整自定义配置和模板使用自身的路由配置。规则集格式损坏和核心配置检查失败仍会报告错误。

### 配置规则

使用 sing-box 时，可添加独立规则：协议选择 `stun`，出站选择 `proxy`，放在国内 IP 直连规则之前。需要阻断 STUN 时选择 `block`。不要附加必须命中的域名条件，也不要假定所有 STUN 都使用 3478 端口。

该规则只作用于已被核心接管并识别为 STUN 的流量。普通 TCP、其他 UDP、IPv6 接管以及代理断开时的系统防漏需要分别验证。DNS 接管等更高优先级的内置规则仍然先执行；增加协议选项本身不会改变已有分流规则。

## 验证

```sh
dotnet test --project v2rayN/ServiceLib.Tests -c Release -- --treenode-filter '/*/*/RoutingProtocolTests/*'
dotnet build v2rayN/v2rayN.Desktop.Review -c Release
dotnet v2rayN/v2rayN.Desktop.Review/bin/Release/net10.0/v2rayN.Desktop.Review.dll artifacts/routing-protocols --headless --routing-protocols-only
dotnet build v2rayN/v2rayN/v2rayN.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true -p:SelfContained=false -p:PublishSingleFile=false
```

回归测试覆盖核心往返切换、混合协议、未知协议、仅 DNS 规则、双核心 TUN 和损坏的规则集。Avalonia 在隔离窗口中检查协议选项、选中状态及跳过规则的提示。

协议能力依据：[sing-box 协议嗅探](https://sing-box.sagernet.org/configuration/route/sniff/)、[Xray 路由](https://xtls.github.io/config/routing.html#ruleobject)、[V2Fly 嗅探实现](https://github.com/v2fly/v2ray-core/blob/master/app/dispatcher/sniffer.go)。
