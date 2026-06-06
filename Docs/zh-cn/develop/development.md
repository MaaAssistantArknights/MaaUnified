# MAAUnified 本地开发

命令默认从 `MaaAssistantArknights` 主仓根目录执行，不要在 `src/MAAUnified` 子目录里直接跑。

`zsh` 里整段粘贴带 `# 1)` 这类注释的代码块，可能报 `parse error near ')'`：

- 只复制可执行命令；或
- 先执行 `setopt interactivecomments`

## 适用范围

这篇文档只管本地开发：拉子模块、构建、运行、测试、排查。

分支、提交流程见 [贡献说明](./contributing.md)。CI、发布和验收见 [CI、发布与验收](./ci-and-release.md)。

## 仓库与子模块

- `src/MAAUnified`：Avalonia 前端。
- `src/MaaUtils`：共享工具。
- MaaCore runtime、`resource/` 和平台启动入口都在主仓环境里验证。

日常联调建议始终从主仓跑，不要只在 `src/MAAUnified` 里单独看托管前端。

按用途分两种情况：

1. 只需要本地 build、运行、联调：直接 clone 官方 `MaaAssistantArknights` 即可。
2. 还需要提交改动、发 PR：先 fork `MaaAssistantArknights`，再从自己的 fork clone，并保留 `origin` / `upstream` 两个远端。

只需要本地 build 时，可直接使用官方仓库：

```bash
git clone --recurse-submodules -b dev-v2 --single-branch https://github.com/MaaAssistantArknights/MaaAssistantArknights.git
cd MaaAssistantArknights
```

准备贡献时，推荐从自己的 fork clone。下面的用户名和仓库名请按你自己的 GitHub 信息替换：

```bash
git clone --recurse-submodules -b dev-v2 --single-branch https://github.com/<GitHub 用户名>/<fork 仓库名>.git
cd MaaAssistantArknights
git remote add upstream https://github.com/MaaAssistantArknights/MaaAssistantArknights.git
git remote -v
```

预期主仓远端为：

```text
origin    https://github.com/<你的 GitHub 用户名>/<你的 MaaAssistantArknights fork 仓库名>.git (fetch)
origin    https://github.com/<你的 GitHub 用户名>/<你的 MaaAssistantArknights fork 仓库名>.git (push)
upstream  https://github.com/MaaAssistantArknights/MaaAssistantArknights.git (fetch)
upstream  https://github.com/MaaAssistantArknights/MaaAssistantArknights.git (push)
```

如果你已经 clone 了仓库，也可以直接补齐远端：

```bash
git remote set-url origin https://github.com/<你的 GitHub 用户名>/<你的 MaaAssistantArknights fork 仓库名>.git
git remote add upstream https://github.com/MaaAssistantArknights/MaaAssistantArknights.git
git remote -v
```

如果 `upstream` 已经存在，就不用重复添加。

首次同步子模块：

```bash
git submodule sync --recursive
git submodule update --init --depth 1 src/MAAUnified src/MaaUtils
git submodule status
```

子模块处于 detached HEAD 是正常状态；那表示它正停在主仓记录的提交上。

如果只是本地 build、运行或联调，停在这里即可。

如果还需要查看或同步 `MAAUnified` 子仓的日常集成分支 `dev`，要注意一个额外约定：主仓初始化出来的 `src/MAAUnified` 默认通常只抓取 `main`。先把 `dev` 加进 fetch 配置，再切本地跟踪分支：

```bash
git -C src/MAAUnified remote set-branches --add origin dev
git -C src/MAAUnified fetch origin
git -C src/MAAUnified switch dev || git -C src/MAAUnified switch -c dev refs/remotes/origin/dev
git -C src/MAAUnified config branch.dev.remote origin
git -C src/MAAUnified config branch.dev.merge refs/heads/dev
git -C src/MAAUnified status --short --branch
```

预期输出类似 `## dev...origin/dev`。如果你是按 [贡献说明](./contributing.md) 把官方 `MaaUnified` 配成了 `upstream`、自己的 fork 配成了 `origin`，把上面的 `origin` 替换成对应远端即可。

