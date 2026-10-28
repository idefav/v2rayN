# TUN 独立设置页 · 视觉绑定

沿用 v2rayN 现有 Primer 语义：白色画布、浅灰分区、细边框、紧凑系统字体，蓝色只强调主要操作和焦点。

HTML 首个 style 保留注入的完整 tokens.css；以下 OKLch 仅用于色彩角色说明，不替换绑定值。

| 角色 | 绑定值 | OKLch 近似表达 |
|---|---|---|
| --bg | #ffffff | oklch(100% 0 0) |
| --surface | #f6f8fa | oklch(97.82% .00342 247.86) |
| --fg | #1f2328 | oklch(25.42% .01112 254.04) |
| --muted | #656d76 | oklch(53.10% .01719 251.29) |
| --border | #d0d7de | oklch(87.58% .01230 247.97) |
| --accent | #0969da | oklch(53.99% .19058 257.48) |

- 展示与正文：`-apple-system, BlinkMacSystemFont, "Segoe UI", "Noto Sans", Helvetica, Arial, sans-serif`。
- 技术值：`ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, "Liberation Mono", monospace`。
- 正文 14px、页面标题 24px、分区标题 16px；字重仅 400 / 600；中文标题字距为 0。
- 1px 边框、6px 控件圆角；主内容密集分组，帮助独立滚动。
- 单一实心主要操作为底部“保存并应用”；开关与当前导航采用中性色。
- 深色采用现有设计规范的深海军蓝 OKLch 角色，浅深主题共享结构、密度和交互。

只读参考：`/Users/idefav/Documents/sources/workspace/ssh-tunnel-group/v2rayN/docs/design/main-primer-opendesign/design-spec.md`。未读取或修改真实运行配置。
