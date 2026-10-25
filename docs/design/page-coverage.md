# v2rayN 页面设计覆盖审计

2026-10-25，覆盖当前 Avalonia `Views/` 下30个 AXAML 文件，以及C#动态构建的关于、应用工具两页。复用同一套配色、按钮和表格样式，不等于该页面已有完整的字段分组、交互与状态设计。

## 已有专门设计并完成客户端主要适配

| 页面族 | 工程视图 | 已有覆盖 |
|---|---|---|
| 主工作区、运行摘要、节点列表、详情 | `MainWindow`、`StatusBarView`、`ProfilesView`、`ServerWorkspaceView` | Primer 布局、中英文、浅深主题、窄窗、空态、应用反馈、日志抽屉 |
| 代理检查 | `ProxyCheckView` | 多协议/路径、通信与出口分开判断、能力项、取消/重试、观察端与基线 |

## 此前缺少完整独立设计的页面

| 页面族 | 工程视图 | 本轮统一设计范围 |
|---|---|---|
| 分组与订阅 | `SubSettingWindow`、`SubEditWindow` | 列表、编辑、更新范围、加载/失败、删除确认 |
| 代理策略 | `ClashProxiesView` | 单节点、手动/自动策略、候选、管理接口不可用 |
| 当前连接 | `ClashConnectionsView` | 筛选、刷新、代理链/进程详情、关闭范围确认 |
| 路由管理与编辑 | `RoutingSettingWindow`、`RoutingRuleSettingWindow`、`RoutingRuleDetailsWindow` | 规则集、顺序、条件表单、JSON、未保存退出 |
| DNS 设置 | `DNSSettingWindow` | 基础/TUN、上游、不同内核 JSON、能力提示、校验 |
| 配置模板 | `FullConfigTemplateWindow` | 内核分区、JSON 编辑、错误定位、作用范围 |
| 通用节点编辑 | `AddServerWindow` | 协议变体、认证、传输、TLS、验证与保存 |
| SSH 节点编辑 | `AddSshServerWindow` | 密码/私钥、端点信任、UDP/HTTP3、分阶段测试 |
| 自定义配置/出站 | `AddServer2Window` | JSON/文件入口、语法校验、作用范围 |
| 策略组/代理链编辑 | `AddGroupServerWindow` | 候选、顺序、自动/手动/串联语义、预览 |
| 节点选择器 | `ProfilesSelectWindow` | 搜索、多选、当前集合、确定/取消 |
| SSH 密钥部署 | `SshKeySetupWindow` | 身份核对、选择密钥、公钥预览、部署/验证/恢复 |
| 设置工作区 | `OptionSettingWindow` | 核心、应用、系统代理、TUN、内核管理及固定保存条 |
| 外观与语言 | `ThemeSettingView` | 主题、字体、语言、重启提示与字号预览 |
| 全局快捷键 | `GlobalHotkeySettingWindow` | Windows 编辑、冲突、重置、非支持平台状态 |
| 备份与恢复 | `BackupAndRestoreView` | 本地/WebDAV、凭证遮盖、校验、恢复覆盖确认 |
| 更新管理 | `CheckUpdateView` | 客户端/内核、版本、平台架构、下载及失败重试 |
| 完整日志页 | `MsgView` | 级别/文本、跟随、暂停、复制/导出/清空范围 |
| 确认弹窗 | `MessageBoxDialog` | 删除、未保存、SSH 身份固定等不同动作语义 |
| 分享 | `QrcodeView` | 二维码/链接、敏感内容遮盖与分享范围 |
| 系统权限 | `SudoPasswordInputView` | 用途、密码遮盖、取消、失败重试 |
| JSON 通用组件 | `JsonEditor` | 字体、校验、格式化、错误定位、长内容滚动 |

对应的统一设计在同一个 Primer 项目中补齐，使用虚构数据；设计任务不读取工程源码、真实配置或凭证。设计原型与真实客户端实施状态分别记录，不能将原型中的模拟操作视为已完成部署、更新或网络验证。

## 本轮设计交付与工程映射

上述25个视图均已有可评审的独立主体或共享组件设计。补齐动态页面后，统一原型为9个页面族、27个路由和13种节点协议变体；不是每个 AXAML 文件单独复制一套页面。次级页的主要状态仍为**设计已完成、原生页面待逐项实施**；代理策略绑定、关于窗口布局修复已实施。

