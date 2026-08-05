# MAAUnified WPF 对齐审查 Prompt 清单

## 使用约定

- 审查目标为当前工作区中的 `src/MAAUnified`。
- WPF 基准固定为 commit `23ddd271b40a3d0c18de8a6ae8305357cc032e19`。需要读取旧实现时，应使用 `git show <commit>:src/MaaWpfGui/...`，不要用当前 WPF 分支替代冻结基准。
- 每个任务默认只审查和报告，不修改代码、配置、基线或测试。
- 文档、基线状态和现有测试只能作为线索，不能代替对实际运行路径的检查。
- WPF 没有对应实现的 macOS/Linux 特有能力、Avalonia 内部架构和发布形式，不作为“未对齐”问题。

## M01 冻结基线与兼容契约

```text
MAAUnified核对工作M01
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 中冻结基线与兼容契约相关的功能，确认当前工作区是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查 38 个功能项、12 个系统入口、300 个配置键、waiver、fallback 记录、字段映射和命令映射是否真实覆盖实际代码，是否存在只在文档或测试中声明 Aligned、实际运行路径却未实现的情况。请交叉检查 Compat/Mapping/Baseline、Docs/testing、FeatureManifest、配置读写入口和对应 UI/Service/Core 调用。若存在问题，请按严重程度列出，并说明 WPF 证据、MAAUnified 证据、实际行为差异、影响范围和缺失测试；先不要修复。
```

## M02 配置存储、导入与迁移

```text
MAAUnified核对工作M02
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的配置存储、旧配置导入、配置迁移和配置方案管理是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查旧 gui.json 与新格式识别、容错反序列化、默认值、未知字段保留、全局值与单配置值作用域、配置方案增删改名/排序/切换、当前配置选择、导入合并、导出、失败回滚、自动保存时机，以及 WPF 历史任务结构到 UnifiedTaskItem 的转换。重点关注 Application/Configuration、UnifiedConfigurationService、ConfigurationManagerView、SettingsPageViewModel 中的配置管理逻辑和 Compat 配置键。若存在问题，请按严重程度列出，并给出 WPF 与 MAAUnified 的代码证据、可复现输入、数据损失风险和缺失测试；先不要修复。
```

## M03 应用启动与生命周期

```text
MAAUnified核对工作M03
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 在 Windows 上可与 WPF 对照的应用启动和生命周期行为，确认是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查命令行参数、单实例行为、首次启动、配置加载顺序、资源初始化、启动公告/更新触发时机、主窗口创建与显示、未处理异常、关闭确认、退出清理、Core 释放和待应用更新处理。重点关注 App/Program.cs、App.axaml.cs、MAAUnifiedRuntime、AppLifecycleService、MainWindow 和 Compat/Runtime；不要把 WPF 没有的 macOS/Linux 打包布局当作 parity 缺陷。若存在问题，请列出证据、触发条件和用户影响；先不要修复。
```

## M04 MaaCore Bridge

```text
MAAUnified核对工作M04
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的 MaaCore Bridge 是否完整承接了 WPF MaaService/AsstProxy 使用 Core 的行为，并与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查 native 库加载、ABI/字符串编码、实例创建、资源加载、连接参数、实例选项、任务 append/set、start/stop、截图、回调注册与 JSON 解析、错误传播、取消、线程安全、重建实例和释放顺序。重点关注 CoreBridge、UnifiedSessionService、FeatureServices 中所有调用 IMaaCoreBridge 的路径。若存在问题，请按严重程度列出 WPF 调用语义、MAAUnified 差异、可能导致的任务参数或状态错误及缺失测试；先不要修复。
```

## M05 连接与会话生命周期

```text
MAAUnified核对工作M05
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的设备连接、模拟器检测和会话生命周期是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查连接配置和客户端类型联动、ADB 路径、地址候选生成与尝试顺序、自动检测/始终检测、BlueStacks 等特殊配置、超时、断线重连、截图测试、连接失败诊断、连接状态投影，以及 profile 切换后连接参数同步。重点关注 ConnectFeatureService、UnifiedSessionService、ConnectionGameSharedStateViewModel、ConnectSettingsView 和 WPF ConnectSettingsUserControlModel/MaaService。若存在问题，请列出复现场景、两侧代码证据和状态或配置影响；先不要修复。
```

