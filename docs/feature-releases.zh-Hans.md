# SSH feature 测试版打包发布

`release SSH feature preview` 支持从 `feature/native-ssh-profiles` 或与源码版本对应的 `release/<Version>` 构建测试版。
推送 `v<Version>-ssh-preview.<序号>` 标签即可触发，例如：

```bash
git switch feature/native-ssh-profiles
git pull --ff-only
git tag -a v7.25.5-ssh-preview.1 -m 'SSH feature preview 1'
git push origin feature/native-ssh-profiles
git push origin v7.25.5-ssh-preview.1
```

`Version` 必须与 `v2rayN/Directory.Build.props` 一致，序号为正整数。
标签提交必须属于 feature 分支或对应版本的 release 分支，工作流校验标签与所选 ref 指向同一提交，不会转到 `master`。
如果 fork 尚未自动注册新工作流，可通过已有的 `release all platforms` 入口启动：

```bash
gh workflow run build-all.yml --repo idefav/v2rayN \
  --ref v7.25.5-ssh-preview.1 -f release_tag=v7.25.5-ssh-preview.1
```

该入口识别 `-ssh-preview.` 标签后转入 feature 打包流程，不触发上游发布任务。
也可重跑失败的 Actions；新工作流进入默认分支后，可以直接手动选择标签并传入同名
`release_tag`。标签与所选 ref 必须指向同一个提交。

## 从 release 分支打包

例如当前 `release/7.25.6`：先将变更提交并推送到 release，再在该提交创建未使用过的预览标签。标签推送会自动触发一次任务；如果需要 Actions 的运行来源明确显示 release 分支，取消该标签的自动任务后，使用以下入口手动启动：

```bash
gh workflow run build-all.yml --repo idefav/v2rayN \
  --ref release/7.25.6 -f release_tag=v7.25.6-ssh-preview.8
```

运行详情的 `headBranch` 应为 `release/7.25.6`，`headSha` 必须等于该标签提交；发布说明会记录实际构建来源。这样使用的是 release 提交，而非重新回到 feature 分支构建。

发布内容：

| 平台 | 架构 | 格式 |
| --- | --- | --- |
| Windows WPF | x64 / arm64 | ZIP |
| Windows Avalonia | x64 / arm64 | ZIP，文件名带 `-desktop` |
| Linux | x64 / arm64 | ZIP |
| macOS | Intel x64 / Apple Silicon arm64 | ZIP、DMG |

共 10 个包及一个 `SHA256SUMS` 文件。所有包包含 .NET 运行时和代理核心。
每次运行先固定 `2dust/v2rayN-core-bin` 的资源提交；专用 sing-box 从 `packaging/build-ssh-core.sh` 固定的用户 fork 提交构建并附核心摘要清单，Linux/Windows 同时交付匹配的 Cronet 共享库。测试和所有包使用相同的固定源码与资源。
Linux ZIP 解压后运行 `v2rayN`；macOS 可使用 DMG 安装。

发布前必须通过单元测试、临时 Docker OpenSSH/Paramiko 集成测试、全部架构编译、
ZIP/DMG 完整性检查及资产数量检查。仅在全部成功后发布 GitHub Pre-release，
不会设为 Latest。上传中断只会留下草稿，可重跑；已公开的版本不会被覆盖，
新构建请增加标签序号。

Primer 原型的67项回归、30个AXAML及2个动态页面的设计覆盖审计，以及两组 Avalonia Headless + Skia 控件回归也属于发布门禁。设计用 `coverage.json` 是源码清单，已通过精确白名单排除在通用代码覆盖率忽略规则之外；可使用干净的 `git archive` 导出验证，避免依赖本地未跟踪文件。

macOS原生窗口检查也是发布必需门禁：实际创建AppKit NSWindow，不传Headless参数，分别验收中文浅色和英文深色，并校验成功清单与截图。锁屏或显示会话不可用不能算原生通过；详见[原生渲染启动与验收说明](macos-native-rendering.zh-Hans.md)。

Review 在注入演示节点前等待真实的节点列表初始化与已排队刷新完成，避免慢速Linux运行器用后台结果替换夹具对象。共用AppManager和SQLite的订阅测试按工程约定串行运行；不通过放宽界面断言或跳过失败测试来发布。

测试版不要求 `GPG_PRIVATE_KEY`，不生成 GPG 签名；macOS 包未经 Apple 开发者签名
及公证。现有上游正式发布工作流仍独立使用其签名配置。

下载后可在包所在目录校验：

```bash
# Linux
sha256sum --check SHA256SUMS --ignore-missing
# macOS：核对输出与 SHA256SUMS 中对应文件的记录
shasum -a 256 v2rayN-macos-arm64.dmg
```
