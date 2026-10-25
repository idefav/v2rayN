# 次级页面覆盖清单

入口：[page-designs.html](page-designs.html)。机器可读清单：[coverage.json](coverage.json)。设计状态均指可交互演示，不指真实客户端实现。

共9个页面族、27个目录路由、13种通用节点协议变体。原主界面、代理检查与原32项测试保持不变。

## 分组与订阅

| 页面 | 状态与入口 | 可评审内容与动作 |
|---|---|---|
| 列表与更新 | [交互演示](page-designs.html#subscriptions) | 范围确认；直连/代理更新；启用/频率；失败重试；删除确认 |
| 添加/编辑 | [交互演示](page-designs.html#subscription-edit) | URL校验；更新间隔；过滤；前后置策略；保存/放弃 |

## 代理策略、代理链与连接

| 页面 | 状态与入口 | 可评审内容与动作 |
|---|---|---|
| 策略概览 | [交互演示](page-designs.html#policies) | 单节点/API不可用；手动切换；自动延迟；故障转移 |
| 策略组编辑 | [交互演示](page-designs.html#policy-edit) | 类型语义；候选选择器；顺序；保存/放弃 |
| 代理链编辑 | [交互演示](page-designs.html#chain-edit) | 串联语义；选择器；上移/下移；路径预览 |
| 连接列表 | [交互演示](page-designs.html#connections) | 目标/类型/代理链/进程；筛选/自动刷新；详情抽屉；单条/全部关闭确认 |

## 路由与规则

| 页面 | 状态与入口 | 可评审内容与动作 |
|---|---|---|
| 规则集列表 | [交互演示](page-designs.html#routes) | 启用/默认；顺序；添加/演示导入；删除确认 |
| 规则集编辑 | [交互演示](page-designs.html#ruleset-edit) | 可视化/JSON；顺序/出站；保存/放弃/继续；规则导航 |
| 单条规则编辑 | [交互演示](page-designs.html#rule-edit) | 域名/IP/端口/进程；AND语义；出站；语法/字段校验 |

## DNS 与配置模板

| 页面 | 状态与入口 | 可评审内容与动作 |
|---|---|---|
| DNS设置 | [交互演示](page-designs.html#dns) | 基础/直连/远程/TUN；上游与目标分离；Xray/sing-box；JSON默认值覆盖确认 |
| 配置模板 | [交互演示](page-designs.html#template) | 完整模板范围；双内核JSON；格式化/语法位置；未保存保护 |

## 通用节点与自定义配置

| 页面 | 状态与入口 | 可评审内容与动作 |
|---|---|---|
| 协议字段编辑 | [交互演示](page-designs.html#node-editor/SSH) | 13种协议；认证/传输/TLS变体；SSH人工固定/信任失效；分阶段测试/取消 |
| 完整自定义配置 | [交互演示](page-designs.html#custom-config) | 完整对象范围；Xray/sing-box；JSON校验/默认值确认 |
| 单出站对象 | [交互演示](page-designs.html#custom-outbound) | 单对象范围；Xray/sing-box；JSON校验/保存 |

## SSH 密钥部署

| 页面 | 状态与入口 | 可评审内容与动作 |
|---|---|---|
| 六步向导 | [交互演示](page-designs.html#ssh-deploy) | 单一SSH目标；指纹人工核对；密钥引用；公钥授权预览；部署/验证模拟；失败恢复 |

## 设置与维护

| 页面 | 状态与入口 | 可评审内容与动作 |
|---|---|---|
| 核心设置 | [交互演示](page-designs.html#settings-core) | 内核/端口/API；能力依赖；固定保存条 |
| 应用设置 | [交互演示](page-designs.html#settings-app) | 外观/字号；语言需重启说明；Windows快捷键/macOS/Linux不可用 |
| 系统代理设置 | [交互演示](page-designs.html#settings-proxy) | 接管方式/类型；绕过/PAC；不即时应用系统 |
| TUN设置 | [交互演示](page-designs.html#settings-tun) | 栈/MTU/DNS/路由；权限依赖；sudo预览 |
| 内核管理 | [交互演示](page-designs.html#settings-kernel) | 接口/遮盖凭证；超时/能力；重启影响确认 |
| 备份恢复 | [交互演示](page-designs.html#backup) | 本地/WebDAV；凭证遮盖；加载→校验；覆盖范围/排除凭证；失败重试 |
| 更新工作区 | [交互演示](page-designs.html#updates) | 客户端/内核分离；当前/可用版本；平台架构；下载进度/失败重试；不提供安装 |
| 应用工具与区域预设 | [交互演示](page-designs.html#application-tools) | 目录预览；统计清理范围；Windows提权/UWP能力限制；区域预设覆盖确认；失败重试 |

## 完整日志

| 页面 | 状态与入口 | 可评审内容与动作 |
|---|---|---|
| 完整日志页 | [交互演示](page-designs.html#logs) | 级别/文本筛选；暂停/跟随；复制/清空/导出预览；错误详情 |

## 目录与通用组件

| 页面 | 状态与入口 | 可评审内容与动作 |
|---|---|---|
| 页面设计目录 | [交互演示](page-designs.html#catalog) | 全部页面入口；可深链接；页面族说明 |
| 交互组件画廊 | [交互演示](page-designs.html#gallery) | 删除确认；未保存三选项；身份固定；分享范围/二维码；sudo不存密码；Toast/错误/进度/空态/焦点 |
| 关于与帮助 | [交互演示](page-designs.html#about) | 构建/平台/运行时；关于弹窗；内容独立滚动与固定关闭；更新入口 |

## 子页与变体

| 对象 | 已设计变体 | 可点击入口 |
|---|---|---|
| policies · policy | single / manual / latency / failover | [single](page-designs.html#policies/single) · [manual](page-designs.html#policies/manual) · [latency](page-designs.html#policies/latency) · [failover](page-designs.html#policies/failover) |
| node-editor · protocol | SSH / VLESS / VMess / Shadowsocks / Trojan / Hysteria2 / TUIC / WireGuard / SOCKS / HTTP / AnyTLS / Naive / MASQUE | [SSH](page-designs.html#node-editor/SSH) · [VLESS](page-designs.html#node-editor/VLESS) · [VMess](page-designs.html#node-editor/VMess) · [Shadowsocks](page-designs.html#node-editor/Shadowsocks) · [Trojan](page-designs.html#node-editor/Trojan) · [Hysteria2](page-designs.html#node-editor/Hysteria2) · [TUIC](page-designs.html#node-editor/TUIC) · [WireGuard](page-designs.html#node-editor/WireGuard) · [SOCKS](page-designs.html#node-editor/SOCKS) · [HTTP](page-designs.html#node-editor/HTTP) · [AnyTLS](page-designs.html#node-editor/AnyTLS) · [Naive](page-designs.html#node-editor/Naive) · [MASQUE](page-designs.html#node-editor/MASQUE) |
| ruleset-edit · editor-mode | visual / json | [visual](page-designs.html#ruleset-edit/visual) · [json](page-designs.html#ruleset-edit/json) |
| dns · kernel-json | sing-box / Xray | [sing-box](page-designs.html#dns/sing-box) · [Xray](page-designs.html#dns/Xray) |
| template · kernel-json | sing-box / Xray | [sing-box](page-designs.html#template/sing-box) · [Xray](page-designs.html#template/Xray) |
| custom-config · kernel-json | sing-box / Xray | [sing-box](page-designs.html#custom-config/sing-box) · [Xray](page-designs.html#custom-config/Xray) |
| custom-outbound · kernel-json | sing-box / Xray | [sing-box](page-designs.html#custom-outbound/sing-box) · [Xray](page-designs.html#custom-outbound/Xray) |
| backup · storage-mode | local / webdav | [local](page-designs.html#backup/local) · [webdav](page-designs.html#backup/webdav) |
| updates · artifact | client / kernel | [client](page-designs.html#updates/client) · [kernel](page-designs.html#updates/kernel) |
| ssh-deploy · step | target / identity / key / authorization / deploy-verify / result-recovery | [target](page-designs.html#ssh-deploy?step=0) · [identity](page-designs.html#ssh-deploy?step=1) · [key](page-designs.html#ssh-deploy?step=2) · [authorization](page-designs.html#ssh-deploy?step=3) · [deploy-verify](page-designs.html#ssh-deploy?step=4) · [result-recovery](page-designs.html#ssh-deploy?step=5) |
| node-editor/SSH · authentication | key / password | [key](page-designs.html#node-editor/SSH?auth=key) · [password](page-designs.html#node-editor/SSH?auth=password) |
| settings-app · platform | Windows / macOS / Linux | [Windows](page-designs.html#settings-app?platform=Windows) · [macOS](page-designs.html#settings-app?platform=macOS) · [Linux](page-designs.html#settings-app?platform=Linux) |

## 状态边界

- 列表提供空集合入口；可编辑页面提供未载入草稿空态与载入默认值；忙碌预览禁用主体提交，状态选择器保持可用。
- 异步更新、测试、部署、备份、校验、权限、下载提供失败与重试；失败保留既有数据。删除/关闭均先确认范围，关闭不使用成功语义色。
- 保存/放弃/继续编辑、节点选择器暂存、人工固定主机身份均有独立提交边界。
- 不以继承颜色认定设计完成：表中每一页都有内容、字段、动作和对应状态。精确路由与状态数组以coverage.json为准。

## 真实未覆盖项与限制

- **implementation-boundary · 真实网络、订阅、文件导入、私钥、备份、关闭连接、系统修改、下载、安装**：用户禁止；只设计本地模拟流程，不是遗漏的生产实现承诺。
- **prototype-limitation · DNS/模板表单与双内核JSON自动互转、完整内核语义校验**：明确为独立演示草稿；只验证JSON语法与对象根。
- **prototype-limitation · 复杂过滤表达式实际执行、路由条件全量语法及策略引擎计算**：字段及流程可评审；不执行用户代码或真实策略决策。
- **verification-gap · 二维码物理扫码、屏幕阅读器全遍历、真实Avalonia三平台/DPI**：本地QR编码及校验可测；真实设备与原生辅助技术未验证。
- **verification-gap · 所有页面×所有状态×所有宽度的像素级穷举**：脚本覆盖所有路由/语言/主题/状态；渲染聚焦12个代表性长表单、JSON错误、覆盖确认与SSH向导组合。

## 验证入口

- 原32项：[tests/prototype-check.cjs](tests/prototype-check.cjs)
- 次级页面：[tests/secondary-pages-check.cjs](tests/secondary-pages-check.cjs)
- 代表性实际渲染：[tests/secondary-visual-review.html](tests/secondary-visual-review.html)

已执行结果：原32项与次级页面35项均通过，共67项。所有目录路由的中英文/浅深主题与五种状态均有内容回归；13种协议检查特有字段。工程覆盖审计另检查全部30个AXAML与2个动态页面，避免仅扫描AXAML而漏掉C#构建的界面。

真实渲染12个代表场景，覆盖长SSH表单、错误JSON、恢复覆盖确认、SSH向导及1440/760/600宽度。渲染发现宽窗继承双列CSS、JSON编辑区收缩，已移除无关样式、限定工作区单列、禁止编辑区收缩，并增加静态回归。折叠导航返回链接也已修正。

工程归档后已独立复跑65项测试，并在浏览器重新渲染修正后的全部12个代表场景。最终[视觉复核图](../../../artifacts/secondary-primer-design-20261025/secondary-visual-review-final.jpg)确认宽窗表单不再挤到右侧，JSON编辑区和错误说明可见，恢复确认及SSH向导在窄窗正常呈现；另保存了1440宽设置页与600宽英文深色SSH表单。未执行所有页面×状态×宽度的像素级穷举。

后续复查补齐应用工具、关于与帮助两页，并独立复跑当前67项测试。600宽[中文工具范围确认](../../../artifacts/secondary-primer-followup-20261025/tools-scope-zh-600.jpg)及[英文深色关于弹窗](../../../artifacts/secondary-primer-followup-20261025/about-en-dark-600.jpg)已在浏览器实际渲染复核。