| 工程视图 | 原型入口 |
|---|---|
| `SubSettingWindow` / `SubEditWindow` | [订阅列表](main-primer-opendesign/page-designs.html#subscriptions) / [编辑](main-primer-opendesign/page-designs.html#subscription-edit) |
| `ClashProxiesView` / `ClashConnectionsView` | [策略](main-primer-opendesign/page-designs.html#policies) / [连接与详情](main-primer-opendesign/page-designs.html#connections) |
| `RoutingSettingWindow` / `RoutingRuleSettingWindow` / `RoutingRuleDetailsWindow` | [规则集列表](main-primer-opendesign/page-designs.html#routes) / [规则集编辑](main-primer-opendesign/page-designs.html#ruleset-edit) / [单条规则](main-primer-opendesign/page-designs.html#rule-edit) |
| `DNSSettingWindow` / `FullConfigTemplateWindow` | [DNS](main-primer-opendesign/page-designs.html#dns) / [配置模板](main-primer-opendesign/page-designs.html#template) |
| `AddServerWindow` / `AddSshServerWindow` | [通用协议编辑](main-primer-opendesign/page-designs.html#node-editor/VLESS) / [SSH身份与扩展](main-primer-opendesign/page-designs.html#node-editor/SSH) |
| `AddServer2Window` | [完整配置](main-primer-opendesign/page-designs.html#custom-config) / [单出站](main-primer-opendesign/page-designs.html#custom-outbound) |
| `AddGroupServerWindow` / `ProfilesSelectWindow` | [策略组与候选选择器](main-primer-opendesign/page-designs.html#policy-edit) / [代理链](main-primer-opendesign/page-designs.html#chain-edit) |
| `SshKeySetupWindow` | [六步密钥部署向导](main-primer-opendesign/page-designs.html#ssh-deploy) |
| `OptionSettingWindow` | [五分区设置工作区](main-primer-opendesign/page-designs.html#settings-core) |
| `ThemeSettingView` / `GlobalHotkeySettingWindow` | [外观、语言和平台快捷键](main-primer-opendesign/page-designs.html#settings-app) |
| `BackupAndRestoreView` / `CheckUpdateView` | [备份恢复](main-primer-opendesign/page-designs.html#backup) / [更新管理](main-primer-opendesign/page-designs.html#updates) |
| `MsgView` | [完整日志](main-primer-opendesign/page-designs.html#logs) |
| `MessageBoxDialog` / `QrcodeView` / `SudoPasswordInputView` | [确认、分享与权限组件画廊](main-primer-opendesign/page-designs.html#gallery) |
| `JsonEditor` | [双内核JSON编辑组件](main-primer-opendesign/page-designs.html#template) |
| C# `CreateAboutWindow` | [关于与帮助](main-primer-opendesign/page-designs.html#about) |
| C# `BuildApplicationTools` | [应用工具与区域预设](main-primer-opendesign/page-designs.html#application-tools) |

详细字段、动作、空态/忙碌/失败、深链接与实施边界见[统一设计规范](main-primer-opendesign/secondary-pages-spec.md)和[覆盖清单](main-primer-opendesign/coverage.md)。

## 本轮代码复查修复

| 问题 | 修复 |
|---|---|
| 配置/凭证变更后仍保留旧“控制接口连接成功”与身份说明 | 立即失效可见状态，清理旧说明并提示重新验证；禁用观察端时清空说明 |
| 系统设置/能力核对被显示为“通信通过” | 单独显示“已核对（非通信）”，不使用流量成功标记 |
| 不完整的观察端 JSON 可以让服务标题为空白 | 使用明确的未导入/配置待完善提示 |
| 原生执行专项验收时，清单固定声称使用 Headless 渲染 | 根据实际启动模式记录渲染器 |
| 代理策略页的只读活动指示列未限定绑定方向 | 显式使用单向绑定，防止显示转换器反向影响活动状态，并增加实际控件回归 |
| 检查页与报告使用记忆的内核类型，停止后可能仍显示为正在使用该内核 | 根据实际进程状态生成同源内核说明，停止时明确显示未运行 |

新增四项回归测试。源码测试总计243项，237通过、6项因额外环境跳过、0失败。实际界面复核记录位于 `artifacts/primer-review-20261025/`。

两组原生界面专项（中文浅色1440、英文深色600）各39项通过，各留存16张截图；完整桌面流程69项通过，留存27张截图。构建成功，完整桌面编译仍有3项既有的 `ReactiveAttribute` 类型冲突警告。

设计原型独立复跑32+33=65项测试通过。修正后的12个代表场景已在浏览器重新渲染并复核，覆盖1440/760/600宽度、中英文与浅深主题。最终[视觉复核图](../../artifacts/secondary-primer-design-20261025/secondary-visual-review-final.jpg)与单页截图保存在 `artifacts/secondary-primer-design-20261025/`。原型测试、真实Avalonia流程检查和实际网络验收分别记录；本轮未进行公网网络验收。

## 后续复查：动态页面与关于窗口

发现仅扫描AXAML会漏掉C#构建的关于、应用工具两页，已补入同一套Primer原型和覆盖清单。增加[机器可读映射](native-view-map.json)与 `native-coverage-check.cjs`，检查30个AXAML和2个动态页面均有存在的设计目标；这只证明页面级设计映射，不替代字段、平台与功能验收。

关于窗口原先把说明文字和关闭按钮放在同一个固定高度StackPanel内，最小高度或大字号时关闭按钮可能超出可视区。已将关闭操作固定到底部，主体独立滚动，并在Review工程中验证420×240、24px字体下的滚动与关闭。

本次桌面构建成功（3项既有 `ReactiveAttribute` 警告）。macOS原生渲染器启动失败（RenderTimer -6661），因此此次两组专项使用 **Avalonia.Headless + Skia**，并非原生窗口会话验收；中文浅色1440、英文深色600各41项通过，各17张截图。记录位于 `artifacts/primer-review-followup-20261025/`。

原型32+35=67项通过，覆盖审计30+2项映射通过。新增两页在浏览器实际检查了600宽中文工具范围确认与英文深色关于弹窗，截图位于 `artifacts/secondary-primer-followup-20261025/`。本次未重新运行未变更的ServiceLib源码测试；前一轮243项结果保留在上文。
