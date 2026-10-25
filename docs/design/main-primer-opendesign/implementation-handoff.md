# v2rayN · Avalonia 工程交接

此文档只提出组件映射和分阶段建议，未读取真实源码、没有仓库级假设，不修改客户端业务逻辑。原型：[index.html](index.html)；视觉规则：[design-spec.md](design-spec.md)。所有交互为本地模拟。

## 组件到标准控件映射

| 概念组件 / HTML 区域 | 标准 Avalonia 实现建议 | 关键约束 |
|---|---|---|
| 应用壳 | Window + Grid，三行：56 / * / 32 | 使用平台标准窗口标题栏；原型顶栏是产品工具条，不是假窗口控件 |
| 侧边导航 | ListBox / ListBoxItem + DockPanel | 正常196px，窄窗64px；底部设置/帮助；Selection 与页面状态绑定 |
| 当前连接摘要 | Border + Grid + TextBlock | 接入已应用状态；速率独立更新，不导致整表刷新 |
| 节点工作区 | Grid，`* / 284`，可选 GridSplitter | 拖拽宽度属于实现增强；遵守最小主表宽度 |
| 密集节点表 | DataGrid（官方配套 Avalonia.Controls.DataGrid） | 显式列、虚拟化、冻结复选/名称列可作为工程增强；ScrollViewer 横滚 |
| 单行选择与批量勾选 | SelectedItem + 独立 CheckBox 绑定 | SelectedNode、CheckedNodeIds、RunningNodeId 三个独立源 |
| 搜索、分组、协议 | TextBox + ComboBox | 搜索可防抖；筛选不丢批量选择；显示选中总数 |
| 排序 | DataGridColumn.SortDirection / CollectionView | 延迟空值语义明确；排序稳定，保留选中ID |
| 详情 | Border + DockPanel + ScrollViewer | 底部固定操作，元数据可滚；目标与透明中继分开表示 |
| 窄窗详情抽屉 | 同一 Grid 上层 Border + TranslateTransform | 非模态；可继续操作列表；关闭回焦点；支持减少动画 |
| 添加/编辑/连接控制 | Window.ShowDialog + Grid 或 Overlay 自定义层 | 采用标准焦点管理；草稿与已应用配置分离 |
| 状态 pill | Border + TextBlock + PathIcon | 文字与图形共同表达状态，色彩只作辅助 |
| 更多菜单 | MenuFlyout / ContextMenu | 自动关闭、Esc、方向键；避免自造菜单键盘模型 |
| 提示与辅助说明 | ToolTip + 可见 TextBlock | 关键限制不能只存在于悬浮提示 |
| 日志抽屉 | 底部 Border + GridSplitter + 虚拟化 ListBox | 限制条数，用户关闭不停止后台记录；可选自动跟随 |
| 全局反馈 | 局部 TextBlock + NotificationManager | 应用结果进入可访问状态区域和日志，不能只有瞬时 Toast |
| 运行态诊断分组 | 分组 CollectionView / ItemsControl + DataGrid；窄窗用 ItemsControl 卡片 | 固定一个运行快照，按网络接管、TCP/TLS、DNS、UDP、QUIC/HTTP3、IPv4/IPv6分组，不遍历节点 |
| 诊断证据展开 | DataGrid 行详情 / Expander + TextBlock | 同源模型在表格和窄窗卡片呈现；展示来源、样本、时间、建议 |
| 进度 | ProgressBar + 已评估数 TextBlock | 分母固定；`executed=false`的取消行不能计作完成 |
| 主题/语言 | ThemeVariant + DynamicResource / 本地化资源 | 不在 View 中散布硬编码色值或语言条件 |

用 PathIcon 统一线性图标，18px 为主、1.6px 视觉笔画；原型中的 V 字母标是轻量品牌字标，不应覆盖客户端已有的正式品牌资产。

## 最小视图模型建议（概念，不对应任何既有类）

```text
WorkspaceState
  CurrentPage, Theme, Locale, DetailsOpen, LogsOpen
NodeInventory
  Nodes, Query, GroupFilter, ProtocolFilter, Sort
  SelectedNodeId, CheckedNodeIds
RuntimeSnapshot
  KernelState, RunningNodeId, AppliedProxyMode, AppliedTun, AppliedRoute
  DownloadRate, UploadRate, ListeningPorts
ApplyOperation
  OperationId, TargetNodeId, DraftConfiguration, State, Error
CheckSession
  SessionId, Demo, LiveValidated, RuntimeSnapshot, RuntimeFingerprint
  ObserverConfigSnapshot, ExpectedBaselineSnapshot, ItemIds
  StartedAt, CompletedAt, Status, CancelReason, Stale, Attempt, Results
DiagnosticItem
  ItemId, ProtocolGroup, Protocol, Target, Path, AddressFamily
  EvidenceScope(client/resolver/none), ObserverKind, CapabilityRequirement
ExpectedBaseline
  Key(Protocol + Target + Path), ExpectedAddress, Version
CheckResult
  ItemId, SessionId, Executed, CommunicationStatus, OverallOutcome
  ObservedExit, ExpectedExit, Compared, Match, ComparisonStatus
  EvidenceScope, EvidenceSource, ObserverEndpoint, ObservedAt
  Duration, Samples, Attempt, History, ErrorCategory
```

