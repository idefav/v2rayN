# v2rayN

### A GUI client for Windows, Linux and macOS. Support [Xray](https://github.com/XTLS/Xray-core) and [sing-box](https://github.com/SagerNet/sing-box) and [others](https://github.com/2dust/v2rayN/wiki/List-of-supported-cores)

[![CodeFactor](https://www.codefactor.io/repository/github/2dust/v2rayn/badge)](https://www.codefactor.io/repository/github/2dust/v2rayn)
[![Release](https://img.shields.io/github/v/release/2dust/v2rayN?logo=github&label=Release)](https://github.com/2dust/v2rayN/releases)
[![Downloads](https://img.shields.io/github/downloads/2dust/v2rayN/latest/total?logo=github&label=Downloads)](https://github.com/2dust/v2rayN/releases)
[![Telegram](https://img.shields.io/badge/Telegram-Chat-26A5E4?logo=telegram)](https://t.me/v2rayn)
 
[![Windows](https://img.shields.io/badge/Windows-supported-0078D6?logo=windows)](https://github.com/2dust/v2rayN) 
[![Linux](https://img.shields.io/badge/Linux-supported-FCC624?logo=linux&logoColor=000)](https://github.com/2dust/v2rayN) 
[![macOS](https://img.shields.io/badge/macOS-supported-000000?logo=apple)](https://github.com/2dust/v2rayN) 
[![GPG Signed](https://img.shields.io/badge/GPG-signed-4B32C3?logo=gnuprivacyguard)](https://github.com/2dust/v2rayN)


---

## Download / 下载

Download the latest release here:

在这里下载最新版本：

[https://github.com/2dust/v2rayN/releases](https://github.com/2dust/v2rayN/releases)


> [!TIP]
> v2rayN is the desktop version. For the mobile version, please visit the v2rayNG \
> v2rayN 是电脑版，手机版请访问 v2rayNG
>
> https://github.com/2dust/v2rayNG

---

## Documentation / 使用文档

Read the Wiki for usage guides and configuration details.

请阅读 Wiki 获取使用说明和配置教程。

[https://github.com/2dust/v2rayN/wiki](https://github.com/2dust/v2rayN/wiki)

---

## Supported Platforms / 支持平台

| Platform / 平台 | x64 | x86 | arm64 | riscv64 | loong64 |
| --- | --- | --- | --- | --- | --- |
| Windows | ✅ | ✅ | ✅ | - | - |
| Linux | ✅ | - | ✅ | ✅ | ✅ |
| macOS | ✅ | - | ✅ | - | - |

Minimum OS requirements: [Release files introduction](https://github.com/2dust/v2rayN/wiki/Release-files-introduction) / 最低系统要求：[发布文件介绍](https://github.com/2dust/v2rayN/wiki/Release-files-introduction)

---

## macOS: “已损坏，无法打开” / “App is damaged and can't be opened”

本仓库的 SSH/UI **测试版**未使用 Apple Developer ID 签名和公证。浏览器下载的文件带有下载隔离标记，可能被 macOS Gatekeeper 拦截；“已损坏”也可能表示下载不完整、签名失效或文件被修改，不能仅凭提示认定没有风险。测试版下载入口为 [idefav/v2rayN Releases](https://github.com/idefav/v2rayN/releases)，与上方上游正式版入口不同。

### 1. 先确认来源与下载完整性

从对应 Release 页面下载适合机器的包：Apple Silicon 使用 `arm64`，Intel 使用 `64`。测试版提供 `SHA256SUMS`，将本地计算值与**同一版本、同名文件**的记录对比：

```bash
shasum -a 256 "$HOME/Downloads/v2rayN-macos-arm64.dmg"
hdiutil verify "$HOME/Downloads/v2rayN-macos-arm64.dmg"
```

如果下载文件名带有 `(1)` 等后缀，请替换为实际路径并保留引号。SHA-256 不匹配或磁盘映像校验失败时重新下载，不执行下面的放行步骤。校验和只能证明与发布文件一致，不代替开发者身份验证。

### 2. 将应用复制到“应用程序”，再打开

打开 DMG，将 `v2rayN.app` 拖入 `/Applications`，然后推出磁盘映像，从“应用程序”打开。不要直接运行 `/Volumes/...` 磁盘映像内的应用，也不要对只读映像内的文件执行修改命令。

若已有客户端正在工作，请先保存配置并安排好切换时间；退出旧客户端可能影响当前代理连接，不要为了处理此提示直接停止正在使用的代理。

### 3. 对确认可信的测试版单独放行

尝试打开后，如果“系统设置 → 隐私与安全性”出现该应用的“仍要打开”，按提示确认。该入口不一定适用于所有“已损坏”提示。参见 [Apple：在 Mac 上安全地打开 App](https://support.apple.com/zh-cn/102445)。

如果仍被拦截，且已经确认来源可信、文件校验通过，可在终端**仅移除这个应用**的下载隔离属性，再从“应用程序”重试：

```bash
xattr -dr com.apple.quarantine "/Applications/v2rayN.app"
```

安装在其他目录时替换路径。只有出现 `Permission denied` 且确认目标路径无误时，才使用管理员权限：

```bash
sudo xattr -dr com.apple.quarantine "/Applications/v2rayN.app"
```

此操作不会修复损坏的二进制、缺失的文件或无效签名。不要关闭全局 Gatekeeper／SIP，不要对整个 `/Applications` 或下载目录递归清除属性；若提示“将损坏你的电脑”或检测到恶意软件，不按此流程绕过。

### 4. 仍无法打开时

收集以下只读诊断输出，连同 macOS 版本、CPU 架构、测试版标签及下载文件名提交 Issue：

```bash
codesign --verify --deep --strict --verbose=2 "/Applications/v2rayN.app"
spctl --assess --type execute --verbose=4 "/Applications/v2rayN.app"
xattr -lr "/Applications/v2rayN.app"
```

未经 Developer ID 签名／公证的测试版被 `spctl` 拒绝是可能的，不能单独证明文件损坏。若报告签名资源被修改或缺失，应重新下载完整应用并反馈，不建议用重新签名掩盖错误。分享输出前隐藏个人路径。长期减少此类拦截需要发布方使用 Developer ID 签名并完成 Apple 公证；本节命令只是可信测试包的本地处理方式。

**English:** These feature previews are not Developer ID signed or notarized. Verify the download against the matching release checksums, copy the app from the DMG to Applications, then use Privacy & Security → Open Anyway if available. For a trusted, verified preview only, the scoped `xattr` command above removes its download quarantine attribute. It does not repair corrupted files or signatures; do not disable system-wide security.

---

## GPG Verification / GPG 签名校验

以下为上游正式版的 GPG 校验说明。本仓库的 `*-ssh-preview.*` 测试版未提供 GPG 发布签名，使用各测试版附件中的 `SHA256SUMS` 校验完整性。

Release files are signed with GPG to verify authenticity and integrity, helping prevent mirror, ISP, or CDN hijacking.

发布文件已使用 GPG 签名，可用于校验文件真实性与完整性，预防镜像站、运营商或 CDN 劫持。

### Fingerprint / 公钥指纹

```text
7694 5E9F 3E9A 168F 8070 F195 805D 661C
134D FAF6 8903 C199 463C 31E5 AE90 3AE0
```

---

## Community / 社区

Telegram Group / Telegram 群组：

[https://t.me/v2rayN](https://t.me/v2rayN)

Telegram Channel / Telegram 频道：

[https://t.me/github_2dust](https://t.me/github_2dust)
