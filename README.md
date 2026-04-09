# MAAUnified

`MAAUnified` 是 MAA 的跨平台图形前端，基于 Avalonia 构建。

本项目的初衷，是为 MAA 提供一套可持续演进的跨平台 GUI。相较于继续在现有实现上局部修补，独立维护一套统一前端更适合承担 macOS 与 Linux 的图形界面，并为后续能力扩展预留清晰边界。

在项目规划上，`MAAUnified` 将长期与 WPF 前端并行演进，优先面向 macOS 与 Linux 使用场景，逐步完善功能与平台能力。代码结构自始即按独立仓库组织，并以 `submodule` 形式接入宿主仓库。

## 技术栈

- .NET `10.0`
- Avalonia `11.2.8`
- C#
- xUnit

SDK 版本沿用主仓库在 [`global.json`](./global.json) 中指定的版本，当前为 `10.0.201`。

## 构建与运行

独立仓库形态下：

```bash
dotnet restore App/MAAUnified.App.csproj
dotnet run --project App/MAAUnified.App.csproj
dotnet test Tests/MAAUnified.Tests.csproj -c Release
```

在 `MaaAssistantArknights` 宿主仓库中联调时，请先进入 `src/MAAUnified/` 后再执行上述命令。

## 本地构建（主仓库 + submodule）

适用场景：
- 你是从 `MaaAssistantArknights` 主仓开始 clone，并通过 `git submodule` 拉取了 `src/MAAUnified`
- 你希望在宿主仓内本地构建完整可运行目录，而不只是单独 `dotnet run` 前端

通用依赖：
- .NET `10` SDK
- `git`
- `python3` 或 `python`
- `cmake`
- `ninja`
- C/C++ 工具链

Linux 运行前提：
- 必须有可用图形会话（`DISPLAY` 或 `WAYLAND_DISPLAY`）
- 如果在纯 SSH TTY 等无图形环境启动，应用会直接退出

### 1. 拉取主仓并初始化 submodule

```bash
git clone https://github.com/MaaAssistantArknights/MaaAssistantArknights.git
cd MaaAssistantArknights

git submodule sync --recursive
git submodule update --init --depth 1 src/MAAUnified src/MaaUtils

git submodule status
git -C src/MAAUnified remote -v
git -C src/MAAUnified rev-parse --short HEAD
```

补充说明：
- 当前 `.gitmodules` 中 `src/MAAUnified` 的 URL 是 `https://github.com/MaaAssistantArknights/MaaUnified.git`，因此 GitHub Actions 与公共环境可以直接拉取官方仓
- 如果你本地开发时更习惯用 SSH push，可以在自己的工作区把 `src/MAAUnified` 的 `origin` 改成对应的 SSH 地址
- `git submodule update --init` 检出的是主仓当前锁定的 submodule 提交，`src/MAAUnified` 处于 detached HEAD 是正常现象
- 如果你只是想复现主仓当前状态，到这里即可；如果你要继续开发 `MAAUnified` 并切到 UI 仓最新主线，再执行：

```bash
git -C src/MAAUnified fetch origin
git -C src/MAAUnified switch main || git -C src/MAAUnified switch -c main --track origin/main
git -C src/MAAUnified pull --ff-only origin main
```

### 2. Linux x64 本地构建

```bash
cd /path/to/MaaAssistantArknights

# 1) 还原 C# 依赖
dotnet restore src/MAAUnified/App/MAAUnified.App.csproj

# 2) 下载 MaaDeps（原生依赖）
python3 tools/maadeps-download.py x64-linux

# 3) 构建并安装 MaaCore runtime（产物在 install/，含 resource/）
cmake --preset linux-publish-x64 --fresh -DINSTALL_PYTHON=OFF
cmake --build --preset linux-publish-x64
cmake --install build --config RelWithDebInfo

# 4) 发布 Avalonia app（Linux 包内置 .NET runtime）
dotnet publish src/MAAUnified/App/MAAUnified.App.csproj -c Release -r linux-x64 --self-contained true --no-restore -o publish

# 5) 合并 runtime 到发布目录
cp -a install/. publish/

# 6) 运行
cd publish
./MAAUnified
```