## 环境要求

- .NET 10 SDK
- `git`
- `python3`，Windows 可用 `python`
- `cmake`
- `ninja`
- 对应平台的 C/C++ 工具链

Linux 运行图形界面时需要 `DISPLAY` 或 `WAYLAND_DISPLAY`。没有图形会话时，应用应记录 `UiStartupNoDisplay` 并退出。

如果 CMake 报 generator 不匹配，优先重新执行带 `--fresh` 的 preset。

## 本地构建与运行

本地运行目录必须同时包含 Avalonia 应用、MaaCore runtime 和 `resource/`。

如果 `tools/maadeps-download.py` 提示 GitHub API rate limit：

- 先设置 `GH_TOKEN` 或 `GITHUB_TOKEN` 后重试；或
- 手动下载对应 triplet 的 `devel` / `runtime` 压缩包到 `src/MaaUtils/MaaDeps/tarball/`，再执行 `python3 src/MaaUtils/tools/maadeps-extract.py <triplet>`

### Linux x64

```bash
# 1) 还原 Avalonia app 依赖，并下载 Linux x64 运行时依赖
dotnet restore src/MAAUnified/App/MAAUnified.App.csproj
python3 tools/maadeps-download.py x64-linux

# 2) 构建并安装 MaaCore runtime（产物在 install/，含 resource/）
cmake --preset linux-publish-x64 --fresh -DINSTALL_PYTHON=OFF
cmake --build --preset linux-publish-x64
cmake --install build --config RelWithDebInfo

# 3) 发布 Avalonia app 到 publish/bin
mkdir -p publish
dotnet publish src/MAAUnified/App/MAAUnified.App.csproj -c Release -r linux-x64 --self-contained true --no-restore -o publish/bin

# 4) 合并 runtime 到发布目录根部，准备便携目录
cp -a install/. publish/
mkdir -p publish/config publish/data publish/cache publish/debug publish/update-packages
bash src/MAAUnified/CI/create-linux-appimage.sh publish publish MAAUnified x86_64

# 5) 启动本地打包产物
cd publish
./MAAUnified.AppImage
```

### Windows x64

```powershell
# 1) 还原 Avalonia app 依赖，并下载 Windows x64 运行时依赖
dotnet restore src\MAAUnified\App\MAAUnified.App.csproj
python tools\maadeps-download.py x64-windows

# 2) 构建并安装 MaaCore runtime（产物在 install\，含 resource\）
#    与主仓 CI 保持一致时，可额外传入 -DMAA_HASH_VERSION=debug-local
cmake --preset windows-unified-publish-x64 --fresh -DINSTALL_PYTHON=OFF
cmake --build --preset windows-unified-publish-x64 --config RelWithDebInfo
cmake --install build --config RelWithDebInfo

# 3) 发布 Avalonia app，并把 runtime 合并到运行目录
New-Item -ItemType Directory -Path publish -Force | Out-Null
dotnet publish src\MAAUnified\App\MAAUnified.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o publish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
Copy-Item install\* publish\ -Recurse -Force

# 4) 启动本地打包产物
.\publish\MAAUnified.exe
```

### macOS Intel (x64)

```bash
# 1) 还原 Avalonia app 依赖，并下载 macOS Intel x64 运行时依赖
dotnet restore src/MAAUnified/App/MAAUnified.App.csproj -r osx-x64
python3 tools/maadeps-download.py x64-osx

# 2) 构建并安装 MaaCore runtime（产物在 install/，含 resource/）
cmake --preset macos-publish-x64 --fresh -DINSTALL_PYTHON=OFF
cmake --build --preset macos-publish-x64
cmake --install build --config RelWithDebInfo

# 3) 发布 Avalonia app 到 publish/bin
mkdir -p publish
dotnet publish src/MAAUnified/App/MAAUnified.App.csproj -c Release -r osx-x64 --self-contained true --no-restore -o publish/bin

# 4) 合并 runtime 到发布目录根部，并生成启动入口
cp -a install/. publish/
bash src/MAAUnified/CI/create-unix-launchers.sh publish macos

# 5) 启动本地打包产物
./publish/MAAUnified
```

