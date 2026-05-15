# MAAUnified CI、发布与验收

本页记录主仓 workflow、产物约定、baseline 门禁和维护者发布步骤。

本地构建、运行和测试见 [本地开发](./development.md)。

## CI workflow

主仓以这两条 workflow 为准：

- `.github/workflows/ci-avalonia.yml`：调试包和合并前验证
- `.github/workflows/release-maaunified.yml`：正式发布包

`src/MAAUnified/CI/` 下的文件只当模板或同步副本看，不作为实际入口。

## 打包矩阵与产物

| 名称 | Runner | RID | MaaDeps triplet | CMake preset | Debug | Release |
| --- | --- | --- | --- | --- | --- | --- |
| `windows-x64` | `windows-latest` | `win-x64` | `x64-windows` | `windows-unified-publish-x64` | 完整调试包 | `.zip` |
| `linux-x64` | `ubuntu-latest` | `linux-x64` | `x64-linux` | `linux-publish-x64` | 完整调试包 | `.AppImage` |
| `macos-x64` | `macos-latest` | `osx-x64` | `x64-osx` | `macos-publish-x64` | 完整调试包 | `.dmg` |
| `macos-arm64` | `macos-latest` | `osx-arm64` | `arm64-osx` | `macos-publish-arm64` | 完整调试包 | `.dmg` |

调试包保留日志和符号，用来复现和排障。正式包面向分发。

正式包形态固定为：

- Windows Release：`.zip`，解压根目录直接看到 `MAAUnified.exe`
- Linux Release：单个 `.AppImage`
- macOS Release：`.dmg`

### macOS 签名与 ad-hoc fallback

macOS 正式包优先使用 Developer ID 签名和 Apple notarization。签名材料由这些 GitHub Secrets 提供：

- `HGUANDL_SIGN_CERT_P12`
- `HGUANDL_SIGN_CERT_PASSWD`
- `HGUANDL_APPSTORE_KEYID`
- `HGUANDL_APPSTORE_KEY`
- `HGUANDL_APPSTORE_ISSUER`

材料齐全时，workflow 导入证书、正式 `codesign` app bundle；签名状态为 `developer-id` 时才继续执行 `notarytool`、`stapler` 和 `spctl`。材料不齐全或签名失败时，workflow 输出 warning，并由 `create-macos-app-dmg.sh` fallback 到 ad-hoc signing；ad-hoc 仍失败时继续产出 unsigned `.dmg`。

ad-hoc/unsigned 包未公证，发布说明必须标注该状态。用户可能需要在“隐私与安全性”中手动允许，或确认来源后执行 `xattr -dr com.apple.quarantine /Applications/MAAUnified.app`。

## 布局要求

CI 产物需要满足这些约定：

- CI 组装目录位于 runner 临时目录 `${RUNNER_TEMP}/maaunified-staging`
- Linux / macOS 托管应用和依赖在 `${RUNNER_TEMP}/maaunified-staging/bin/`
- MaaCore runtime、原生库和 `resource/` 在 `${RUNNER_TEMP}/maaunified-staging/` 根目录
- Windows 根目录入口是 `${RUNNER_TEMP}/maaunified-staging/MAAUnified.exe`
- Linux 根目录保留 `${RUNNER_TEMP}/maaunified-staging/MAAUnified` 和 `${RUNNER_TEMP}/maaunified-staging/MAAUnified.sh`
- Debug 包必须包含可运行应用、runtime、`resource/` 和 `debug/`

## CI 测试门禁

- Linux：baseline consistency gate 和完整 `MAAUnified.Tests`
- Windows：平台能力契约和 native smoke gate
- macOS：打包、签名状态输出、fallback warning、dmg 验证

Linux 负责整体功能和 baseline 相关门禁；Windows、macOS 只补各自平台侧验证，不重复跑整套功能测试。

门禁失败时，调试包和测试结果要能直接拿来复现。

## Baseline 与 Acceptance

baseline 是冻结的功能、配置键和 fallback 事实源。acceptance 基于 baseline 生成验收矩阵和案例。

机读源在：

- `src/MAAUnified/Compat/Mapping/Baseline/baseline.freeze.v1.json`
- `src/MAAUnified/Compat/Mapping/Baseline/acceptance.template.v1.json`
- `src/MAAUnified/Compat/Mapping/Baseline/baseline.schema.v1.json`
- `src/MAAUnified/Compat/Mapping/Baseline/acceptance.schema.v1.json`

可读投影在：

- `src/MAAUnified/Docs/testing/baseline.freeze.v1.md`
- `src/MAAUnified/Docs/testing/acceptance.checklist.template.v1.md`
- `src/MAAUnified/Docs/testing/avalonia-parity-matrix.md`

维护规则：

1. 改 baseline / acceptance 时，只改机读源。
2. Markdown 是投影，不是事实源。
3. JSON 变了，投影也要同步。
4. 投影没同步，相关测试应当失败。

变更说明沿用三类：

- `Data-only`
- `Schema`
- `Policy`

## 验收与 Waiver

Package A 冻结后，baseline 条目默认按 P0 处理。失败路径至少满足：

- 进程不崩溃
- UI 有可见反馈
- `debug/` 下有可定位日志
- 日志或证据能关联到 scope 和 case id

平台能力不可用时应降级并记录诊断，不要静默失败。

Windows GPU 探测遇到 `Indirect`、`Virtual`、`IDD` 一类 adapter 时，应跳过这些 adapter，继续枚举真实显卡。

只有真实阻塞且无法在当前包解决时，才允许 `Waived`。必须带上：

- `owner`
- `reason`
- `expires_on`
- `alternative_validation`

`expires_on` 超期后按门禁失败处理。

## 维护者发布流程

1. 先把改动合进 `MaaUnified`。
2. 回主仓更新 `src/MAAUnified` 的 submodule 指针。
3. 跑 Debug workflow，看调试包和测试结果。
4. 确认目标平台启动、布局和日志都正常。
5. 创建或确认 GitHub Release。
6. 跑正式发布 workflow，生成 Windows `.zip`、Linux `.AppImage`、macOS `.dmg`。
7. 检查 macOS job 日志：若签名状态不是 `developer-id`，发布说明中必须提醒用户该包未经过 Apple notarization。

Windows GUI 启动或 GPU 问题，优先看发布目录下的 `debug/windows-gpu-probe.log` 和 `debug/avalonia-ui-startup.log`。

## 相关文档

- [本地开发](./development.md)
- [贡献说明](./contributing.md)
- [中文文档入口](../README.md)
