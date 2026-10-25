# macOS 原生窗口启动与验收

## 已定位的故障

Avalonia 12.1.2 的原生渲染计时器调用 `CVDisplayLinkCreateWithActiveCGDisplays`，失败时会在窗口建立前抛出异常。上游记录见 [Avalonia #18895](https://github.com/AvaloniaUI/Avalonia/issues/18895)。

本机复现：会话锁定时，AppKit 初始化后活动显示器数量变为0，CoreVideo返回 `-6661`；在线显示器仍为2，因此只检查“连接了显示器”不能代表原生窗口可以启动。未初始化AppKit的独立CoreVideo探针有时返回成功，也不足以证明原生窗口可用。

## 修复行为

- 客户端在初始化Avalonia之前检查当前显示会话、锁屏标志、主显示器、活动显示器和CoreVideo状态；会话不可用时等待恢复，保持单实例互斥量，支持终端取消。等待期间不启动AppKit，也不启用代理核心。
- 既有客户端的运行连接不经过这段启动等待；此修复只处理新进程启动阶段。
- 不解锁系统、不更改显示器或安全设置、不切换到Headless。正常显示会话继续使用原有Avalonia原生渲染路径。
- Review默认等待15秒；无法开始原生验收时退出非零，写入明确原因，清除旧成功清单。该状态不能算原生通过。
- 这项启动保护覆盖已复现的锁屏及显示会话不可用情形，不宣称解决所有显示器重配置或图形驱动故障。

## 验收条件

原生检查必须不传 `--headless`，成功清单的 `renderer` 为 `native`，并通过“可见AppKit NSWindow且原生句柄非零”断言。中文浅色1440宽与英文深色600宽均需完成实际控件检查和截图。

```sh
dotnet build v2rayN/v2rayN.Desktop.Review -c Release
dotnet v2rayN/v2rayN.Desktop.Review/bin/Release/net10.0/v2rayN.Desktop.Review.dll artifacts/native-review/zh-light --primer-only --language=zh-Hans --theme=light --width=1440
dotnet v2rayN/v2rayN.Desktop.Review/bin/Release/net10.0/v2rayN.Desktop.Review.dll artifacts/native-review/en-dark --primer-only --language=en --theme=dark --width=600
```

真实macOS CI使用 `.github/workflows/native-macos-review.yml`，先建立原生窗口再验收两组界面，并保存原生证据。它是测试版发布的必需依赖；失败或缺少证据不能发布。

本机锁屏时需由用户手动解锁并保持显示器开启。Headless结果和原生结果分别记录，不互相替代。
