# MAAUnified CI、发布与验收

本页记录主仓 workflow、产物约定、baseline 门禁和维护者发布步骤。

本地构建、运行和测试见 [本地开发](./development.md)。

## CI workflow

主仓以这些 workflow 为准：

- `.github/workflows/ci-avalonia.yml`：调试包和合并前验证
- `.github/workflows/ci.yml`：官方 WPF/main Release Pipeline，负责普通版发布和 `MAARuntime-*` runtime artifacts，不直接正式发布 MAAUnified
- `.github/workflows/release-maaunified.yml`：正式 MAAUnified 附加/补发入口；beta release event 自动或手动从成功的 `ci.yml` run 下载 `MAARuntime-*` 后打包上传
- `.github/workflows/release-nightly-ota.yml`：复用普通 nightly alpha tag，构建并上传 runtime artifacts 后调用 `release-maaunified.yml` 上传 alpha MAAUnified 到 MaaRelease，再触发 mirrors

`src/MAAUnified/CI/` 下的文件只当模板或同步副本看，不作为实际入口。

## 打包矩阵与产物

| 名称 | Runner | RID | MaaDeps triplet | CMake preset | Debug | Release |
| --- | --- | --- | --- | --- | --- | --- |
| `windows-x64` | `windows-latest` | `win-x64` | `x64-windows` | `windows-unified-publish-x64` | 完整调试包 | 暂不发布 |
| `linux-x64` | `ubuntu-latest` | `linux-x64` | `x64-linux` | `linux-publish-x64` | 桌面便携 `.zip` | alpha/beta 桌面便携 `.zip` |
| `macos-x64` | `macos-latest` | `osx-x64` | `x64-osx` | `macos-publish-x64` | 完整调试包 | alpha/beta `.dmg` |
| `macos-arm64` | `macos-latest` | `osx-arm64` | `arm64-osx` | `macos-publish-arm64` | 完整调试包 | alpha/beta `.dmg` |

调试包保留日志和符号，用来复现和排障。正式包面向分发。

当前 MAAUnified 是普通版 alpha/beta release 的附加产物，不创建 MAAUnified-only release 或 channel。`ci.yml` 只产出普通版发布和 `MAARuntime-*` runtime artifacts；正式 MAAUnified 包由 `release-maaunified.yml` 从成功的 `ci.yml` run 下载 runtime artifacts 后打包上传。Beta 通过 beta release event 自动触发，也可手动补发；alpha/nightly 由 `release-nightly-ota.yml` 复用普通 nightly alpha tag，构建并上传 runtime artifacts 后调用 `release-maaunified.yml` 上传到 MaaRelease，再触发 mirrors。Stable 暂不附带 MAAUnified 包，Windows 也暂不加入 release。

当前 MAAUnified release 包形态固定为：

- Linux Release：`.zip` 便携包，解压根目录直接看到 `MAAUnified.AppImage`
- macOS Release：`.dmg` 安装镜像，按架构区分 x64/arm64

Linux Debug 包也使用同样的桌面便携 `.zip` 布局。`MAAUnified.AppImage` 是便携目录的桌面启动入口，不是单文件自包含包；必须先解压完整 `.zip`，再从解压后的根目录运行。

## 软件更新产物

MAAUnified 软件更新只接受 `MAAUnified-*` 包名，并按当前平台选择包：Linux 只选择 `.zip`，macOS 只选择 `.dmg`。不会把 `MAAComponent-OTA-*`、普通版 `MAA-*` 包或 Windows 包当作 MAAUnified 软件更新。

Stable 暂不发布 MAAUnified 包；当 stable release/version API 存在但没有当前平台的 `MAAUnified-*` 包时，MAAUnified 更新检查会返回无可用更新的安静状态，不提示用户安装普通版产物。Beta 可从普通版 beta release 选择对应平台的 `MAAUnified-*` 包。Nightly 继续映射到普通版 alpha API/tag，并继续受 `AllowNightlyUpdates` 显式开关控制，默认不暴露 nightly 选项。

这些约定只影响 MAAUnified 的 Avalonia 更新检查。WPF 更新逻辑和 WPF 产物命名不使用这套筛选规则，不受 MAAUnified 附加产物发布策略影响。

Mirror酱目前只支持资源更新；选择 Mirror酱作为软件更新源时，界面会提示 MAAUnified 暂未支持 Mirror酱软件更新，并要求切换到海外源/GitHub。
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
- MaaCore runtime、原生库和 `resource/` 在 `${RUNNER_TEMP}/maaunified-staging/` 根目录；MAAUnified release 包复用主仓构建出的 MaaCore runtime artifacts，不重新定义 WPF 或普通版 runtime 产物。
- macOS staging 根目录必须同时包含 `libMaaCore.dylib` 和 `libMaaAdbControlUnit.dylib`；后者来自 MaaFramework macOS 包的 `bin/libMaaAdbControlUnit.dylib`，需要与 `libMaaCore.dylib` 同目录。
- Windows 根目录入口是 `${RUNNER_TEMP}/maaunified-staging/MAAUnified.exe`
- Linux 根目录保留 `MAAUnified.AppImage`、`resource/`、原生库，以及 `config/`、`data/`、`cache/`、`debug/`、`update-packages/` 等便携目录
- Debug 包必须包含可运行应用、runtime、`resource/` 和 `debug/`

### macOS MaaFramework control unit

真实 MAAUnified release workflow 会在打包前准备 MaaCore runtime artifacts，并下载 MaaFramework macOS 包，把 `bin/*AdbControlUnit*` 复制到 `install/`。

本地手工处理时，打开 MaaFramework latest release，找到对应架构的 macOS 包：

- latest：https://github.com/MaaXYZ/MaaFramework/releases/latest
- Intel：`MAA-macos-x86_64-v*.zip`
- Apple Silicon：`MAA-macos-aarch64-v*.zip`

解压后复制：

```bash
cp -f MaaFramework-temp/bin/libMaaAdbControlUnit.dylib install/
test -f install/libMaaAdbControlUnit.dylib
```

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
6. 跑官方 `ci.yml` Release Pipeline，确认普通版发布和对应平台的 `MAARuntime-*` runtime artifacts 已成功产出；`ci.yml` 不直接正式发布 MAAUnified。
7. Beta MAAUnified 由 beta release event 自动触发 `.github/workflows/release-maaunified.yml`，也可手动指定成功的 `ci.yml` run 补发 Linux `.zip` 和 macOS `.dmg`。
8. Alpha/nightly MAAUnified 由 `.github/workflows/release-nightly-ota.yml` 复用普通 nightly alpha tag，上传 runtime artifacts 后调用 `release-maaunified.yml` 上传到 MaaRelease 并触发 mirrors。
9. Stable 暂不发布 MAAUnified。Windows 加入 release 前，需要重新启用对应 matrix；macOS 继续检查签名与 notarization 状态。

Windows GUI 启动或 GPU 问题，优先看发布目录下的 `debug/windows-gpu-probe.log` 和 `debug/avalonia-ui-startup.log`。

## 相关文档

- [本地开发](./development.md)
- [贡献说明](./contributing.md)
- [中文文档入口](../README.md)
