# 原生实现与验证记录

日期：2026-10-28。

> UI 还原修订：最初发布的 preview.18 使用了通用表单和按钮布局，未达到此目录原稿的视觉要求。后续 `feature/tun-design-fidelity` 已按原稿重做布局；最新记录见 [UI 还原复查](fidelity-review.md)。下方原始测试计数保留为首次实现的历史记录。

[交互原型](index.html) 由 Local Codex 生成，完整保留其视觉规范、交接说明和离线检查脚本。产品界面使用原生 Avalonia 控件实现，入口为“设置 → TUN 模式”。[使用说明](../../tun-settings.zh-Hans.md) 描述实际配置行为；页面内还提供逐字段的中英文帮助、搜索与 Markdown 导出。

## 实际接入

九类能力均进入配置读写、生成和平台校验：DNS 模式与虚拟入口、UDP NAT、系统路由范围与规则集、Linux 自动重定向及编号、网卡/UID/MAC 筛选、网络命名空间、MPTCP 绕过、TCP 回接、设备名与多地址。原生字段目录有 41 项，包含原有基础设置与拆开的包含/排除列表。

- 新增字段留空时保留既有有效配置。保存不会打开主界面 TUN 开关。
- 有已选节点时，先按未来启用 TUN 的配置预检，再保存；运行中的 TUN 通过现有配置比较流程应用，未改变时保留连接。
- 应用失败恢复上一份设置并尝试恢复运行配置，报告结果，保留草稿。
- 不适用的平台字段默认隐藏，可只读查看；切换开关不会丢失已保存的其他平台值。
- 完整 TUN 模板拥有配置时，本页只读并提供跳转。其他内核负责 TUN 时，sing-box 专用值保留但不应用。
- Avalonia 的旧通用设置不再写入独立 TUN 配置。Windows WPF 本轮保留旧界面，共享模型保存新增字段。

原型中的演示平台、示例内核版本和模拟保存入口未带入产品。原型的 `icmp_policy` / `compatibility_preflight` 是交接意图；产品沿用既有 `IcmpRouting` / `EnableLegacyProtect`。实际序列化键、默认值及范围以原生代码和已核对的 sing-box schema 为准，不采用原型自行设定的限制。例如实际 UDP 时长支持 Go 时长组合，NFQueue 的 0 仍保留内核默认语义；自定义 DNS 不额外强制原型的子网限制。

适配基线为 sing-box 1.14+。本机配置检查使用 1.14.3-ssh-udp.1。旧内核是否接受某组显式新增字段由保存前预检决定，不把主代理内核的版本当成 TUN 前置内核的版本。

## 验证结果

| 验证 | 结果与边界 |
|---|---|
| 共享服务回归 | 总计 430，421 通过，9 跳过，0 失败；包含 25 个 TUN 用例及三份真实内核配置检查。跳过项为另需环境变量、Docker 或 SSH 测试依赖的集成检查。 |
| Release 构建 | ServiceLib、Avalonia 与界面验证程序构建通过；完整 Avalonia 重编译仍有原有的 3 个 ReactiveAttribute 类型冲突警告。 |
| 原生界面 | 五组语言/主题/宽度，每组 10 张截图、20 项状态与布局检查；全程隔离配置，不启动真实 TUN。 |
| 界面矩阵 | 中文浅色 1440、英文浅色 1440、中文深色 1000、英文深色 760、中文浅色 600。 |
| 独立配置冒烟 | 直接接管 / 前置 SOCKS × gvisor / system / mixed，共六份配置通过 sing-box check；本轮仅运行 --check-only。 |
| 设计原型 | 配套 Node 脚本 46 项离线检查通过；浏览器确认 DNS 修改、模拟保存、清除脏状态且 TUN 仍关闭。模型检查不代表实际接管验证。 |

原生截图及各组 `manifest.json` 位于工作区 `artifacts/tun-settings-20261028/`，不作为安装包。本轮按用户选择跳过需要管理员权限的 Mac 真实 TUN 启停；Windows / Linux 为配置生成与平台策略验证，未执行对应系统的接管测试。未安装或替换正在运行的客户端。

## 复查入口

~~~sh
# 共享服务测试；设置本机实际内核路径时启用配置检查及 Bonjour 回归
TUN_TEST_SINGBOX=/path/to/sing-box MDNS_TEST_SINGBOX=/path/to/sing-box \
  dotnet run --project v2rayN/ServiceLib.Tests -c Release -- --progress off

# 原生页面隔离评审；语言、主题、宽度可替换
dotnet run --project v2rayN/v2rayN.Desktop.Review -c Release -- \
  /tmp/tun-review --headless --tun-settings-only --language=zh-Hans --theme=Light --width=1440

# 配置检查不需要管理员权限，不创建 TUN
python3 packaging/test-tun-macos.py --check-only --output /tmp/tun-check

# 原型检查不读取真实配置
node docs/design/tun-settings-opendesign/tests/prototype-check.cjs
~~~
