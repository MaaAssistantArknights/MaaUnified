# MAAUnified 本地开发指南

本文说明 `MAAUnified` 的本地开发入口、常用测试方式与提交前检查要求。除特别说明外，命令均从主仓根目录 `MaaAssistantArknights` 执行。

如果当前目标是参与 `MAAUnified` 的协作开发、提交子仓 PR 或更新主仓 submodule 指针，建议与本文配合阅读 [贡献流程说明](./contributing.md)。

## 开发入口

`MAAUnified` 是基于 Avalonia 的跨平台图形前端，位于主仓 `src/MAAUnified`。日常开发建议统一从 `MaaAssistantArknights` 主仓根目录联调，这样可以同时验证 MaaCore runtime、`resource/` 资源目录、平台启动入口与打包布局，而不会遗漏集成问题。

首次从主仓工作时，应先同步必要 submodule：

```bash
git submodule sync --recursive
git submodule update --init --depth 1 src/MAAUnified src/MaaUtils
git submodule status
git -C src/MAAUnified rev-parse --short HEAD
```

Linux 环境启动图形界面前必须具备可用桌面会话，即存在 `DISPLAY` 或 `WAYLAND_DISPLAY`。无图形环境下应用应记录 `UiStartupNoDisplay` 并退出，不应产生未处理异常或 core dump。

## 本地运行与打包入口

完整运行目录需要同时包含 Avalonia 托管应用、MaaCore 原生库及 `resource/`。本地打包建议从主仓根目录执行，生成的目录约定如下：

- `staging/bin/`：Linux/macOS 的 `MAAUnified` 可执行文件与托管依赖。
- `staging/` 根目录：MaaCore 动态库、原生依赖、`resource/` 与平台启动脚本。
- Windows 入口：`staging/MAAUnified.exe`，可直接双击打开且不会弹出命令行窗口。
- Linux 入口：`staging/MAAUnified`，同时保留 `staging/MAAUnified.sh`。
- macOS 发布入口：`release/MAAUnified.app` 与 `release/*.dmg`。

Linux x64 本地构建示例：

```bash
# 先恢复托管依赖，并下载目标平台的 MaaCore 原生依赖。
dotnet restore src/MAAUnified/App/MAAUnified.App.csproj
python3 tools/maadeps-download.py x64-linux

# 再用 CMake preset 配置、编译并安装原生部分到 install/。
cmake --preset linux-publish-x64 --fresh -DINSTALL_PYTHON=OFF
cmake --build --preset linux-publish-x64
cmake --install build --config RelWithDebInfo

# 最后发布 Avalonia 应用，拷贝 install/ 内容，生成启动入口并直接验证运行。
dotnet publish src/MAAUnified/App/MAAUnified.App.csproj -c Release -r linux-x64 --self-contained true --no-restore -o staging/bin
mkdir -p staging
cp -a install/. staging/
bash src/MAAUnified/CI/create-unix-launchers.sh staging linux
./staging/MAAUnified
```

Windows x64 本地构建示例：

```powershell
# 先恢复托管依赖，并下载目标平台的 MaaCore 原生依赖。
dotnet restore src\MAAUnified\App\MAAUnified.App.csproj
python tools\maadeps-download.py x64-windows

# 再用 CMake preset 配置、编译并安装原生部分到 install\。
cmake --preset windows-publish-x64 --fresh -DINSTALL_PYTHON=OFF
cmake --build --preset windows-publish-x64 --config RelWithDebInfo
cmake --install build --config RelWithDebInfo

# 最后发布单文件 Windows GUI 入口，拷贝 install\ 内容后可直接双击 staging\MAAUnified.exe。
dotnet publish src\MAAUnified\App\MAAUnified.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o staging -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
Copy-Item install\* staging\ -Recurse -Force
```

macOS 本地构建示例，x64 与 arm64 分别使用对应 triplet、RID 与 CMake preset：

```bash
# 先恢复托管依赖，并下载目标平台的 MaaCore 原生依赖。
dotnet restore src/MAAUnified/App/MAAUnified.App.csproj
python3 tools/maadeps-download.py x64-osx

# 再用 CMake preset 配置、编译并安装原生部分到 install/。
cmake --preset macos-publish-x64 --fresh -DINSTALL_PYTHON=OFF
cmake --build --preset macos-publish-x64
cmake --install build --config RelWithDebInfo

# 最后发布 Avalonia 应用，拷贝 install/ 内容，并生成 .app 与 .dmg 做本地验证。
dotnet publish src/MAAUnified/App/MAAUnified.App.csproj -c Release -r osx-x64 --self-contained true --no-restore -o staging/bin
mkdir -p staging release
cp -a install/. staging/
bash src/MAAUnified/CI/create-macos-app-dmg.sh staging release MAAUnified-local-macos-x64 1.0.0 local
```

若同一 `build/` 目录曾使用不同 CMake generator，优先使用 `cmake --preset ... --fresh` 重新配置。

如果当前目标是参与 `MAAUnified` 代码修改和提交，而不是只在本地验证运行结果，可继续阅读 [贡献流程说明](./contributing.md)。

## 测试建议

日常快速模式优先使用已构建产物，命令从主仓根目录执行：

```bash
dotnet test src/MAAUnified/Tests/MAAUnified.Tests.csproj -c Release --no-build --no-restore --logger "console;verbosity=minimal"
```

若出现 testhost 收尾阶段卡住，切换到稳定模式：

```bash
dotnet test src/MAAUnified/Tests/MAAUnified.Tests.csproj -c Release --no-build -s src/MAAUnified/Tests/stable.runsettings --logger "console;verbosity=minimal"
```

首次测试或依赖变更后，可从 `src/MAAUnified` 执行完整构建测试：

```bash
cd src/MAAUnified
dotnet restore Tests/MAAUnified.Tests.csproj
dotnet build Tests/MAAUnified.Tests.csproj -c Release --no-restore
dotnet test Tests/MAAUnified.Tests.csproj -c Release --no-build --logger "console;verbosity=minimal"
```

不要通过加大并行度处理偶发卡住。该仓库的慢点通常来自并行测试收尾阶段，而不是单个测试本体。

## 提交前检查

提交前应完成以下检查：

1. 确认变更范围符合任务要求，尤其不要改动无关文档、生成物或 submodule 指针。
2. 对功能变更运行相关测试；对共享行为、配置、平台能力或基线相关变更运行完整 `MAAUnified.Tests`。
3. 涉及 UI 启动、平台能力、GPU 探测或 Windows 发布问题时，检查 `debug/avalonia-ui-startup.log`、`debug/avalonia-ui-errors.log` 与相关平台日志。
4. 涉及 baseline 或 acceptance 的变更，只修改机读源并通过生成同步投影文档，禁止手写投影结果。
5. 使用 `git status --short` 与必要的 `git diff` 检查最终改动，确保没有带入本地绝对路径、临时产物、个人化远端、个人化分支或本地环境命令。
