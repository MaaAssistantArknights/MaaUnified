# MAAUnified

`MAAUnified` 是 MAA 的跨平台图形前端，基于 Avalonia 与 .NET 构建。项目按独立仓库形态组织，并以 `submodule` 方式接入 `MaaAssistantArknights` 主仓，主仓路径为 `src/MAAUnified`。

本项目面向 macOS、Linux 与 Windows 的统一 GUI 演进，以现有 WPF 前端行为为主要参考，逐步收口配置语义、交互逻辑与平台能力。涉及 MaaCore、资源、协议与发布链路的内容，应优先保持与主仓既有约定一致。

## 项目定位

- 作为 `MaaAssistantArknights` 的跨平台 GUI 子项目维护。
- 以 Avalonia 实现主窗口、任务配置、平台能力封装与多语言资源。
- 通过 `CoreBridge` 调用 MaaCore，不在前端层重新定义核心协议。
- 在主仓构建链路中参与完整构建、发布与回归验证。

## 界面预览

截图文件建议放在 `Docs/zh-cn/assets/screenshots/` 下。当前先保留位置，后续补图时直接替换对应文件并取消注释即可。

<!-- ![MAAUnified 主界面](./Docs/zh-cn/assets/screenshots/main-window.png) -->

> 截图占位：主窗口与任务队列。

<!-- ![MAAUnified 设置页](./Docs/zh-cn/assets/screenshots/settings.png) -->

> 截图占位：设置页与平台能力配置。

<!-- ![MAAUnified 工具页](./Docs/zh-cn/assets/screenshots/toolbox.png) -->

> 截图占位：工具页或高级功能页。

## 构建与运行

`MAAUnified` 的完整运行依赖 MaaCore 原生库和 `resource/` 资源目录。建议从 `MaaAssistantArknights` 主仓根目录构建完整运行目录，而不是只对 `App/MAAUnified.App.csproj` 执行 `dotnet run`。

三平台构建指南请阅读：

- [安装、构建与运行](./Docs/zh-cn/develop/install-and-run.md)：位于中文开发文档 `develop/` 下，说明 Windows、Linux、macOS 本地构建与运行步骤。
- [CI 与发布流程](./Docs/zh-cn/develop/ci-and-release.md)：GitHub Actions、调试包、正式包与维护者发布流程。

Debug 包按平台产出一个完整可运行包，包含应用、MaaCore runtime、`resource/`、诊断日志目录和适合排障的符号信息。正式发布包面向用户分发：Windows 为 `.zip`，解压后根目录直接提供 `MAAUnified.exe`；Linux 为单个 `.AppImage`；macOS 发布形态保持既有 `.dmg`。

完整运行目录或从包中展开后的内容需要同时包含：

- `bin/` 下的 Avalonia 应用与 .NET 托管依赖。
- 运行目录根部的 MaaCore 原生库及其依赖。
- 运行目录根部的 `resource/` 资源目录。
- 平台启动入口，例如 Linux/macOS 的 `MAAUnified` 或 Windows 的 `MAAUnified.exe`。

## 从现有 Windows 版迁移配置

如果你已经在旧版 Windows GUI 中使用过 MAA，迁移时主要关注 `config/` 下这两个文件：

- `config/gui.new.json`
- `config/gui.json`

建议直接把这两个文件复制到 `MAAUnified` 运行目录的 `config/` 下。

`MAAUnified` 首次启动时，如果还没有 `config/avalonia.json`，会按 `gui.new.json -> gui.json` 的顺序自动导入旧配置。如果已经生成过 `avalonia.json`，可以在设置里的配置导入入口手动选择这两个旧文件重新导入。

日常迁移一般不需要手动处理更多文件；旧文件会作为导入来源保留，新的统一配置会写入 `config/avalonia.json`。如果想确认导入结果，可以查看 `debug/config-import-report.json`。

## 技术栈

- .NET `10.0`
- Avalonia
- C#
- xUnit

SDK 版本沿用本目录 [`global.json`](./global.json) 中指定的版本。

## 目录结构

- [`App/`](./App/)：应用入口、视图、样式、ViewModel 与 UI 服务。
- [`Application/`](./Application/)：配置、运行时编排、功能服务、诊断与多语言资源。
- [`Platform/`](./Platform/)：托盘、通知、热键、自启动、Overlay 等平台能力封装。
- [`CoreBridge/`](./CoreBridge/)：MaaCore 桥接层与调试替身。
- [`Compat/`](./Compat/)：兼容映射、历史字段与默认值适配。
- [`Tests/`](./Tests/)：单元测试、契约测试与回归测试。
- [`Docs/`](./Docs/)：项目文档索引、迁移说明、开发规范与基线材料。
- [`CI/`](./CI/)：CI 模板与发布辅助脚本。

## 文档入口

- [`Docs/README.md`](./Docs/README.md)：文档总索引。
- [`Docs/zh-cn/README.md`](./Docs/zh-cn/README.md)：中文文档入口。
- [`Docs/zh-cn/develop/install-and-run.md`](./Docs/zh-cn/develop/install-and-run.md)：中文开发文档下的安装、构建与运行说明。
- [`Docs/zh-cn/platform-capabilities.md`](./Docs/zh-cn/platform-capabilities.md)：平台能力与降级说明。
- [`Docs/zh-cn/develop/README.md`](./Docs/zh-cn/develop/README.md)：开发文档索引。
- [`Docs/zh-cn/protocol/README.md`](./Docs/zh-cn/protocol/README.md)：协议与数据约定索引。
