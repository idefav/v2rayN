# 应用图标统一替换

2026-11-13。保留 V7 紧凑连接界面、独立设置窗口、抽屉及现有功能，只更新应用品牌资源。

## 设计

Local Codex 项目 `v2rayn-brand-20261113`，运行 `e8e2db0d-aad2-44d7-8261-3656caecd4a1` 已成功结束。
选用「对向接续」：两条中心对称的开放圆端路径，蓝色底板、白色主体，替换旧的斜体 V。
界面标题及关于页使用相同几何的单色版本；静态应用图标不表达连接状态。

生产源：`packaging/icons/app-icon.svg` 与 `v2rayN/v2rayN.Desktop/Assets/BrandGlyph.svg`。
生成方法和平台尺寸见 `packaging/icons/README.md`。

## 接入范围

- Avalonia 与 WPF 的应用 ICO、四种通知 ICO，共 10 个文件。
- macOS ICNS 与 Linux 启动器 PNG，共 2 个文件。
- Avalonia 顶部标题和关于页从独立品牌 SVG 加载；功能图标保持原来的笔画粗细。
- macOS 原生状态栏保留独立 P/T 实际状态、蓝/绿/琥珀语义和非模板彩色渲染。
- 仍尊重用户提供的通知图标覆盖，不删除已有配置或自定义图标。
- macOS 打包合并内核资源后重新写入项目中的规范图标；本地 DMG 同步替换 `Resources/AppIcon.icns`，避免只替换 MacOS 目录而 Dock 仍用旧图标。

## 验证

本地证据位于仓库同级 `artifacts/local-brand-20261113`。

| 项目 | 结果 |
| --- | --- |
| SVG 同源几何、12 个平台导出字节复核 | 通过 |
| ICO 全部帧通过真实 Avalonia/Skia 解码，透明角部有效 | 通过，纳入启动回归 |
| ICNS 经 macOS `iconutil` 解码至完整 iconset | 通过，最大 1024px |
| 隔离启动 / V7 紧凑界面 / V5 抽屉 | 63 / 901 / 160 项检查通过，Headless + Skia |
| macOS ARM64 Release publish | 通过 |
| WPF Windows 目标编译 | 通过，框架依赖 DLL；9 个新图标帧均在 PE 的 RT_ICON 表中 |
| DMG 只读挂载、文件与权限逐一比对 | 40 个文件条目一致 |
| 签名、配置目录标记、内核与助手哈希 | 通过，ad-hoc 签名；未公证 |
| 本轮原生启动、Dock 加载和菜单栏 | 68 项通过；AppKit 实际图形与新标记轮廓匹配，旧 V 负例被拒绝 |
| 原生 V7 紧凑界面 / V5 抽屉 | 904 / 162 项通过，共 23 张截图 |

首次原生检查因锁屏未能开始，用户解锁后已连续补测完成。系统为 ICNS 加了缩放和高光，原先逐像素颜色比较误报；测试现采用包内 ICNS 字节一致性与归一化主体轮廓比对，实际重合率 97.22%，并验证旧 V 无法通过。实际菜单栏按钮 22×22，图像 17×17，`Visible=true`、`Template=false`。
补测只修改测试程序和说明，生产代码与已交付 DMG 未变，SHA256 再次核验通过。

Windows 自包含发布缺少本地运行时缓存；未执行 Windows/Linux 原生运行或安装。
未安装、替换或重启当前客户端，未修改本机 TUN、代理、路由或用户配置，未推送或发布。

## 本地包

`artifacts/local-brand-20261113/v2rayN-macos-arm64-local-brand.dmg`

SHA256：`9aacb845a4c6e5b4d329dacb20e4d8c8bfe9414a49a64d1ec8f569c59b3c8bac`

保留原有 DNS、退出清理、配置目录与模式切换修复；仍使用相同 sing-box 和 AmazToolV2。
