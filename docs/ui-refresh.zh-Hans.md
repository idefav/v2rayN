# Avalonia 全界面换新实现与验收

2026-10-12：当前代理按单节点／策略组重组，服务器移除重复分组侧栏，新增独立出口 IP／地区检测、国旗与缓存状态。见 [本轮设计及验收](../../docs/design/v2rayn-exit-info-20261011/README.md) 和 [视觉对照](../../docs/design/v2rayn-exit-info-20261011/comparison.html)。ServiceLib 156 项通过，三 RID Release 发布成功；原生平台与外部服务尚未验证的部分在记录中单独列出。

本次追加服务器操作列固定：横向滚动时详情、编辑始终可见。固定单元格复用原 DataGrid 的已实现行及选择，字号变化时测量按钮宽度；深色选中背景通过实际像素对比对齐。默认全页 84 张截图、89 项检查，五组矩阵各 19 张、58 项，Linux X11 原生 19 张、57 项均通过。见 [固定列验收记录](../../docs/design/v2rayn-ui-polish-acceptance-20261011/fixed-actions-full/README.md) 与 [固定列标准安装包](../../artifacts/v2rayn-ui-fixed-actions-20261011/package/README.md)。未启动或替换用户客户端，未修改或清空宿主系统代理。以下为此前主操作路径和全界面换新记录。

最新一轮修复节点、分组与订阅主操作路径，记录见 [workflow-v2 验收](../../docs/design/v2rayn-ui-polish-acceptance-20261011/workflow-v2/README.md) 与 [54 项设计／实际对照](../../docs/design/v2rayn-ui-polish-acceptance-20261011/workflow-v2/comparison.html)。新版首页与订阅稿单独存放，原稿保留。分组筛选始终可见；详情和编辑进入行操作／右键；顶部“更多”移除，表格设置只保留列配置。订阅页区分本地分组与订阅，提供全部／单条／通过代理更新，嵌入页面没有“关闭”。URL 只读列改为单向绑定，避免表格复用清空内存地址；分组同步及过期刷新也已修正。

本轮全页 80 张截图、80 个检查通过；七组主路径矩阵和 Linux X11 原生每组 15 张、49 个检查通过，总计 200 张截图。英文大字号截断经视觉复拍修正，列与筛选框按文字测量，字号参与窄窗适配。ServiceLib 150 项通过、零跳过。最新 [标准 macOS arm64 包](../../artifacts/v2rayn-ui-workflow-20261011/package/README.md) 包含 osx-arm64、linux-x64、win-x64 Release 发布验证；未安装或启动标准客户端，未操作宿主系统代理。

本轮 macOS 原生复测在 RenderTimer 初始化阶段遇到 -6661，未计为通过。前轮原生证据与下文历史记录保留，不能替代本轮验收；平台窗口、Dock／托盘、Windows 原生、Wayland 和真实外部网络仍有待验收项。

第二轮采用 [54 项新版设计稿](../../docs/design/v2rayn-ui-polish-20261011/index.html)。原审计发现的 24 项明显差异、25 项局部差异、5 项平台／设计冲突已逐项处理，最新 [整改与视觉复核](../../docs/design/v2rayn-ui-polish-acceptance-20261011/final-alignment.md) 和 [并排对照](../../docs/design/v2rayn-ui-polish-acceptance-20261011/final-alignment/comparison.html) 位于工作区。原未通过记录与旧稿保留，平台修订单独存放。默认浅色中文的实际控件已经逐项复核，系统平台待验收项目另列，不以截图数量或编译成功代替最终验收。

## 实现范围

Avalonia 原有 28 个视图接入统一界面，新增服务器工作区。Windows WPF 的视图、样式和布局文件未修改。

- `WorkspaceStyles.axaml` 集中定义浅色／深色配色、蓝色强调色、字号、圆角和表格样式。用户字号同时应用于表单、按钮、菜单、标题、日志、JSON 和表格；保持原有 Semi 主题、字体和语言设置。
- 主窗口使用扩展客户区和 56 高的融合顶部栏，保留原生窗口按钮和系统行为；不支持扩展装饰的 Linux 环境保留系统标题栏。默认 1440×900、最小 600×480。版本移至侧栏底部及“帮助 → 关于”。
- 首页只常驻运行摘要、节点工具栏和表格。连接控制置于窗口内浮层，详情默认关闭，从行操作／右键打开 360 宽抽屉；详情与诊断日志互斥。低于 1000 使用图标导航，分组选择器始终可见；低于 800 工具栏换行，字号也参与表格精简适配。旧列、排序和尺寸偏好继续读取；表格设置提供列选择、精简列和自动列宽，全组测试放在测试范围下拉，多选切换为批量操作。普通测试按钮只测试选中节点。
- 运行状态读取实际进程，流量读取现有统计事件。没有进程时显示“内核未运行”，没有检测数据时显示“—”或“未测试”。配置中的内核类型不会被当作运行成功。
- 设置采用二级导航，复用业务 ViewModel 与命令。Core、应用、系统代理、TUN 和核心分配共用同一份选项草稿，DNS、模板、备份、Windows 快捷键分别保留草稿。离开时可以保存、放弃或继续编辑；校验失败会保留页面。外观延续即时生效方式。
- 管理页可嵌入主窗口，也可以由原窗口调用。保存后的订阅刷新、路由菜单刷新和内核重载通过共享流程完成。
- 服务器、当前代理、连接和日志使用原实例。日志完整页与抽屉复用同一控件，移动前先卸载；抽屉精简工具栏，完整页保留过滤、复制和自动刷新等操作。
- 节点、订阅、策略组、代理链、路由和设置表单保留原命名控件、绑定及高级字段，采用轻分组和自适应列数；协议高级字段可收起。长表单滚动，保存／取消固定在底部。DNS 与模板 JSON 编辑器在窄窗口上下排列，权限和二维码弹窗保留可见操作区。
- SSH 保留密码／密钥认证、节点公钥固定、重新信任、登录和代理访问检测、取消、失败及免密配置流程。网络及 SSH 配置格式未改变。

