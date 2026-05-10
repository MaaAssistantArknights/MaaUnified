# MAAUnified CI 与发布流程

本文说明 `MAAUnified` 的三平台打包、主仓 `ci-avalonia`、`release-maaunified` 与维护者发布流程。除特别说明外，命令均从主仓根目录 `MaaAssistantArknights` 执行。

如果你当前需要的是 Windows、Linux、macOS 的本地构建步骤或运行目录说明，可先阅读 [安装、构建与运行](./install-and-run.md)。本文主要说明 CI 工作流、打包矩阵、发布产物和维护者发布链路。

## CI 入口

主仓维护两类与 `MAAUnified` 相关的 workflow：

- `.github/workflows/ci-avalonia.yml`：构建 Debug 包，用于 pull request、`dev` 分支推送和手动触发。Debug 每个平台只上传一个完整可运行包，保留诊断信息和符号，方便测试者直接复现问题。
- `.github/workflows/release-maaunified.yml`：构建正式发布包，并上传到指定 GitHub Release。Release 面向用户分发，Windows 上传 `.zip`，Linux 上传单个 `.AppImage`，macOS 保持既有 `.dmg`。

`src/MAAUnified/CI/ci-avalonia.yml` 与 `src/MAAUnified/CI/ci-standalone.yml` 是子仓或同步用模板；主仓实际运行以 `.github/workflows/` 下的 workflow 为准。

## 三平台打包矩阵

当前主仓打包矩阵包括：

| 名称 | Runner | RID | MaaDeps triplet | CMake preset | Debug 产物 | Release 产物 |
| --- | --- | --- | --- | --- | --- | --- |
| `windows-x64` | `windows-latest` | `win-x64` | `x64-windows` | `windows-publish-x64` | 完整 `.zip` 调试包 | `.zip`，解压根目录为 `MAAUnified.exe` |
| `linux-x64` | `ubuntu-latest` | `linux-x64` | `x64-linux` | `linux-publish-x64` | 完整 Linux 调试包 | 单个 `.AppImage` |
| `macos-x64` | `macos-latest` | `osx-x64` | `x64-osx` | `macos-publish-x64` | 完整 macOS 调试包 | `.dmg` |
| `macos-arm64` | `macos-latest` | `osx-arm64` | `arm64-osx` | `macos-publish-arm64` | 完整 macOS 调试包 | `.dmg` |

打包流程统一为：

1. checkout 主仓。
2. 初始化 `src/MAAUnified` 与 `src/MaaUtils`。
3. 安装 .NET 与 Python。
4. restore `src/MAAUnified/App/MAAUnified.App.csproj`。
5. 下载 MaaDeps。
6. 使用 CMake preset 构建并安装 MaaCore runtime 到 `install/`。
7. `dotnet publish` Avalonia app 到 Unix 的 `staging/bin`，Windows 则直接生成根目录 `staging/MAAUnified.exe`。
8. 将 `install/` 合并到 `staging/` 根目录。
9. 生成根目录平台启动入口。
10. 校验 `staging` 布局，并按 Debug 或 Release 目标转换为对应产物。

布局校验必须保证：

- Linux/macOS 托管应用和依赖位于 `staging/bin/`。
- MaaCore 动态库与 `resource/` 位于 `staging/` 根目录。
- Windows 包存在 `staging/MAAUnified.exe`，且不再附带 `MAAUnified.cmd`。
- Linux 包存在 `staging/MAAUnified` 和 `staging/MAAUnified.sh`。
- macOS 包生成 `.app` 与 `.dmg`，不发布 `MAAUnified.command`。
- Debug 包是每个平台一个完整包，必须包含可运行应用、MaaCore runtime、`resource/`、`debug/` 诊断目录和适合排障的符号文件。
- Release Windows 包是 `.zip`，用户解压后应能在根目录直接双击 `MAAUnified.exe` 启动。
- Release Linux 包是单个 `.AppImage` asset，必要时 `chmod +x` 后即可直接运行或双击启动，不再发布 Linux `.tar.gz` 作为正式包。
- Release macOS 包保持既有 `.dmg` 形态。
- 正式发布包不包含调试符号和非 runtime 头文件，除非平台签名或运行时工具链另有硬性要求。

## `ci-avalonia`

`ci-avalonia` 用于调试包和合并前验证。触发条件包括：

- 手动 `workflow_dispatch`。
- pull request 修改 `src/MAAUnified`、MaaCore、MaaUtils、resource、CMake 或 MaaDeps 相关路径。
- 推送到主仓 `dev` 且修改上述路径。

Debug 包的版本信息来自当前主仓 commit，形如 `debug-<short_sha>`，发布通道为 `debug`，诊断 profile 为 `full`。每个平台只产出一个完整调试包，包内保留启动日志、平台能力日志、测试诊断所需符号和 MaaCore runtime；测试者下载对应平台包后不需要再额外拼装 `resource/` 或原生库。

