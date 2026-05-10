# MAAUnified 架构说明

`MAAUnified` 采用分层结构组织跨平台 GUI、应用编排、MaaCore 桥接、平台能力与兼容映射。整体目标是让 UI 可持续演进，同时把 MaaCore 原生边界、平台差异和 WPF 兼容口径约束在清晰模块内。

## 分层概览

主要目录职责如下：

- `App/`：Avalonia 应用入口、视图、样式、ViewModel、窗口与 UI 服务。
- `Application/`：应用用例、会话状态机、配置门面、功能服务、日志与诊断、多语言协调。
- `CoreBridge/`：MaaCore C API 桥接、回调归一化、调试替身与稳定 DTO。
- `Platform/`：托盘、通知、热键、自启动、Overlay、文件选择、GPU 能力与平台降级。
- `Compat/`：历史配置键、WPF 功能基线、任务类型目录、runtime 布局与迁移兼容。
- `Tests/`：单元测试、契约测试、基线门禁、UI 投影与平台能力回归测试。

依赖方向以应用层为中心收敛：

```text
App -> Application -> CoreBridge
App -> Application -> Platform
Application -> Compat
App -> Compat
Tests -> App/Application/CoreBridge/Platform/Compat
```

`CoreBridge` 与 `Platform` 不应反向依赖 `App`。`Compat` 提供兼容数据和 runtime 布局工具，不承载 UI 状态和业务编排。

## App 层

`App` 层负责 Avalonia 启动与可视化交互。`App/Program.cs` 创建 Avalonia `AppBuilder`，处理启动诊断、Linux 图形会话检查、软件渲染配置、待安装更新与启动异常记录。`App/App.axaml.cs` 在 Avalonia framework 初始化完成后创建运行时、注册全局异常处理、构造 `MainShellViewModel` 与主窗口。

`App` 层可以：

- 绑定 View 与 ViewModel，处理窗口生命周期和 UI 线程相关行为。
- 读取 `Application` 暴露的服务与状态。
- 通过 ViewModel 将用户操作转化为应用层用例调用。
- 展示平台能力降级、错误通道和诊断反馈。

`App` 层不直接读写配置文件，不直接调用 MaaCore 原生 API，也不绕过应用层自行维护任务执行状态。

## Application 层

`Application` 层承载主要应用语义。`MAAUnifiedRuntime` 是运行时服务聚合对象，包含配置服务、会话服务、资源流程、功能服务、平台能力服务、诊断与日志服务。`MAAUnifiedRuntimeFactory.Create` 负责将这些服务装配成一次应用运行所需的对象图。

核心职责包括：

- `UnifiedConfigurationService` 统一加载、迁移和保存 `config/avalonia.json`。
- `UnifiedSessionService` 与 `SessionStateMachine` 维护连接、任务执行和状态变化。
- 功能服务将设置页、任务队列、高级功能、远程控制、更新、成就、公告等能力封装成稳定接口。
- `UiDiagnosticsService` 与 `UiLogService` 记录错误、降级、导航耗时和可定位诊断信息。

`Application` 层通过接口依赖 `CoreBridge` 和 `Platform`，不得直接依赖 Avalonia 控件、窗口类型或具体平台 UI 实现。

## CoreBridge 层

`CoreBridge` 层负责将 MaaCore 原生接口转换为前端可消费的异步接口和 DTO。生产运行使用 `MaaCoreBridgeNative`，测试或调试可使用 `MaaCoreBridgeStub` 或测试内的 fake bridge。

该层的边界要求是：

- 原生句柄、回调细节、P/Invoke 约定只在 `CoreBridge` 内部扩散。
- 对上层暴露 `IMaaCoreBridge` 与明确的模型类型。
- 上层只关心连接、任务追加、启动、停止、资源和回调结果，不直接处理 MaaCore C API。

## Platform 层

`Platform` 层封装操作系统能力，并通过 `PlatformServiceBundle` 统一提供给应用层。默认装配由 `PlatformServicesFactory.CreateDefaults` 完成。

平台能力应遵循可降级原则：当托盘、通知、热键、自启动、Overlay、GPU 探测等能力不可用时，应返回可解释的 fallback 状态，记录诊断信息，并保持主窗口可交互。Windows GPU 探测中遇到 `Indirect`、`Virtual`、`IDD` 等虚拟或间接 adapter 时，应跳过该 adapter 并继续探测真实显卡，不能让单个异常 adapter 导致整次枚举失败。

## Compat 层

`Compat` 层维护与既有 WPF 前端、历史配置和打包 runtime 布局有关的兼容数据。它包含历史配置键、任务类型目录、WPF 功能基线、baseline 与 acceptance 机读源，以及 `RuntimeLayout`、`MacAppRuntimeSeed` 等 runtime 布局辅助逻辑。

配置迁移规则以 `config/avalonia.json` 为主写目标，旧文件 `config/gui.new.json`、`config/gui.json` 只读导入，不回写、不删除。

## Tests 层

`Tests` 层覆盖应用服务、ViewModel 投影、平台能力契约、配置迁移、基线同步与启动守卫。测试可以使用 fake bridge、fake platform service 或临时 runtime 目录模拟场景，但不应把测试替身泄漏到生产代码路径。

基线相关测试负责确保机读源、可读投影和验收模板保持同步。凡是影响功能对齐、配置键映射、平台 fallback 或 acceptance 案例的变更，都应让相应测试成为合并门禁。

## 运行时装配流程

运行时装配的主路径如下：

1. `Program.Main` 解析 runtime base directory，记录启动环境，处理待安装更新，并在 Linux 上检查图形会话。
2. `Program.BuildAvaloniaApp` 配置 Avalonia、字体、Skia、软件渲染与平台选项。
3. `App.OnFrameworkInitializationCompleted` 调用 `MAAUnifiedRuntimeFactory.Create` 创建运行时对象图。
4. `MAAUnifiedRuntimeFactory` 创建配置、日志、诊断、MaaCore bridge、会话状态机、平台服务、资源流程和各功能服务。
5. `App` 创建 `MainShellViewModel` 与 `MainWindow`，先让主窗口尽早可交互，再继续执行 shell 初始化、首屏等待、崩溃探测和平台初始化。
6. 退出时由 Avalonia desktop lifetime 触发 runtime dispose，释放托盘、热键、Overlay 与 MaaCore bridge。

该装配方式保证 UI 启动、配置迁移、平台降级和 MaaCore 通信各自有明确归属，同时允许测试替换 `IMaaCoreBridge`、`PlatformServiceBundle` 或临时配置目录。