### 3. Windows x64 本地构建

```powershell
cd C:\path\to\MaaAssistantArknights

git submodule sync --recursive
git submodule update --init --depth 1 src\MAAUnified src\MaaUtils

dotnet restore src\MAAUnified\App\MAAUnified.App.csproj
python tools\maadeps-download.py x64-windows

cmake --preset windows-publish-x64 --fresh -DINSTALL_PYTHON=OFF
cmake --build --preset windows-publish-x64 --config RelWithDebInfo
cmake --install build --config RelWithDebInfo

dotnet publish src\MAAUnified\App\MAAUnified.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o publish
Copy-Item install\* publish\ -Recurse -Force
```

### 4. macOS x64 本地构建

```bash
cd /path/to/MaaAssistantArknights

git submodule sync --recursive
git submodule update --init --depth 1 src/MAAUnified src/MaaUtils

dotnet restore src/MAAUnified/App/MAAUnified.App.csproj
python3 tools/maadeps-download.py x64-osx

cmake --preset macos-publish-x64 --fresh -DINSTALL_PYTHON=OFF
cmake --build --preset macos-publish-x64
cmake --install build --config RelWithDebInfo

dotnet publish src/MAAUnified/App/MAAUnified.App.csproj -c Release -r osx-x64 --self-contained true --no-restore -o publish
cp -a install/. publish/
```

常见问题：
- 如果你之前在同一个 `build/` 目录切换过不同 generator，CMake 可能会报 generator 不匹配；优先使用 `cmake --preset ... --fresh`
- `publish/` 目录需要同时包含应用本体、MaaCore 动态库和 `resource/`；因此 `cp -a install/. publish/` 或 `Copy-Item install\* publish\` 这一步不能省
- 如果只想做 UI 层快速迭代，不依赖宿主仓打包产物，也可以继续使用上面的独立形态命令：`dotnet run --project App/MAAUnified.App.csproj`

## 配置约定

- 主配置文件：`config/avalonia.json`
- 自动导入条件：`avalonia.json` 不存在
- 导入顺序：`gui.new.json` -> `gui.json` -> 默认值
- 旧配置文件仅作为读取来源，不会被回写覆盖

## 暂未支持

- macOS / Linux 显卡能力支持：该部分涉及 MaaCore 边界，当前暂不实现
- macOS / Linux 关闭模拟器功能：仍需按平台分别适配，暂未完成调试
- MaaUnified 的软件更新：暂未开发更新相关功能

## 目录结构

- [`App/`](./App/)：应用入口、视图、样式、ViewModel 与 UI 服务
- [`Application/`](./Application/)：配置、运行时编排、功能服务、诊断与多语言资源
- [`Platform/`](./Platform/)：托盘、通知、热键、自启动、Overlay 等平台能力封装
- [`CoreBridge/`](./CoreBridge/)：MaaCore 桥接层与调试替身
- [`Compat/`](./Compat/)：兼容映射、历史字段与默认值适配
- [`Tests/`](./Tests/)：单元测试、契约测试与回归测试
- [`Docs/`](./Docs/)：迁移文档、基线说明、平台策略与映射说明
- [`CI/`](./CI/)：独立仓库与宿主仓库的 CI 模板

## 开发原则

- 以现有 WPF 行为为主要参考，逐步完成配置语义、交互逻辑与平台能力收口
- 变更范围尽量限定在 `src/MAAUnified/**` 内
- 涉及 MaaCore 边界的能力调整单独处理，不在前端层强行扩展

## 相关文档

- [`Docs/README.md`](./Docs/README.md)
- [`Docs/avalonia-migration.md`](./Docs/avalonia-migration.md)
- [`Docs/avalonia-parity-matrix.md`](./Docs/avalonia-parity-matrix.md)
- [`Docs/avalonia-platform-degrade-strategy.md`](./Docs/avalonia-platform-degrade-strategy.md)
- [`Docs/wpf-avalonia-field-mapping.md`](./Docs/wpf-avalonia-field-mapping.md)