## M06 主框架、导航与窗口生命周期

```text
MAAUnified核对工作M06
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的主框架、导航、主窗口和全局命令行为是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查任务/设置/高级页入口、页面初始化和延迟加载、导航状态保持、窗口尺寸与位置、最小化和关闭行为、关闭确认、强制显示、重启、退出、全局错误通道、页面错误通道，以及窗口标题和运行状态投影。重点关注 MainShellViewModel、MainWindow、RootPageHostViewModel、RootView、PageViewModelBase，并对照 WPF RootViewModel、RootView 和 MainWindowManager。若存在问题，请列出用户操作序列、WPF 预期、MAAUnified 实际行为和证据；先不要修复。
```

## M07 任务队列编辑与持久化

```text
MAAUnified核对工作M07
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的任务队列编辑、选择和持久化功能是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查默认任务列表和顺序、任务新增/删除/改名/拖动排序、启用状态、全选/反选、批量操作、一次性启用、选中项和设置面板同步、任务级配置保存、自动保存、防抖、profile 切换和关闭前 flush。重点关注 TaskQueuePageViewModel 的队列管理部分、TaskQueueItemViewModel、TaskQueueTaskPanelViewModel、TaskQueueView，并对照 WPF TaskQueueViewModel、TaskItemViewModel 和 TaskSettingsViewModel。若存在问题，请列出具体操作序列、持久化前后差异和缺失测试；先不要修复。
```

## M08 任务执行状态机

```text
MAAUnified核对工作M08
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的任务执行状态机是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查开始前自动连接、启用任务过滤和 append 顺序、Start/Stop/WaitAndStop、单任务执行、重复点击与并发保护、停止超时、连接失败、append 失败、Core start 失败、完成/失败/取消后的按钮状态、任务状态复位、后置动作触发条件和再次启动能力。重点关注 TaskQueuePageViewModel 的运行控制部分、TaskQueueFeatureService、SessionStateMachine 和 UnifiedSessionService，并对照 WPF TaskQueueViewModel/MaaService。若存在问题，请按严重程度说明状态迁移、复现步骤和两侧代码证据；先不要修复。
```

## M09 Core 回调、日志与状态投影

```text
MAAUnified核对工作M09
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 对 MaaCore 回调的处理、日志显示和运行状态投影是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查任务链开始/完成/错误、子任务识别、设施名称、关卡/掉落/招募/基建信息、资源下载进度、未知回调、被 WPF 抑制的日志、日志级别与颜色、任务卡状态、Overlay 日志、系统通知和外部通知触发条件。重点关注 TaskQueuePageViewModel 回调处理、TaskQueueLogCard/Entry、SessionStateUiProjection 和相关本地化资源，并对照 WPF TaskQueueViewModel、LogItemViewModel、LogCardItemViewModel。若存在问题，请提供回调 JSON 示例、WPF 预期文本/状态、MAAUnified 实际结果和缺失测试；先不要修复。
```

## M10 Fight 作战任务

```text
MAAUnified核对工作M10
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的 Fight 作战任务设置、持久化和 Core 参数编译是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查关卡输入和候选、活动关卡、上次关卡、理智药/源石/次数、掉落物与数量、剩余理智任务、代理指挥、三星和掉落策略、企鹅物流/一图流、服务器时区与每日重置、可见性和互斥联动，以及最终传给 Core 的 Fight 参数。重点关注 FightTaskModuleViewModel、FightSettingsView、FightTaskParamsDto、TaskParamCompiler 和相关配置迁移，并对照 WPF FightSettingsUserControlModel、FightTask、AsstFightTask。若存在问题，请列出具体配置组合、两侧最终参数差异和影响；先不要修复。
```

