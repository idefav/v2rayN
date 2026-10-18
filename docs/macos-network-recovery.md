# macOS 网络恢复

macOS 客户端在应用启动、系统唤醒和网络变化后自动检查当前连接。此功能适用于关闭 TUN、选择“自动配置系统代理”、未指定自定义系统代理脚本的配置；隐藏到托盘后仍然运行。

恢复按以下顺序执行：

1. 合并网络事件，确认默认接口及其地址稳定 3 秒；离线时等待网络。
2. 从 macOS 读取实际 HTTP、HTTPS、SOCKS 代理开关、地址及端口。状态不符时重新应用，最多尝试 3 次，每次间隔 2 秒，并读回验证。
3. 使用当前内核的本地 SOCKS 端口请求设置中的连通性测试 URL，同时请求 Google 的备用检测地址。每轮最多 5 秒，任意目标返回 2xx 即通过。
4. 全部失败后间隔 3 秒再检测；连续两轮失败才重启当前内核。重启后等待 SOCKS 握手就绪，再验证系统代理与实际请求。

每 60 秒补充一次检查。每轮最多重启一次，自动重启至少间隔 60 秒，5 分钟内最多两次。正常连接不会因一次检测失败或一次唤醒而重启。代理设置失败单独报告，不通过反复重启内核处理权限问题。

状态栏保留内核运行状态，另显示“检查中”“恢复中”“等待网络”或“连接异常”；成功后清除恢复状态。诊断日志中的 `ConnectionRecovery` 前缀区分 `system proxy` 和 `core connection` 故障，不记录测试 URL、密码或私钥。

手动切换节点、路由、代理模式或重启服务会取消旧检查。所有内核重载共用串行协调器；退出先停止恢复服务和注销通知，再清理系统代理、关闭内核。

## 开发验证

需要 .NET 10 SDK。常规测试无需外网；SSH 集成测试需要带 paramiko 的 Python，以及兼容的 sing-box。

```sh
dotnet build v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj -c Release
SSH_TEST_PYTHON=/path/to/python \
SSH_TEST_SINGBOX=/path/to/sing-box \
dotnet v2rayN/ServiceLib.Tests/bin/Release/net10.0/ServiceLib.Tests.dll --progress off
```

`StalledSshRecoversByRestartingCurrentCoreOnSameProxyPort` 使用回环 SSH/HTTPS 服务，模拟 SSH TCP 仍存活但无法打开转发通道的情况，验证重启后同一本地端口恢复 TLS 请求。`NativeWakeNotificationUsesWorkspaceCenterAndUnregisters` 在 macOS 原生通知中心验证回调及注销，不让整台电脑休眠。

在当前配置为 `127.0.0.1:10808` 的开发 Mac 上，额外设置 `RECOVERY_LIVE_CHECK=1` 可执行实际默认网络、系统代理和 Google 连通性的只读检查。此选项不修改系统设置。

隔离界面工具提供 `--recovery-only`，渲染五种恢复状态及 700/1100 宽度，使用测试数据并禁用系统代理修改。

## 本机验收

真实短时休眠、整夜休眠、Wi-Fi 切换、开机登录时网络延迟就绪以及托盘隐藏需在安装预览版后逐项验证。前提是网络及远端节点已恢复正常，目标在 60 秒内恢复访问。检查实际系统代理、显式代理请求及遵循系统代理的原生网络请求三条证据；自动化通知或回环夹具通过不能代替真实休眠验收。