### macOS Apple Silicon (arm64)

```bash
# 1) 还原 Avalonia app 依赖，并下载 macOS Apple Silicon arm64 运行时依赖
dotnet restore src/MAAUnified/App/MAAUnified.App.csproj -r osx-arm64
python3 tools/maadeps-download.py arm64-osx

# 2) 构建并安装 MaaCore runtime（产物在 install/，含 resource/）
cmake --preset macos-publish-arm64 -DINSTALL_PYTHON=OFF
cmake --build --preset macos-publish-arm64
cmake --install build --config RelWithDebInfo

# 3) 发布 Avalonia app 到 publish/bin
mkdir -p publish
dotnet publish src/MAAUnified/App/MAAUnified.App.csproj -c Release -r osx-arm64 --self-contained true --no-restore -o publish/bin

# 4) 合并 runtime 到发布目录根部，并生成启动入口
cp -a install/. publish/
bash src/MAAUnified/CI/create-unix-launchers.sh publish macos

# 5) 启动本地打包产物
./publish/MAAUnified
```

## 运行目录与诊断日志

`publish/` 目录约定：

- Windows 入口在根目录：`publish/MAAUnified.exe`
- Linux / macOS 入口在根目录：`publish/MAAUnified`
- 托管应用和依赖在 `publish/bin/`
- MaaCore runtime、原生库和 `resource/` 在 `publish/` 根目录
- 诊断日志在 `publish/debug/`

常看这几个文件：

- `debug/avalonia-ui-startup.log`
- `debug/avalonia-ui-errors.log`
- `debug/avalonia-platform-events.log`
- `debug/config-import-report.json`
- `debug/windows-gpu-probe.log`

## 本地测试

日常快速模式：

```bash
dotnet test src/MAAUnified/Tests/MAAUnified.Tests.csproj -c Release --no-build --no-restore --logger "console;verbosity=minimal"
```

testhost 收尾偶发卡住时切到稳定模式：

```bash
dotnet test src/MAAUnified/Tests/MAAUnified.Tests.csproj -c Release --no-build -s src/MAAUnified/Tests/stable.runsettings --logger "console;verbosity=minimal"
```

首次运行、依赖变更或清理过产物后：

```bash
dotnet restore src/MAAUnified/Tests/MAAUnified.Tests.csproj
dotnet build src/MAAUnified/Tests/MAAUnified.Tests.csproj -c Release --no-restore
dotnet test src/MAAUnified/Tests/MAAUnified.Tests.csproj -c Release --no-build --logger "console;verbosity=minimal"
```

不要靠提高并行度处理卡住问题；切稳定模式即可。

按变更选测试范围：

- ViewModel、功能服务：相关测试类，提交前跑一次完整测试。
- 配置迁移、任务参数、远程控制、更新流程、多语言：完整 `MAAUnified.Tests`。
- 平台能力、GPU、托盘、热键、Overlay、启动流程：相关契约和回归测试。
- baseline、acceptance、parity matrix、字段映射：基线门禁加完整测试。
- 打包布局、runtime 合并、启动脚本：本地包验证，必要时看 CI 调试包。

## 提交前检查

1. 只提交本次改动需要的文件，不带上无关生成物和 submodule 指针。
2. 共享行为、配置、平台能力和基线相关改动，提交前跑完整 `MAAUnified.Tests`。
3. UI 启动、平台能力或 Windows GPU 问题，先看 `debug/` 日志。
4. baseline / acceptance 相关改动只改机读源，不手写投影文档。
5. 用 `git status --short` 和 `git diff` 收尾。

## 相关文档

- [贡献说明](./contributing.md)
- [CI、发布与验收](./ci-and-release.md)
- [模块边界与禁止事项](./module-boundaries.md)
- [架构说明](./architecture.md)