## M11 Infrast 基建任务

```text
MAAUnified核对工作M11
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的 Infrast 基建任务设置、持久化和 Core 参数是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查工作模式、设施选择与顺序、无人机用途、宿舍、信赖、源石碎片、补货、换班阈值、自定义基建配置文件、计划索引、文件校验、配置可见性和最终设施列表/参数。重点关注 InfrastModuleViewModel、InfrastSettingsView、TaskModuleSettings/InfrastParams、TaskParamCompiler，并对照 WPF InfrastSettingsUserControlModel、InfrastTask、AsstInfrastTask 和 CustomInfrastConfig。若存在问题，请列出输入配置、WPF 参数、MAAUnified 参数和运行影响；先不要修复。
```

## M12 Recruit、Mall 与 Award

```text
MAAUnified核对工作M12
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的 Recruit、Mall、Award 三类任务是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。Recruit 重点检查刷新、加急、招募次数、保留星级、三星处理、标签选择和确认等级；Mall 重点检查信用领取、购买优先级、黑名单、溢出策略和每日重置；Award 重点检查各奖励入口和启用条件。检查 UI 默认值、配置迁移、profile 隔离以及传给 Core 的参数。重点关注对应 ModuleViewModel、SettingsView、TaskParams/TaskModuleSettings 和 TaskParamCompiler，并对照 WPF 同名 UserControlModel、MaaTask 与 AsstTask。若存在问题，请分任务列出证据、参数差异和缺失测试；先不要修复。
```

## M13 Roguelike 与 Reclamation

```text
MAAUnified核对工作M13
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的 Roguelike 和 Reclamation 任务设置、联动、持久化及 Core 参数是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。Roguelike 重点检查主题、模式、难度、分队、开局干员、助战、投资、商店、烧水、月度小队、深入调查和主题特有限制；Reclamation 重点检查主题、模式、工具选择和循环行为。重点关注 RoguelikeModuleViewModel、ReclamationModuleViewModel、对应视图、DTO 和 TaskParamCompiler，并对照 WPF 同名 UserControlModel、RoguelikeTask/ReclamationTask 及 AsstTask。若存在问题，请列出具体组合、可见性/默认值差异、最终 JSON 差异和影响；先不要修复。
```

## M14 StartUp、Custom、SingleStep 与 UserDataUpdate

```text
MAAUnified核对工作M14
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 中能够在 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 找到对应行为的 StartUp、Custom、SingleStep 和 UserDataUpdate 任务。重点检查客户端启动、账号切换、启动等待、客户端类型、Custom 任务名称与参数、单步调试入口的可见条件、用户数据更新选项、启用状态、配置迁移和最终 append 顺序/参数。重点关注对应 ModuleViewModel、SettingsView、DTO、TaskParamCompiler 和 LegacyTaskSchemaConverter，并对照 WPF 同名任务模型和控件。WPF 基准中没有的纯调试或 Avalonia 扩展能力只需标注为不适用，不应判为未对齐。若存在实际 parity 问题，请列出证据和影响；先不要修复。
```

## M15 PostAction 后置动作

```text
MAAUnified核对工作M15
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的 PostAction 后置动作是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 在 Windows 上的行为对齐。重点检查执行条件、动作顺序、退出 MAA、退出模拟器、关机、重启、休眠、休眠后唤醒、关闭显示器、脚本/命令、延时、确认提示、取消、任务失败或手动停止时是否执行、配置迁移与危险操作保护。重点关注 PostActionModuleViewModel、PostActionFeatureService、PostActionPromptService、PostActionExecutorSupport 和对应视图/模型，并对照 WPF PostActionSetting、PostActionUserControl 和执行逻辑。不要把 WPF 没有的 macOS/Linux 实现差异列为 parity 缺陷。若存在问题，请列出触发条件和两侧行为证据；先不要修复。
```

## M16 Copilot