Linux job 会额外运行：

```bash
dotnet test src/MAAUnified/Tests/MAAUnified.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~BaselineContractTests|FullyQualifiedName~BaselineCoverageTests|FullyQualifiedName~BaselineRenderSyncTests|FullyQualifiedName~ParityMatrixSyncTests"
dotnet test src/MAAUnified/Tests/MAAUnified.Tests.csproj -c Release --no-restore --disable-build-servers -m:1
```

Windows job 会运行平台能力契约与 Windows native smoke gate。失败时会上传 `.trx` 测试结果，便于定位门禁失败。

手动触发示例：

```bash
gh workflow run "Build MAAUnified Debug Packages" --ref <主仓分支或 tag>
```

## `release-maaunified`

`release-maaunified` 用于正式发布包。触发条件包括：

- GitHub Release published。
- 手动 `workflow_dispatch`，传入现有 release tag。

手动触发示例：

```bash
gh workflow run "Release MAAUnified Packages" --ref <主仓分支或 tag> -f release_tag=<release tag> -f prerelease=true
```

workflow 会从 release tag 推导版本信息。若 tag 符合 `vX.Y.Z` 或 `X.Y.Z` 形式，使用该语义版本作为 `VersionPrefix`；否则使用 `1.0.0` 并将 tag 转为 release suffix。正式包发布通道为 `formal`，诊断 profile 为 `minimal`。

Release 产物约定如下：

- Windows：上传 `.zip`。用户解压后，包根目录必须直接存在 `MAAUnified.exe`，可双击启动，不需要进入 `bin/` 或运行 `.cmd`。
- Linux：上传单个 `.AppImage` asset。下载后如系统未保留可执行位，执行 `chmod +x MAAUnified*.AppImage`，之后可终端运行或在桌面环境中双击启动。
- macOS：保持既有 `.dmg` 发布形态；签名与 notarization 逻辑见下文。

macOS job 会检测签名与 notarization 所需 secrets 是否完整。secrets 完整时导入 Developer ID 证书并对 dmg notarize、staple；不完整时仍生成未签名 dmg。

## 本地构建与多平台产物

Windows、Linux、macOS 的本地构建命令、运行目录结构和平台差异说明见 [安装、构建与运行](./install-and-run.md)。

如果目标是生成多平台产物，而不是在当前系统复现单个平台问题，建议优先使用 GitHub CLI 触发主仓 workflow，由 GitHub Actions 统一产出 Windows、Linux 和 macOS 包。例如：

```bash
gh workflow run "Build MAAUnified Debug Packages" --ref <主仓分支或 tag>
```

正式发布包同样建议通过主仓 workflow 生成，而不是在单机环境手工拼装多平台产物。Release Windows `.zip`、Release Linux `.AppImage` 和 macOS `.dmg` 都应由 workflow 完成最终封装；本地命令主要用于复现布局、启动和诊断问题。

## 维护者发布流程

维护者发布应按“子仓合并、主仓更新 submodule 指针、触发主仓 workflow”的顺序进行。

1. 在 `src/MAAUnified` 中确认 UI 子仓改动已提交，并同步到维护分支。
2. 将 UI 子仓维护分支通过 PR 合并到 `MaaAssistantArknights/MaaUnified` 的主线分支。
3. 在主仓更新到目标基础分支后，同步 submodule 到 UI 子仓主线最新 commit：

```bash
git fetch origin
git switch <主仓目标分支>
git pull --ff-only

git -C src/MAAUnified fetch origin
git -C src/MAAUnified switch main || git -C src/MAAUnified switch -c main --track origin/main
git -C src/MAAUnified pull --ff-only origin main
git -C src/MAAUnified rev-parse HEAD
```

4. 在主仓检查并提交 submodule 指针：

```bash
git status --short
git diff --submodule
git add src/MAAUnified
git commit -m "feat(Avalonia): update MAAUnified"
git push origin <主仓目标分支>
```

5. 触发 Debug 包验证：

```bash
gh workflow run "Build MAAUnified Debug Packages" --ref <主仓目标分支>
```

6. 验证 Debug 包、测试结果和目标平台启动行为。Debug 每个平台只应有一个完整包，且包内诊断日志和符号足够用于排障。Windows GUI 启动或 GPU 探测问题优先查看发布目录下 `debug/windows-gpu-probe.log` 与 `debug/avalonia-ui-startup.log`。

7. 创建或确认目标 GitHub Release 后，触发正式发布包：

```bash
gh workflow run "Release MAAUnified Packages" --ref <主仓目标分支> -f release_tag=<release tag> -f prerelease=<true 或 false>
```

发布文档不得写入个人化远端、个人化分支、本地绝对路径或 GitHub CLI 默认仓库切换命令。需要指定仓库时，应在命令中显式使用当前维护目标，或在执行前由维护者自行确认 GitHub CLI 上下文。
