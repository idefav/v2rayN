# 独立代理 sshd 的网关安装修复（2026-11-01）

## 问题与修复

SSH 编辑器部署 tun2proxy 网关时报 `E_SSHD_COMMAND_AMBIGUOUS`。部署使用节点本身的 SSH 地址和端口，没有回退到管理端口 22。

本次实际链路为 `106.14.159.136:10024` 透明中继到新加坡服务器的代理 SSH `10026`，服务端 `SSH_CONNECTION` 确认本地端口为 `10026`。部署助手沿会话祖先进程识别到 `sshd-udp-10026.service`，其启动命令包含：

```text
-D -e -f /etc/ssh/sshd_config -p 10026
-oAllowUdpForwarding=yes -oPidFile=/run/sshd-udp-10026/sshd.pid
```

旧解析器拒绝所有 `-o` 参数，因而把这套可识别的启动方式误判为不明确。修复只允许 `AllowUdpForwarding=yes/no` 和 `PidFile=绝对路径/none`，支持合并或分开的 `-o` 写法，并将原值完整带入 `sshd -T/-t`。代理监听端口也保持不变。其他未知覆盖项、包装脚本和无法可靠解析的命令仍然拒绝。

新增回归覆盖真实服务命令、分开参数、显式禁用 UDP、无 PID 文件、`SSHD_OPTS` 展开及不受支持的配置覆盖。旧代码的正向用例复现错误，修复后 Linux 部署助手全部测试通过；Windows amd64/arm64 全部包的测试编译通过，Linux 专用测试不会进入 Windows 构建。

## 真实部署结果

在 macOS 使用与界面相同的 `SshGatewayDeploymentService`，隔离本地配置和事务目录，固定该节点的已确认主机公钥与凭据。先以旧助手复现同一错误，再只更换助手确认预检通过。最终安装使用正式发布的 sing-box `1.14.3-ssh-udp.3` 验证。

首次数据验证因验证程序复制了旧 `.1` 核心而未能启动；客户端和服务器均确认事务已回滚。换成 `.3` 核心后重新预检、安装、验证和提交全部成功。

| 检查 | 结果 |
| --- | --- |
| 目标代理服务 | `sshd-udp-10026.service`，未修改或重启 |
| 网关 | 官方 `udpgw-server 0.8.7`，Linux amd64 |
| 监听 | 仅 TCP `127.0.0.1:7300` |
| 服务 | `v2rayn-udpgw.service`，active、enabled、DynamicUser 非 root |
| SSH 策略 | 此账号未启用 ForwardProxy，无需新增例外 |
| 部署验证 | 实际监听、新 SSH 连接心跳、随机令牌 UDP 回显通过，事务 committed |
| 当前客户端 | 核心 PID、配置 SHA-256、运行模式未变化；保持自定义 UDP 模式 |
| 原自定义模式 | 两个 STUN、HTTPS、HTTP3 共 4/4 通过 |
| tun2proxy 模式 | HTTPS、两个 STUN、DNS/UDP A 与 AAAA、DNS/TCP A、HTTP3 共 7/7 通过 |
| 观察到的出口 | `43.98.191.120` |

网关安装不会自动切换运行节点；在节点草稿选择 tun2proxy 并填写 `127.0.0.1:7300`，保存并启用后才使用网关模式。当前在线流量未因本次安装切换模式。

本次验证覆盖 macOS 发起部署、Linux amd64 目标和实际代理 SSH 链路；不替代 ARM64 目标、Windows 交互、系统 TUN、整机重启及所有故障场景的独立验收。当前核心是用户已安装的独立更新，缺少相邻打包清单，因此运行检查器的 `manifest_matches_binary` 为 false；协议探测、版本、进程和配置保持检查均通过，未把该报告的整体 `ok` 标记为通过。

本地可复现记录位于工作区 `artifacts/ssh-udpgw-install-fix-20261101`，包含部署服务调用程序、修复前后预检、安装事务结果、服务状态及两种模式的协议探测报告。不包含密码或私钥内容。