```text
MAAUnified核对工作M16
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的 Copilot 作业导入、列表管理、设置和执行流程是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查本地文件、剪贴板、maa://、网络作业和合集导入，重复项处理，任务列表增删排序，关卡类型识别，普通/悖论/保全派驻作业，编队、助战、循环次数、追加用户任务、自动加入列表、参数校验、执行/停止、回调日志和通知。重点关注 CopilotPageViewModel 两个 partial、CopilotItemViewModel、CopilotView 和 CopilotFeatureService，并对照 WPF CopilotViewModel、CopilotModel/SSSCopilotModel、CopilotView。若存在问题，请按场景列出输入、WPF 结果、MAAUnified 结果及证据；先不要修复。
```

## M17 Toolbox

```text
MAAUnified核对工作M17
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified Toolbox 中与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对应的全部工具是否对齐。分别检查公开招募识别、仓库识别、干员箱识别、抽卡识别、窥屏/画面预览和小游戏工具的输入、截图来源、识别参数、结果展示、复制/导出、历史设置、风险提示、运行/停止、错误反馈和配置键。重点关注 ToolboxPageViewModel、ToolboxAssetCatalog、六个 Toolbox 子视图和 ToolboxFeatureService，并对照 WPF ToolboxViewModel、ToolboxView、OperBoxData、MiniGameEntry 等。若某个 Avalonia 工具没有 WPF 对应项，只标记为不适用。若存在问题，请按页签列出证据、复现步骤和缺失测试；先不要修复。
```

## M18 StageManager 与资源工作流

```text
MAAUnified核对工作M18
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的 StageManager、关卡活动数据和资源工作流是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查关卡列表来源、活动开放时间、客户端/服务器区服差异、每日轮换、关卡名称与代码解析、资源更新、缓存刷新、失败回退，以及 Fight 页面和任务参数消费到的数据是否一致；同时检查 WPF 已有的企鹅物流/一图流上报开关和标识。重点关注 StageManagerFeatureService、StageManagerPageViewModel、ResourceWorkflowService、StageActivity 数据和相关测试，并对照 WPF StageManager、StageInfo、StageActivityInfo、ResourceUpdater 和 GameDataReportService。若存在问题，请列出具体日期/区服/关卡场景和两侧结果；先不要修复。
```

## M19 GUI、背景、主题与语言设置

```text
MAAUnified核对工作M19
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的 GUI、背景、主题和语言设置中与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对应的功能是否对齐。重点检查语言切换、主题及跟随系统、UI 缩放、背景图片路径/透明度/模糊/拉伸、日志时间格式、窗口标题滚动、通知开关、托盘相关 GUI 选项、设置即时预览、保存时机、取消或失败后的回滚、启动恢复和配置键兼容。重点关注 SettingsPageViewModel 的 GUI/Background 逻辑、GuiSettingsView、BackgroundSettingsView、UiLanguageCoordinator 和 App 主题应用，并对照 WPF GUI 配置及对应 UserControlModel/View。Avalonia 特有视觉实现不要求逐像素复刻，但用户可见语义和配置行为必须一致。若存在问题，请列出操作步骤和证据；先不要修复。
```

## M20 Game、Start、Performance 与 Timer 设置

```text
MAAUnified核对工作M20
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的 Game、Start、Performance 和 Timer 设置是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查客户端类型和语言、启动客户端/账号切换、自动开始、启动延时、ADB/截图/触控模式、GPU/OCR/CPU 性能选项、模拟器路径选择、定时任务槽、星期和时间判断、错过计划的处理、配置联动、默认值、可见性及保存时机。重点关注 SettingsPageViewModel.StartPerformance.cs、ConnectionGameSharedStateViewModel、四个设置视图、Platform 中 Windows GPU 能力，并对照 WPF 同名 SettingsUserControlModel/View、GUI/Timer 配置。WPF 没有的非 Windows GPU 后端不纳入 parity。若存在问题，请列出配置组合、WPF 预期和实际结果；先不要修复。
```

## M21 快捷键与开机启动

