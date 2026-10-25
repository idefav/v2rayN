# v2rayN 主界面设计原型

2026-10-25，通过 Local Codex 生成并复核，采用 GitHub Primer 风格设计系统。当前交付为独立设计原型，不接入真实代理、节点或服务器。

## 文件

- `index.html`：可直接在浏览器打开的独立原型，无需安装依赖。
- `design-spec.md`：颜色、字体、间距、组件状态、主题、语言及响应式规则。
- `implementation-handoff.md`：向 Avalonia 实施时的控件映射与交接建议。
- `tests/prototype-check.cjs`：原型状态与内容回归测试。
- `tests/diagnostic-visual-review.html`：协议检查页的主题、语言、尺寸视觉检查入口。
- `page-designs.html`：其余页面的统一交互设计目录，包含27个路由、13种节点协议变体。
- `secondary-pages-spec.md`：次级页面规范、交互边界及 Avalonia 控件映射。
- `coverage.md`、`coverage.json`：页面、状态、变体和验证限制清单。
- `tests/secondary-pages-check.cjs`、`tests/secondary-visual-review.html`：次级页面回归与十二场景视觉复查入口。

## 设计范围

节点工作区、运行摘要、节点详情、连接控制、日志抽屉和关联代理检查页；支持中文/英文、浅色/深色、空状态、内核未运行和模拟应用失败。

选中节点与当前运行节点分别标识。代理检查围绕本次运行快照，按协议及路径展示通信、出口证据与预期比较，明确区分客户端出口、递归解析器出口和缓存信息。所有数据均为虚构演示。

## 验证

```sh
node docs/design/main-primer-opendesign/tests/prototype-check.cjs
```

32 项状态与内容测试通过。这些测试不验证真实网络协议或代理出口，也不替代客户端集成测试。

另在浏览器检查了 1440、760、600 宽度、主题与语言切换、节点搜索、模拟应用及失败保留原节点、空状态、内核未运行、多协议结果和取消进度。截图保存在 `artifacts/main-primer-design-20261025/`。

## 次级页面补齐与代码复查

同日按当前客户端的30个 AXAML 视图完成覆盖盘点；此前缺少独立设计的25个视图已映射到统一 Primer 原型，详见[工程页面审计](../page-coverage.md)。新增页面的设计交付不代表它们已全部接入原生客户端。

原32项与次级页面35项原型测试独立复跑均通过。修正后的十二场景已在浏览器重新渲染复核，覆盖1440/760/600宽度、中英文、浅深主题、SSH长表单、JSON错误、恢复覆盖确认及SSH向导。另补齐C#动态生成的应用工具和关于页，分别验证600宽中文范围确认与英文深色关于弹窗。截图位于 `artifacts/secondary-primer-design-20261025/` 和 `artifacts/secondary-primer-followup-20261025/`。

```sh
node docs/design/main-primer-opendesign/tests/secondary-pages-check.cjs
node docs/design/main-primer-opendesign/tests/native-coverage-check.cjs
```
