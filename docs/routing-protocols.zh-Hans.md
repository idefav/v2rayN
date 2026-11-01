# 按核心配置路由协议

Avalonia 和 WPF 的路由规则详情根据当前活动节点的核心展示协议选项。SSH 节点使用 sing-box。更改规则的出站目标不会改变负责识别流量的核心。

| 核心 | 可选择的协议 |
| --- | --- |
| sing-box | http、tls、quic、bittorrent、dns、stun、dtls、ssh、rdp、ntp |
| Xray / V2Fly | http、tls、quic、bittorrent |

协议识别需要开启流量嗅探。窗口会提示当前核心和嗅探状态，文档链接也随核心切换。

历史或导入规则包含当前核心不支持的协议时，编辑器保留并显示原选项。可以取消勾选、停用规则，或切换至支持它的核心。启用的流量路由规则如果仍包含不支持的协议，保存和生成配置都会明确报错；不会删除协议条件或自动扩大匹配范围。配置生成失败发生在停止当前核心之前。停用规则和仅用于 DNS 的规则不参与流量路由协议校验；完整自定义配置模板由模板负责配置。

## STUN 分流

使用 sing-box 时，可添加独立规则：协议选择 `stun`，出站选择 `proxy`，放在国内 IP 直连规则之前。需要阻断 STUN 时选择 `block`。不要附加必须命中的域名条件，也不要假定所有 STUN 都使用 3478 端口。

该规则只作用于已被核心接管并识别为 STUN 的流量。普通 TCP、其他 UDP、IPv6 接管以及代理断开时的系统防漏需要分别验证。DNS 接管等更高优先级的内置规则仍然先执行；增加协议选项本身不会改变已有分流规则。

## 验证

```sh
dotnet test --project v2rayN/ServiceLib.Tests -c Release -- --treenode-filter '/*/*/RoutingProtocolTests/*'
dotnet build v2rayN/v2rayN.Desktop.Review -c Release
dotnet v2rayN/v2rayN.Desktop.Review/bin/Release/net10.0/v2rayN.Desktop.Review.dll artifacts/routing-protocols --headless --routing-protocols-only
dotnet build v2rayN/v2rayN/v2rayN.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true -p:SelfContained=false -p:PublishSingleFile=false
```

回归测试覆盖新增协议的保存、持久化和配置生成，切换核心时的拒绝行为，历史协议保留，停用规则，以及无效保存不修改原规则。Avalonia 检查运行隔离窗口，验证实际选项、选择状态、换行及取消行为；不启动用户代理。

协议能力依据：[sing-box 协议嗅探](https://sing-box.sagernet.org/configuration/route/sniff/)、[Xray 路由](https://xtls.github.io/config/routing.html#ruleobject)、[V2Fly 嗅探实现](https://github.com/v2fly/v2ray-core/blob/master/app/dispatcher/sniffer.go)。