```text
MAAUnified核对工作M21
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 在 Windows 上的全局快捷键和开机启动功能是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查 WPF 历史快捷键整数/字符串格式导入、按键和修饰键规范化、动作映射、冲突检测、无效输入反馈、注册/注销与配置切换、启动/停止/显示隐藏动作，以及开机启动的启用、禁用、状态读取和失败反馈。重点关注 HotkeyConfigurationCodec、HotkeySettingItemViewModel、SettingsPageViewModel 热键/Autostart 逻辑、Platform Windows 实现，并对照 WPF MaaHotKeyManager、MaaHotKeyActionHandler 和 GUI 设置。macOS/Linux 回退不作为 WPF parity 项。若存在问题，请列出输入格式、动作差异和证据；先不要修复。
```

## M22 托盘与桌面通知

```text
MAAUnified核对工作M22
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 在 Windows 上的托盘和桌面通知行为是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查托盘图标显示条件、最小化到托盘、关闭到托盘、开始、停止、强制显示、隐藏托盘、Overlay、重启和退出菜单命令，菜单启用状态和文案；通知重点检查任务完成、任务错误、连接错误、点击行为、去重、UseNotify 开关和前后台状态。重点关注 MainWindow、MainShellViewModel、TrayIntegration、WindowsNotifyIconTrayService、DesktopNotificationService，并对照 WPF NotifyIcon、RootViewModel、MainWindowManager 和通知服务。其他平台托盘/通知后端不纳入 WPF parity。若存在问题，请列出操作序列和两侧证据；先不要修复。
```

## M23 Overlay、窗口绑定与 GPU 能力

```text
MAAUnified核对工作M23
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 在 Windows 上与 WPF 对应的 Overlay、目标窗口绑定、截图预览和 GPU 能力行为是否对齐，WPF 基准为提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19。重点检查目标窗口枚举和过滤、目标选择与持久化、失效目标恢复、Overlay 显示隐藏、置顶/跟随、日志内容、窗口尺寸位置同步、截图预览、PC 客户端窗口连接所需的截图和输入模式，以及 GPU 选择/驱动提示/失败回退。重点关注 OverlayAdvancedPageViewModel、OverlaySharedState、OverlayHostWindow、WindowsOverlayCapabilityService、WindowsGpuCapabilityService 和连接设置，并对照 WPF OverlayViewModel/OverlayWindow 与相关连接逻辑。macOS/Linux Overlay 降级不纳入 parity。若存在问题，请列出具体窗口场景和证据；先不要修复。
```

## M24 Remote Control 与 WebApi

```text
MAAUnified核对工作M24
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的 Remote Control 和 WebApi 中能够与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对照的功能是否对齐。Remote Control 重点检查端点、轮询间隔、任务获取、命令解析、开始/停止、状态上报、失败重试、并发和启停生命周期；WebApi 重点检查启用、监听地址、鉴权、任务定义存储、任务 CRUD、执行语义、响应状态和配置兼容。重点关注 RemoteControlFeatureService、RemoteControlCommandDispatcher、RemoteControlCenter、Settings RemoteControl、WebApiFeatureService/WebApiServer，并对照 WPF RemoteControlService 及已有 Web 接入行为。MAAUnified 新增但 WPF 没有的接口只需标为不适用。若存在问题，请提供请求/响应或命令样例及两侧证据；先不要修复。
```

## M25 外部通知与旧敏感配置兼容

```text
MAAUnified核对工作M25
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的外部通知功能和 WPF 旧通知配置兼容是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查 WPF 支持的各 Provider 是否齐全，Provider 选择、参数名称、默认值、必填校验、测试发送、保存、启用条件、任务完成/失败等事件过滤、消息标题与正文、失败反馈，以及旧 gui.json 中普通或加密敏感值能否正确导入并继续使用。重点关注 NotificationProviderFeatureService、ExternalNotificationProvidersPageViewModel、SettingsPageViewModel.RemoteNotification、SensitiveSettingsService 的旧值迁移入口，并对照 WPF ExternalNotificationService 和各 NotificationProvider。不要审查 WPF 没有的安全存储后端内部实现，只检查它是否破坏 WPF 配置兼容和运行行为。若存在问题，请按 Provider 列出证据和复现场景；先不要修复。
```