ID 和快照是数据关联依据，不能用翻译后的节点名称做状态键。节点地址和认证字段只能由真实工程的既有安全模型提供；交接不涉及凭证迁移、保存和解密。主机密钥状态应使用真实证据，不能把本原型“已固定 · 演示”作为默认可信值。

## 状态与提交规则

### 应用节点 / 配置

```text
Idle → Applying(operationId, targetId, draft)
Applying → Succeeded : 发布新的 RuntimeSnapshot
Applying → Failed    : 保留旧 RuntimeSnapshot + 可重试错误
```

- UI 选择不触发配置写入；应用按钮是唯一显式提交入口。
- 应用期间禁止同类操作重入，结果按 operationId 核对，迟到结果不能覆盖新状态。
- 真实应用涉及文件与内核时，由服务层提供事务/回滚能力；UI 仅展示事实。只有服务确认成功，运行标识、系统代理、TUN、路由才能改变。
- 内核停止时保持可查看节点，但应用动作提示启动；运行标识必须撤下，速率归零或未知。
- 失败区分权限不足、内核退出、配置校验、连接超时；保持用户草稿，不清空原配置。日志必须脱敏。
- 原型的模拟失败是评审工具，不是生产设置项。实际产品不提供“状态预览”下拉。
- 启动应用节点、编辑当前运行节点、提交连接控制或重启内核前，调用诊断取消边界。成功应用后推进配置版本。原型即使应用失败也保守中断正在检查的会话；旧报告仍归属原快照。

### 检查任务

```text
NotStarted → Checking(snapshot) → Completed | PartialFailure | Cancelled
Checking → CancelRequested(user/configuration-change) → Cancelled
Completed / PartialFailure → RetryFailed(same snapshot) → Checking
Any report + runtime fingerprint change → Stale (requires full new session)
```

- **检查对象是开始时当前运行的一个配置快照**，包括节点ID/地址/名称、内核、代理模式、TUN、路由和版本。与节点列表选中、多选、订阅、地区完全独立；主界面“测试选中”是另一项功能。
- 原型用550ms定时器模拟逐项评估；工程用 CancellationToken + SessionId/配置版本拒绝迟到结果。切换节点、重启内核或修改接管配置都须取消本次诊断。取消反馈不能被紧接着的“节点已应用”吞掉，报告持续显示原归属和过期原因。
- 固定14项、六个协议分组。工程检查适配器按能力边界执行：系统代理、TUN、SOCKS5 TCP、HTTP CONNECT TLS、系统HTTPS、受控DNS UDP/TCP、递归DNS、SOCKS5/native STUN、SOCKS5/native HTTP3、IPv4/IPv6。不能把能力项当网络连通性验证。
- HTTPS/HTTP、STUN、HTTP3、受控DNS可以提供对应请求的出口证据。证据必须有协议、目标、路径、观察端、采样ID/时间及来源。受控DNS须使用唯一标记关联服务端观测；系统递归DNS只标注解析器源地址。没有服务端观测的普通TCP或设置项不适用/证据不足，不能引用节点缓存补齐。
- CommunicationStatus 与 ComparisonStatus、OverallOutcome分开。综合结论支持 Passed / Failed / Unsupported / Indeterminate / Skipped / Cancelled。通信通过但出口不符是综合失败；无预期已观测是 Indeterminate + NoExpectation，不是匹配。
- IPv6不支持/跳过要写明未覆盖和0样本，不隐藏该行，不判通过。支持性预检、跳过判断可记为已评估；未执行即取消使用 `Executed=false`，不计进度。取消后不能通过给所有行写状态就显示100%。
- 基线按 `Protocol + Target + Path` 唯一定位，地址族单独校验。原型允许RFC 5737 IPv4及`2001:db8::/32` IPv6虚构样例；正式工程不能照搬文档地址限制。目标改变不得静默套用旧目标基线。普通TCP、系统设置和递归解析器不提供客户端出口基线。
- 观察端分为HTTPS、STUN、受控DNS、HTTP3。原型只校验演示域名与端口，不联网。修改基线/观察端清空旧会话；工程可保留带版本的历史，但不得重解释旧结论。
- “重新完整检查”创建新快照、覆盖当前报告；“重试失败项”固定原快照，仅重排综合Failed项目，其他结果保持，历史尝试保留。不重试Unsupported/Skipped/Indeterminate；过期快照禁止重试。若取消会话仍含未执行项，重试失败不能使整份报告变成完成。
- 导出 schemaVersion=2：包含会话快照与过期状态、观察端/基线、14项完整定义、执行标志、比较、采样、历史和进度；`demo=true / liveValidated=false`。不能把旧报告归到导出时新运行的节点。

