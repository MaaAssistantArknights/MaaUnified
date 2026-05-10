# 安装、构建与运行

本文面向需要本地验证、联调或打包 `MAAUnified` 的开发者和测试者。除特别说明外，命令均在 `MaaAssistantArknights` 主仓根目录执行。

如果你当前关注的是 GitHub Actions、调试包、正式发布包或维护者发布流程，可直接跳转到 [CI 与发布流程](./ci-and-release.md)。本文主要说明三平台本地构建、运行目录布局和运行时诊断入口。

普通用户通常直接下载 CI 或 Release 提供的构建产物，不需要自己构建本地运行目录。

## 发布目录结构

本地构建完成后，默认发布目录为 `publish/`。Debug 包会按平台上传一个完整可运行包，用于排障，内容应与该目录布局一致，并保留 `debug/` 日志和符号文件。可运行包应保持以下结构：

```text
publish/
  MAAUnified              # Linux/macOS 根目录启动入口
  MAAUnified.exe          # Windows 根目录 GUI 启动入口
  bin/
    MAAUnified            # Linux/macOS Avalonia 应用可执行文件
    *.dll                 # Linux/macOS .NET 托管依赖与运行时文件
  resource/
    ...                   # MaaCore 资源目录
  debug/
    ...                   # 启动、平台能力、配置导入等诊断日志
  *.dll / *.so / *.dylib  # MaaCore 原生库及其依赖，按平台出现
```

Windows 包默认提供根目录 `MAAUnified.exe` 作为可双击的 GUI 入口；Linux/macOS 仍使用 `publish/bin/` 加根目录启动脚本的布局。`publish/` 根目录始终保存 MaaCore runtime、`resource/` 资源目录和平台启动入口。不要只复制 `bin/`，否则应用可能能够启动但无法加载 MaaCore 或资源。

正式发布包的用户形态与调试包不同：

- Windows Release 是 `.zip`。解压后根目录直接存在 `MAAUnified.exe`，双击即可启动。
- Linux Release 是单个 `.AppImage` 文件。下载后如果无法直接运行，先执行 `chmod +x MAAUnified*.AppImage`，之后可双击或在终端执行该文件。
- macOS Release 保持既有 `.dmg` 形态。

MaaCore 是原生库，通常需要在目标平台构建，或使用 CI 产出的对应平台 runtime。因此，在一台机器上直接产出三平台完整包通常不可行；本地测试建议在对应系统上构建对应包。

## 拉取仓库和子模块

首次构建前需要同步 `src/MAAUnified` 与 `src/MaaUtils` 子模块：

```bash
git submodule sync --recursive
git submodule update --init --depth 1 src/MAAUnified src/MaaUtils
git submodule status
```

`git submodule update --init` 会检出主仓当前记录的子模块提交。此时子模块常处于 detached HEAD，这是 Git 子模块的正常状态，表示工作区与主仓记录的提交一致；它不表示已经切到 `MAAUnified` 远端最新分支。

如果只是复现主仓当前版本，不需要额外切分支。如果要继续开发 UI 子模块，再进入 `src/MAAUnified` 后切到目标开发分支。

## 通用依赖

三平台都需要：

- .NET 10 SDK；
- `git`；
- `python3` 或 Windows 上可用的 `python`；
- `cmake`；
- `ninja`；
- 对应平台的 C/C++ 构建工具链。

Avalonia 依赖通过 NuGet 还原，不需要另行安装 Avalonia runtime。

如果 CMake 报告 generator 不匹配，通常是同一个 `build/` 目录曾经使用过不同 generator。优先使用带 `--fresh` 的配置命令重新生成；仍失败时，可删除 `build/CMakeCache.txt` 与 `build/CMakeFiles/`，或清理整个 `build/` 目录后重试。

## Linux x64 本地构建

```bash
dotnet restore src/MAAUnified/App/MAAUnified.App.csproj
python3 tools/maadeps-download.py x64-linux

cmake --preset linux-publish-x64 --fresh -DINSTALL_PYTHON=OFF
cmake --build --preset linux-publish-x64
cmake --install build --config RelWithDebInfo

mkdir -p publish
dotnet publish src/MAAUnified/App/MAAUnified.App.csproj -c Release -r linux-x64 --self-contained true --no-restore -o publish/bin
cp -a install/. publish/
bash src/MAAUnified/CI/create-unix-launchers.sh publish linux
```

运行：

```bash
cd publish
./MAAUnified
```

本地 `publish/` 目录用于验证完整 Linux 运行布局。正式 Release 由 CI 将该布局封装为单个 `.AppImage` asset；如果只是下载正式包测试，不需要解压目录包，只需运行 `.AppImage`。

Linux 必须在可用图形会话中运行，至少需要 `DISPLAY` 或 `WAYLAND_DISPLAY` 之一。纯 SSH TTY、无桌面会话或未转发图形环境时，程序会以 `UiStartupNoDisplay` 退出，并写入 `debug/avalonia-ui-errors.log`；这属于受控退出，不应产生未处理异常或 core dump。

## Windows x64 本地构建

在 Windows 机器或 Windows runner 的 PowerShell 中，从主仓根目录执行：

```powershell
git submodule sync --recursive
git submodule update --init --depth 1 src\MAAUnified src\MaaUtils

dotnet restore src\MAAUnified\App\MAAUnified.App.csproj
python tools\maadeps-download.py x64-windows

cmake --preset windows-publish-x64 --fresh -DINSTALL_PYTHON=OFF
cmake --build --preset windows-publish-x64 --config RelWithDebInfo
cmake --install build --config RelWithDebInfo

New-Item -ItemType Directory -Path publish -Force | Out-Null
dotnet publish src\MAAUnified\App\MAAUnified.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o publish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
Copy-Item install\* publish\ -Recurse -Force
```

运行：

```powershell
.\publish\MAAUnified.exe
```

本地 `publish/` 目录应与 Windows Release `.zip` 解压后的根目录一致：`MAAUnified.exe` 位于根目录，用户不需要进入 `bin\` 或运行额外启动脚本。

## macOS x64 本地构建

在 macOS 机器或 macOS runner 中，从主仓根目录执行：

```bash
git submodule sync --recursive
git submodule update --init --depth 1 src/MAAUnified src/MaaUtils

dotnet restore src/MAAUnified/App/MAAUnified.App.csproj
python3 tools/maadeps-download.py x64-osx

cmake --preset macos-publish-x64 --fresh -DINSTALL_PYTHON=OFF
cmake --build --preset macos-publish-x64
cmake --install build --config RelWithDebInfo

mkdir -p publish
dotnet publish src/MAAUnified/App/MAAUnified.App.csproj -c Release -r osx-x64 --self-contained true --no-restore -o publish/bin
cp -a install/. publish/
bash src/MAAUnified/CI/create-unix-launchers.sh publish macos
```

运行：

```bash
cd publish
./MAAUnified
```

## 常用诊断文件

运行目录下的 `debug/` 用于保存诊断信息。测试包时优先查看：

- `debug/avalonia-ui-startup.log`：启动阶段记录；
- `debug/avalonia-ui-errors.log`：启动失败或受控退出原因；
- `debug/avalonia-platform-events.log`：平台能力与降级事件；
- `debug/config-import-report.json`：配置迁移与导入报告；
- `debug/windows-gpu-probe.log`：Windows GPU 探测过程与候选显卡，仅 Windows GPU 探测时生成。