## M26 版本更新与公告

```text
MAAUnified核对工作M26
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的应用更新、资源更新和公告功能是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。重点检查 Stable/Beta/Nightly 通道、Nightly 警告、GitHub/镜像源、代理、MirrorChyan CDK、启动/定时/手动检查、跳过版本、自动下载、自动安装、下载进度、校验、待应用更新、失败恢复、更新弹窗；公告重点检查语言文件选择、拉取、缓存、版本去重、不再提醒、禁用公告和启动显示时机。重点关注 VersionUpdateFeatureService、AppUpdateWorkflowService、PendingAppUpdateService、SettingsPageViewModel.VersionUpdate/RemoteNotification、两个 Dialog，并对照 WPF VersionUpdateSettingsUserControlModel、VersionUpdateDialogViewModel、PendingUpdateApplier、AnnouncementDialogViewModel。非 Windows 安装机制不纳入 parity。若存在问题，请列出状态/配置组合和证据；先不要修复。
```

## M27 成就、About、Issue Report 与对话框

```text
MAAUnified核对工作M27
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 的成就、About、Issue Report 以及 WPF 已有对话框行为是否与 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 对齐。成就重点检查解锁状态、通知、自动关闭/禁用、列表筛选、导入导出、全部解锁/锁定；About 检查版本和链接；Issue Report 检查支持包内容、日志/截图处理、缓存清理和入口；Dialog 检查 Announcement、VersionUpdate、ProcessPicker、EmulatorPathSelection、Error、AchievementList、TextDialog 的按钮、默认选择、关闭结果、键盘行为和缩放。重点关注对应 ViewModel、FeatureService 和 Views，并对照 WPF 同名 ViewModel/View。Avalonia 新增且 WPF 没有的通用确认框只需标为不适用。若存在问题，请按功能列出复现步骤和两侧证据；先不要修复。
```

## M28 UI 基础、样式与本地化完整性

```text
MAAUnified核对工作M28
我正在开发 MAAUnified，现在正在做最终核对工作。请你检查 MAAUnified 中会影响 WPF 功能可用性的 UI 基础控件、样式、布局和本地化是否存在对齐问题，WPF 基准为提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19。重点检查所有 WPF 功能入口是否可见可达，控件绑定是否真实连接到命令/属性，禁用和可见条件、键盘操作、焦点、滚动、弹出层、选择列表、输入提交、窗口缩放、长文本和多语言布局，以及 zh-cn、zh-tw、en-us、ja-jp、ko-kr、pallas 的功能性文案键是否缺失或错误回退。重点关注 App/Controls、App/Styles、Root/Settings/TaskQueue/Advanced 视图、UiLocalizationResourceManager 和资源文件，并对照 WPF Views、Res/Styles、Res/Localizations。无需逐像素复刻 WPF，只报告导致功能缺失、误操作或语义变化的问题。若存在问题，请给出页面、语言/主题、操作路径和代码证据；先不要修复。
```

## Z01 最终汇总审查

```text
MAAUnified核对工作Z01
我正在开发 MAAUnified，现在正在做最终核对工作。现在已有各功能模块针对 WPF 冻结提交 23ddd271b40a3d0c18de8a6ae8305357cc032e19 的独立审查报告。请你汇总这些报告并进行跨模块复核：去重相同根因，识别互相矛盾的结论，检查配置导入到 UI、UI 到持久化、持久化到任务参数、任务参数到 Core、Core 回调到 UI/通知这一完整链路，确认没有因单模块边界而漏掉的问题。请重新验证高风险的连接、任务执行状态机、Copilot、配置迁移和更新流程，并运行适用的完整测试。输出按严重程度排序的最终问题清单、已确认无问题的范围、仍无法验证的风险和建议修复顺序；先不要修改任何代码。
```