## 可复现检查

使用 .NET SDK 10.0.100。当前主机 SDK 位于 `/private/tmp/v2rayn-package-sdk/dotnet`；其他主机使用 PATH 中的 `dotnet` 即可。

```sh
cd v2rayN
dotnet build v2rayN.Desktop.Review -c Release -p:UsedAvaloniaProducts=
dotnet v2rayN.Desktop.Review/bin/Release/net10.0/v2rayN.Desktop.Review.dll /tmp/ui-review --headless
dotnet v2rayN.Desktop.Review/bin/Release/net10.0/v2rayN.Desktop.Review.dll /tmp/ui-review-dark --headless --screens-only --theme=Dark
dotnet v2rayN.Desktop.Review/bin/Release/net10.0/v2rayN.Desktop.Review.dll /tmp/ui-review-small --headless --screens-only --width=600
dotnet v2rayN.Desktop.Review/bin/Release/net10.0/v2rayN.Desktop.Review.dll /tmp/ui-review-font --headless --screens-only --font=24 --scale=2
dotnet test --project ServiceLib.Tests -c Release --results-directory /tmp/test-results -- --report-trx
```

UI 验收程序禁用内核启动，并在初始化前设置进程级系统代理变更保护；启动、操作和退出都不能设置或清空代理。它只重置自己的夹具数据库，不可写时直接终止，不退回用户配置目录。不要将验收程序部署到生产客户端的配置目录。Headless 检查使用 [Avalonia 官方支持的 Skia 渲染方式](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform)，输出实际控件像素；它不验证平台窗口装饰、Dock、托盘或系统代理。

完整 SSH 集成检查需要设置 `SSH_TEST_PYTHON`（安装 paramiko 的 Python）、`SSH_TEST_SINGBOX` 和 `SSH_SETUP_DOCKER_IMAGE`。此次使用本地 sing-box 和当前源码构建的隔离 OpenSSH 容器，150 项全部通过、零跳过（原有 149 项及新增路由并发初始化测试）。测试报告保存在 `artifacts/v2rayn-ui-alignment-20261011/tests/`。

Linux 原生窗口检查使用 `v2rayN.Desktop.Review/Dockerfile` 提供 Xvfb，并挂载对应架构的自包含 Review 发布目录：

```sh
dotnet publish v2rayN.Desktop.Review -c Release -r linux-arm64 --self-contained -p:UsedAvaloniaProducts= -o /tmp/linux-review
docker build -t v2rayn-ui-native-review -f v2rayN.Desktop.Review/Dockerfile v2rayN.Desktop.Review
docker run --rm --network none -v /tmp/linux-review:/app -v /tmp/ui-native:/review v2rayn-ui-native-review /review
```

`.github/workflows/ui-refresh-checks.yml` 提供 Ubuntu、Windows、macOS 的构建、控件截图及 ServiceLib 检查。工作流已添加到本地源码，尚未提交或执行远端 CI。

## 上一轮交付和剩余验收记录

上一轮标准包及校验说明位于 [历史工作区 artifacts](../../artifacts/v2rayn-ui-alignment-20261011/package/README.md)。桌面程序执行 osx-arm64、linux-x64、win-x64 Release 自包含发布；AmazTool 使用标准裁剪参数。标准 macOS 应用读取原用户配置，内核及规则数据沿用此前标准包，验收夹具不进入安装包。未自动安装或启动标准客户端。

上一轮完整回归为 76 张实际控件截图、57 个检查通过；十组矩阵每组 64 张、30 个检查；macOS 与 Linux X11 原生后端各 64 张、30 个检查。截图包含非空规则／代理链、有内容 JSON、TLS／REALITY／WebSocket、SSH 公钥变化和失败／取消状态。状态夹具不证明生产网络结果。

英文大字号的表头和 SSH 标签已修正；列宽随字号测量，长标签换行。默认规则初始化通过进程内异步互斥避免启动竞争造成重复插入；显式再次导入的语义不变，并发初始化测试验证仅产生一份默认规则。没有清理用户已有同名规则。

Windows 原生、Wayland、系统窗口拖动、按钮／全屏、托盘／Dock、关闭隐藏、全局快捷键、生产系统代理／TUN 权限以及真实订阅、WebDAV、更新与全部协议互通仍待人工验收。macOS 鼠标工具连接超时未计为通过；Xvfb 没有真实窗口管理器。当前证据证明控件布局和构建兼容，不能宣称三平台全部最终验收完成。
