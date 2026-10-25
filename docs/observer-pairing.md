# 探测服务域名配对

在“代理检查 → 配置服务”弹窗中填写服务域名、管理员提供的完整 `EP1.`
配对串和设备名称，然后点击“配对并连接”。成功后配对串清空；不需要输入
端口、长期 API Key 或导入 JSON。高级区域展示端点、设备 ID 和信任指纹。

管理员通过可信 SSH 终端执行：

```sh
sudo egress-probe-server pairing create --expires 10m
```

配对前先校验配对串携带的 CA 公钥指纹、域名和服务器 ID。客户端在本地生成
P-256 私钥，只发送 CSR。后续控制接口使用 mTLS；观察结果仍由原签名公钥验证。
发现接口不能覆盖已配对服务的信任材料，证书验证不忽略域名、有效期或用途。

设备凭证保存到 macOS Keychain、Windows 当前用户 DPAPI，或 Linux libsecret
访问的 Secret Service。Linux 需要系统安装 libsecret 和可用的 Secret Service。
锁定、不可用或拒绝授权时不回退到明文文件；只有明确点击“仅本次使用”才在
当前进程激活临时身份，重启后需要重新配对。

普通 `observer-device.json` 只保存域名、设备 ID、公共 CA/观察签名公钥、探测
端点和安全存储引用。报告及基线不包含设备私钥或配对串。

剩余不足 30 天的证书在使用时续期；保存新凭证失败时保留旧凭证，以相同 CSR
重试。旧证书最多重叠 24 小时。过期证书或变更后的服务器身份需要重新配对。

“解除配对”先撤销服务器设备，再删除本地身份。离线失败后可明确选择仅删除
本地凭证；这不表示服务器已经撤销，需管理员执行 `devices revoke DEVICE_ID`。

## 开发验证

Go 服务：`go test -race ./...`。客户端测试类：`ObserverIdentityTests`、
`ObserverPairingTests`、`ObserverLifecycleTests`，以及原有 `ObserverTests`。

平台安全存储集成测试需要显式设置 `V2RAYN_TEST_SECRET_STORE=1`，运行
`ObserverIdentityTests.PlatformSecureStoreRoundTripWhenRequested`。它使用随机
隔离测试项并清理，不操作现有设备凭证。未设置时不能将该用例计为平台实机验证。
Windows/Linux/macOS 应各自在目标系统执行；交叉编译不替代运行验收。

本次保留现有布局，配置仍在独立弹窗中完成，不恢复新设计稿重构。