## 分阶段实施

| 阶段 | 交付 | 验收门槛 |
|---|---|---|
| 1 · 视图壳与资源 | Token 资源字典、双主题、系统字体、导航、响应布局、双语 | 1440/760/600宽度，主题和语言切换无截断；无业务接入 |
| 2 · 节点浏览 | DataGrid、筛选排序、勾选、详情、空状态 | 三种选择状态互不干扰；大列表虚拟化；地址与路径完整可读 |
| 3 · 显式应用 | 对接已有服务接口、操作状态、失败回滚与诊断 | 成功后更新运行态；失败、超时、取消、重启不伪造成功 |
| 4 · 运行态诊断 | 单一运行快照、六组14项、协议观察端、目标/协议/路径基线、证据、失败重试、取消和导出 | 无预期不判匹配；解析器不当客户端；IPv6明确未覆盖；配置变化取消；未执行不涨进度 |
| 5 · 三平台验收 | Windows/macOS/Linux、DPI、键盘、屏幕阅读器 | OS权限流程、TUN、代理恢复、窗口最小化/恢复和日志压力测试 |

每阶段均可先使用固定 Fixture，真实接入需由工程明确授权与接口；此项目不承担客户端改造或打包安装。

## 手工验收脚本

1. 默认页：运行节点为上海，被选中为杭州；选东京后，上海仍运行。勾选两行，批量计数变化，详情选择独立。
2. 搜索 `example.net`，叠加 SSH / 香港分组；升降序延迟；清空筛选；无匹配时有清除入口。
3. 选香港节点应用：先显示应用中，完成后运行标识移动。用顶部失败预览再应用：失败反馈出现，原节点不变。
4. 连接控制调整 TUN/路由：等待成功后摘要更新。勾模拟失败再应用：摘要不变。内核停止预览中启动内核后恢复。
5. 日志打开、关闭、导出；确认只有演示信息。添加允许域名成功，真实地址被拒；移除不删除运行节点。
6. 检查页固定当前运行节点；选中其他节点不改变快照。核对六组14个项目、六个综合结论；检查系统路径/native出口不符、HTTP3超时、DNS TCP/native H3未配置预期、递归DNS和IPv6未覆盖。
7. 前几项后取消：未执行项无样本且进度低于100%。重试失败项只改变失败行并保留历史；修改基线/多协议观察端使旧结果失效。地址校验包含文档IPv4/IPv6、HTTPS URL和STUN/DNS端口。
8. 检查进行中返回主界面应用另一个节点：立即取消本次检查，完成后旧报告保持原快照与配置变化提示；导出也保留原归属。应用失败仍中断本会话；迟到回调不能追加结果。
9. 遍历八种预览、中文/英文、浅/深；在1440×900、760×900、600×900检查布局。诊断1440为表格、760/600为完整字段卡片；主界面表格、详情、主题与日志维持原设计。
10. Tab/Shift+Tab、Enter/Space、Esc、Ctrl/⌘+K；检查证据展开、原生 dialog 焦点恢复，减少动态偏好下没有动画。

回归入口 `tests/prototype-check.cjs`：原有16项测试意图保留，旧检查模型断言迁移到协议项目ID；新增16项，合计32项。它验证状态、生成内容、数据边界和断点规则，不是浏览器布局引擎。视觉复查夹具 `tests/diagnostic-visual-review.html` 以本项目iframe提供三种真实布局宽度、两种主题和两种语言的组合，无真实服务访问。

## 原型限制及接入注意

- 没有真实测速、流量、地理定位、主机密钥验证、文件导入、DNS 或网络请求。
- 订阅页更新只模拟反馈；代理链是路径查看；路由页修改显示模式。没有完整路由规则编辑器和链构建器，避免原型越界为业务实现。
- 浏览器存储只保存语言/主题；节点、基线和任务刷新重置。正式产品应按现有配置模型持久化。
- 当前32项原型回归通过；1440/760/600宽度、浅深主题、中英文的12组首屏已用项目内iframe矩阵渲染核对。测试替身没有浏览器布局能力，不能代替逐交互的原生控件验收；不宣称三平台运行验证。真实字体度量、DPI与辅助技术需要工程验收。
