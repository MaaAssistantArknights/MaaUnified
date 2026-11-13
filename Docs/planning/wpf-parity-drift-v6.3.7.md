# MAAUnified WPF 对齐漂移：v6.3.7 → dev-v2

本文汇总 6 份模块级漂移报告（任务队列运行时与会话、设置与配置管理、任务参数与任务模型、Copilot 与工具箱、平台服务与基础设施、UI/本地化与视图行为），去重合并后给出统一的 P0/P1 执行口径。

- WPF 基线：`v6.3.7` = `05dfaeb678`（2026-02-28 发布）。MAAUnified 侧记录的冻结基线为 `23ddd27`（2026-03-02，`ci: add Xunit global using for MAAUnified tests`），两者 `src/MaaWpfGui` 树一致，本文的 WPF 现状一律以 `v6.3.7` 为准。
- 目标：`upstream/dev-v2` = `3badedda9b`（2026-11-05，v6.21.5-alpha 线，`perf: OCR 大升级 (#18246)`）。
- 对照实现：`src/MAAUnified` 当前 `feat/wpf-parity` 分支 HEAD `aa0abd4`。全部结论来自读代码（WPF 侧用 `git show <hash>:<path>`，MAAUnified 侧读工作区），未修改任何代码，未执行 `dotnet build` / `dotnet test`。

## 统计口径

- **行为漂移以两棵树 diff 为准，提交数只表示检索面。** `v6.3.7` 不是 `dev-v2` 的祖先（`git merge-base` = `d990fa97cf`），`v6.3.7..dev-v2` 的提交数会把 2021 年以来的历史一并计入。各模块报告据此采用不同口径：任务队列 1315、设置 945、平台服务 889、UI 2182 为 `git rev-list --count` 粗口径；任务参数改用 `--since=2026-03-04`（冻结日）得 161 条有效提交、Copilot/工具箱改用 `--since=2026-02-28`（发版日）得 148 条有效提交。
- **优先级定义**：P0 = 会造成状态卡死、配置/仓库数据丢失、静默失效或无法启动，须本周期完成；P1 = 用户可见行为差异，须排期；P2 = 观感与低频便利项，功能冻结后按反馈取。
- **去重口径**：6 份报告共 19 条 P0、46 条 P1，合并同类、升级与核实后为 **13 条 P0、35 条 P1**。合并明细：配置树形状 3 条（`GUI`→`Gui`/`Update`/`Timers` 形状、每档案分节、通知/更新/远控嵌套结构，同属 `382009b97b` + `f9c14e0be1`）合并为 P0-04；生息演算迁移 4 条（分散在 4 份报告）合并为 P0-06；停止链路 3 条（含远控停止语义）合并为 P0-01；原 P1 ｢统一启动拦截｣ 升级并入 P0-03。P1 合并同类 9 条、升级并入 P0 1 条、经代码核实剔除 1 条（配置档案删除确认，见末节）。
- **证据格式**：短 hash（10 位）+ WPF 文件路径；MAAUnified 侧给 `文件:行`。所有引用 hash 均已用 `git cat-file -e` 验证存在。
- 本文只收敛为执行口径，不复制原始 diff。

## 总览

| 模块 | 相关提交 / 文件 | P0 数 | P1 数 | 结论 |
| --- | --- | --- | --- | --- |
| 任务队列运行时与会话 | 1315 提交 / 7 文件（+5066 / −1228）；`TaskQueueViewModel.cs` +1759、`AsstProxy.cs` +2269、`Bootstrapper.cs` +1304 | 3 | 5 | 停止与完成语义整体落后一代：无超时收口、无运行归属判定、掉线仍可自动重连，是最高风险面 |
| 设置与配置管理 | 945 提交 / 82 文件；`SettingsViewModel.cs` 550、`Configuration/` 224（+3891 / −428）、`Helper/ConfigConverter.cs` +745 | 2 | 4 | 配置树整体重写后导入侧仍按 v6.3.7 形状读取，升级路径会静默丢设置；加载容错缺失会整档重置 |
| 任务参数与任务模型 | 161 有效提交 / 36 文件（+6756 / −2676） | 3 | 5 | 三种新任务类型缺位；公招、肉鸽参数的旧写法被 Core 兼容性掩盖，升级即丢语义 |
| Copilot 与工具箱 | 148 有效提交 / 20 文件（+4403 / −1126） | 2 | 11 | 列表 id 契约与工具箱仓库数据保护缺口最实际；设置档案化与像素画属决策/新特性 |
| 平台服务与基础设施 | 889 检索提交 / 124 文件（+12466 / −1575） | 3 | 5 | 通知触发面与更新完整性链路落后，缺 filelist 校验与失败跨启动可见 |
| UI、本地化与视图行为 | 2182 提交 / 288 文件（+26437 / −13755） | 0 | 5 | 本地化 key 空间已完全分叉（同名仅 39 个），宜以映射表承接而非改名；无独占 P0，条目并入其它模块 |

合计：P0 **13** 条、P1 **35** 条。总览列按主导模块计数，跨模块条目（P0-04、P0-06 等）只在主导模块计一次。

## P0 执行清单

按影响排序：先状态机与数据安全，再配置契约，最后任务类型与平台链路。

| 编号 | 行为 | WPF 变化 | MAAUnified 现状/文件 | 建议 |
| --- | --- | --- | --- | --- |
| P0-01 | 停止与完成判定收敛到运行归属（含停止超时强收） | 停止等待 60 s 未停即记 `TaskQueue.StopTimeout` 与 `Boot.RestartRecommendation`、`MarkRequiresRestart()`、强制 `SetStopped()`，此后所有启动入口被拦截；`AllTasksCompleted` 改看 `RunningState.Owner`（TaskQueue/Copilot/Toolbox/MiniGame），删除 `_mainTaskTypes` 白名单；结束脚本收敛单入口 + `Interlocked.CompareExchange` 每轮一次，Copilot 需 `CopilotWithScript` 才发；`Stop()` 返回 ｢是否在超时内确认停止｣。`0f4497da86`、`baa0032f70`、`91cdccbb17`、`caec74ff1f` | 未实现。`TaskQueuePageViewModel.StopAsync` → `ConnectFeatureService.StopAsync`（`Application/Services/Features/FeatureServices.cs:597`）→ `UnifiedSessionService.StopAsync`（`:1545`）全链路无 UI 侧超时、无强制复位；停止失败按 `MapRuntimeStatusToSessionState`（`:2164`）解回 Running，Core 卡死时 UI 永久显示运行中。完成判定仍用 `MainTaskModules` 白名单（`App/ViewModels/TaskQueue/TaskQueuePageViewModel.cs:134`、`:7996`）；结束脚本两处调用（`:5225`、`:8845`），无 once 语义，`CopilotWithScript` 只存不用，Copilot 目录无调用 | 加 60 s 等待 + 强收 + `RequiresRestart`，并在启动入口前置拦截；用 `RunOwnershipLease.Owner` 取代白名单；结束脚本单入口 + 每轮一次 + Copilot 归属门；远控 `StopTask` 复用同一路径，不要另写一套停止 |
| P0-02 | 掉线一律停止队列（移除自动重连），开始唤醒链豁免 | 删除 `RuntimeSettings.AutoRestartOnDrop` 与 ｢游戏掉线时自动重连｣ 设置项，旧键仅留作迁移清理；`OfflineConfirm` 与 `OfflineConfirmAfterBattle` 统一日志 + toast + `Stop()`；`taskchain == "StartUp"` 时不停。`01184e2484`、`e4897cd512` | 未对齐（更旧）。`QueueSpecialCallbackNotification`（`TaskQueuePageViewModel.cs:6736`）只在 `!IsAutoRestartOnDropEnabled()` 时停止，且只匹配 `OfflineConfirm`；无 `OfflineConfirmAfterBattle`、无 StartUp 豁免；`Compat/Constants/LegacyConfigurationKeys.cs:110` 仍保留 `Start.AutoRestartOnDrop` | 去掉开关分支恒停止；补 `OfflineConfirmAfterBattle` 与 StartUp 豁免；迁移时删除旧键，并清理 `Fight.AutoRestartOnDrop` 的独立持久化路径 |
| P0-03 | 任务链引用未定义任务 → 资源破损态 + 统一启动拦截 | `ProcSubTaskError` 新增 `ProcessTask/UnknownTask` 分支：`UnknownTaskInChain` 错误日志、`MarkResourceBroken()`（防弹窗期间起新任务）、异步修复引导、`StopTaskQueueAsync()`；`Bootstrapper.TryGetTaskBlockReason()` 顺序为资源损坏 > 需重启 > 内核未初始化，热键/托盘/远控入口统一汇入。`2e0ff15edd`、`a413ff2cfd`、`43965095df` | 未实现。全仓无 `UnknownTask` 分支，`grep -rn "ResourceBroken"` 零命中；仅 `_isCoreQueueCleanupRequired` 处理启动失败残留队列。拦截只有 `EnsureCoreReadyAsync`（`TaskQueuePageViewModel.cs:3818`）覆盖 TaskQueue 入口，Copilot/Toolbox/远控各自 gate | 引入资源破损态与 `UnknownTask` 回调分支；抽 `TryGetTaskBlockReason` 等价物（放 `MAAUnifiedRuntime` 或 `MainShellViewModel`）供各入口复用，远控 `LinkStart` 也要过此判定 |
| P0-04 | dev-v2 配置树形状导入兼容（`Gui`/`Update`/`Timers` + 每档案分节 + 通知/更新/远控嵌套） | `382009b97b` 整体重写配置树：`GUI`→`Gui` 并顺带把 15 个 `ExpanderXxx` 折叠态转成 `CollapesStates`；`VersionUpdate`→`Update` 且子键改名（`UpdateCheck`→`CheckOnStartup`、`UpdateAutoCheck`→`CheckOnSchedule`、`Package`→`UpdatePackage`），新增 `UpdateSource`/`GitHubToken`/`GitHubTokenExpiresAt`/`MirrorChyanCdk`/`AllowNightlyUpdates`；`Timers` 从字典改为具名键 + `List:[...]` 且无迁移器；连接/启动/运行时/通知/远控/三方/工具箱/Copilot 全部搬进每档案 `Gui` 分节。`53870a7a52` 继续加更新字段 | 未对齐（导入侧）。`Application/Configuration/GuiNewJsonConfigImporter.cs:110-113` 仍按 `"GUI"`、`"VersionUpdate"`、Timers 字典读取，`Timers.List` 命中 `NormalizeTimerKey`（`:308-331`）返回 null，只留一条 not recognized 警告、8 个定时槽全丢；档案内 `Gui` 对象整块写成 `profile.Values["Gui"]`（`:94`，`LegacyConfigValueMappings.NormalizeProfileKey:154` 无拆分规则），连接/启动/运行时设置随之全部失效 | importer 加形状探测：`Update` 视作 `VersionUpdate` 别名并补子键映射；`Timers.List[]` 按 `Id/Config/Hour/Minute` 逐项映射；在 `LegacyConfigValueMappings` 加 `Gui.<Section>.<Prop>` → 扁平键表（如 `Gui.ConnectSettings.Address`→`Connect.Address`）；未识别键写入 `ImportReport.Warnings` 而非静默。**此项必须先于其它通知/更新项** |
| P0-05 | 配置反序列化容错：单个非法字段不再整档重置 | `8c72cac105` 引入 `TolerantEnumConverter<T>` 与 `FaultTolerantRootConverter`，按 `JsonException.Path` 删除该属性后重试（≤20 次，多态数组元素按索引删），旧行为是任一字段失败即 `new Root()` 丢全部设置；`8ad75a3760` 增加恢复提示（｢目标配置…缺失，已从当前配置复制｣） | 未对齐。`UnifiedConfigurationService.LoadOrBootstrapAsync` 在解析失败分支（`:401`）直接 `new UnifiedConfig()` 重写为默认档（`:405-410`），不备份损坏文件，用户全部档案丢失 | 在 `AvaloniaJsonConfigStore.LoadAsync` 外层实现 ｢按 path 删节点重试｣；重写前另存 `avalonia.json.broken.<ts>`；恢复结果走启动可见提示（复用 `SchemaMigrationNotice`），不要只写日志 |
| P0-06 | 生息演算迁入工具箱（含工具箱设置分节与远控协议） | `66e003377c` 删除 `ReclamationTaskUserControl` 与 `BaseTask` 的 `[JsonDerivedType]`，改为 `Toolbox.MiniGame.Reclamation`，队列条目搬入且只留首个、按删除位置修正 `TaskSelectedIndex`；Toolbox 平铺设置拆 `Recruitment/OperBox/Gacha/Peep/MiniGame` 五节；远控新增 `Toolbox-Reclamation`，`LinkStart-Reclamation` 不再进任务队列批处理。`1c0440436a` 先改过策略逻辑与设置项 | 未对齐（结构差异 + 无导入映射）。生息演算仍是队列任务（`App/ViewModels/TaskQueue/TaskQueuePageViewModel.cs:130`、`Application/Configuration/LegacyTaskSchemaConverter.cs:41`、`:108`、`:683`）；工具箱无该入口；`GuiNewJsonConfigImporter` 完全不读 `Toolbox.*`；`Application/Services/RemoteControl/RemoteControlCommandDispatcher.cs:107` 只认 `LinkStart-Reclamation` | 结构可自定，但导入必须从 `Toolbox.MiniGame.Reclamation` 反向回填 Reclamation 参数；补 `Toolbox-Reclamation` 别名并保留旧名兼容；是否搬走队列入口属产品决策，与远程控制任务白名单、默认队列生成一同评估 |
| P0-07 | Copilot 战斗列表 ｢哪一项完成｣ 的判定依据 | 列表项新增 `id`（=列表序号）随任务下发，Core 在 `CopilotListLoadTaskFileSuccess` 回报 `id`，WPF 存为 `CurrentCopilotId`，只在 `model.Index == CurrentCopilotId` 的条目取消勾选并触发点赞；同一 `copilotId` 全部成功才点赞。`4843310bec` | 未对齐。列表下发不含 `id`（`App/ViewModels/Copilot/CopilotPageViewModel.WpfParity.cs:3456-3470`），回调忽略 `id`（`CopilotPageViewModel.cs:1934-1940`），完成时取 ｢第一个仍勾选的项｣（`:1848-1885`） | 下发 `id=item.Index`，回调记录 `CurrentCopilotId`，完成逻辑改为按 id 命中；否则中间项失败时会取消错误条目的勾选并点错赞 |
| P0-08 | 库存保持任务（一个队列条目展开为多条 Core 任务链） | 新增队列任务 `DepotMaintain`：先追加 1 条 `Depot` 识别链，再为每个计划追加 1 条 `Fight` 链（`drops`=缺口、`times`=int.MaxValue）；支持 AUTO 倍率、双芯片折算、临期药、按达成率排序、仅执行第一个库存不足计划、活动或资源全开放时整任务跳过；识别为空不覆盖缓存。`38b2e9e4f2`、`948f23f8ae`、`0e99c81442`、`e3ca235bbb`、`be9cc84fd2`、`a7ee2c2bf7`、`e7ef27745e`、`0302381585` | 未实现。`Application/Models/TaskModuleSettings.cs:8-20` 的 `TaskModuleTypes` 无该类型；`LegacyTaskSchemaConverter.cs:80-92` 对未知 `$type` 返回 false 并置 `IsEnabled=false`，dev-v2 条目会变成一行不可用的灰色任务 | 新增 task type + 参数 DTO，编译期展开为多条 Core 链（依赖 P1-05 的逐链状态）；未实现前导入器至少显式报 unsupported，避免静默禁用 |
| P0-09 | 肉鸽开局干员三顺位与逐位助战 | `core_char`(string) + 顶层 `use_support` → `core_char_list[{name,use_support}]`，按顺位招募、每位独立决定是否借助战；WPF 用 Root 级 converter 把旧 `CoreChar`/`UseSupport` 合成第 1 顺位。`f04fb1645b` | 部分对齐。`Application/Services/TaskParams/TaskParamCompiler.cs:1180` 只读 `core_char` 并仍发单值 `use_support`；`App/ViewModels/TaskQueue/RoguelikeModuleViewModel.cs:543`、`:562` 只有单个 `CoreChar`/`UseSupport`；导入只透传 `core_char`（`LegacyTaskSchemaConverter.cs:637-641`）。Core 仍兼容旧字段（`RoguelikeConfig.cpp:213-227`），不会立刻崩，但第 2/3 顺位在导入后丢失 | DTO 改列表并编发 `core_char_list`；导入取 dev-v2 `StartingOpers` 前三项；UI 做三行顺位编辑器（黑流树海主题另立项，属新主题适配而非参数搬运） |
| P0-10 | 公招保留词条、3 星许可保留、6 星确认 | 不再下发 `skip_robot`，改 `preserve_tags`（默认仅 `PreserveTagEnabled` 为真时下发）；新增 `level3_recruitment_permit_reserve`（1–999，仅启用时非 0）；新增 6 星自动确认（仅配置文件可改、界面禁用、招募时间固定 9:00，并新增 `recruitment_time["6"]`）；5 星时间固定 540；旧 `Level1NotChoose=true` → `PreserveTagEnabled=true`。`0289fa0381`、`3a7dc4c636`、`f24c0e237a` | 部分对齐。仍发 `skip_robot`（`TaskParamCompiler.cs:1040`、`:1118`，Core 标 6.11 弃用但兼容）；无 `preserve_tags`、无许可保留；`ChooseLevel6=false`（`:1046`）；导入器注释明确忽略 6 星（`LegacyTaskSchemaConverter.cs:348`），`Level1NotChoose` 直映 `confirm:[1]`（`:394`） | 补三个参数与 `recruitment_time["6"]`，做 `Level1NotChoose`→`PreserveTagEnabled` 迁移；6 星只需配置项，不开放 UI；保留 `skip_robot` 兼容读入 |
| P0-11 | 通知触发面扩大到任务级并统一入口 | 所有任务日志与通知收敛到 `NotificationService.PublishLog/Notify`；新增 `NotificationKind.{TaskComplete,TaskError,Stalled}`；任务链完成、`FightMissionFailedAndStop`、Copilot 完成/失败、界园未收集提示都会产生事件；Copilot 成为独立 `NotificationSource`，停滞提醒同时走系统通知与外部通知。`f9c14e0be1` | 未实现。只有 `AllTasksCompleted`/`TaskChainError`/`TaskStalled` 三个回调决定是否外发（`TaskQueuePageViewModel.BuildAutomaticExternalNotificationRequests`）；无 Copilot 来源、无战斗失败与未收集事件、无统一入口 | 抽 `INotificationDispatch`（source/kind/title/content + display 回调），先在 Asst 回调出口接 `TaskError`/`TaskComplete`，再补 Copilot 与 `CheckEncounter-Uncollected`；注意后者的 kind 是 `TaskComplete`，会顺带触发完成类外发 |
| P0-12 | 安装文件完整性检查 + 更新失败跨启动可见 | 启动对照 CI 生成的 `filelist.txt` 校验缺失文件（跳过 dll/exe/py，防路径穿越）；缺失时提示并可下载完整包注册为待安装；资源损坏期间强制完整包；`pending-update-failure.txt` 不再启动即删，跨启动保留、下次启动提醒并可自动修复。`6591943eaf`、`da79ff88ad`、`217db38484` | 未实现。无 filelist 校验、无失败状态落盘（`grep -i integrity` 只命中配置导入报告）；`Application/Services/VersionUpdate/PendingAppUpdateService.cs` 启动时应用一次，失败无痕迹 | 引入 filelist 校验（可由 CI 产出，缺失即跳过）；落等价失败标记并在启动提示 + 提供完整包修复入口；同版本完整包需允许重装 |
| P0-13 | 更新器委托、占用重试与回滚 | 涉及 dll 的更新交给独立 `MAA.Updater.exe`（优先用包内 updater）；文件被占用时重试 + 互斥量协调 + 失败回滚；支持拖入 `MAA-*-win-*.zip` / `MAAComponent-OTA-*-win-*.zip` 直接安装并做包名正则校验。`4ce5e9bf97`、`208d28d6fa`、`42473f1ede`、`63d39b42c1`、`112b242020` | 部分对齐。进程内解压 + 备份/回滚（`TryRollback`），无外部 updater、无互斥量、无包名识别（`IsManualInstallPackage` 只判手动包），仅 `removelist.txt` 语义 | 跨平台先保留进程内实现，补包名正则识别与同版本完整包修复；Windows 外部 updater 与互斥量单列评估，macOS/Linux 不适用须在实现说明中标注 |

## P1 执行清单

| 编号 | 行为 | WPF 变化 | MAAUnified 现状/文件 | 建议 |
| --- | --- | --- | --- | --- |
| P1-01 | 单次运行时长上限（到点自动停止） | `RuntimeSettings.EnableRunDurationLimit`（`bool?` 三态，null = 右键半选只生效一次）/`RunDurationLimitMinutes`（默认 240，clamp）/`RunDurationLimitExecutePostActions`；`RunningState.SetRunDeadline/TryConsumeRunDeadline/ClearRunDeadline`；到点后等肉鸽战斗结束、与定时启动/远控互斥、可选执行完成后动作、结束脚本只跑一次。`5a55fc8ac1` | 未实现。`grep -rn "RunDurationLimit"` 零命中；设置模型在 `App/ViewModels/Settings/SettingsPageViewModel.cs` 的 `TaskTimeoutMinutes` 同区与 `StartPerformanceSettingsSnapshot`（`:10857`） | 加三键 + 快照字段 + 运行循环截止检查，复用 `StopAsync` 与 `ExecuteAfterCompletionAsync`；三态在 UI 上可用 ｢仅本次｣ 复选项等价表达 |
| P1-02 | ｢日志停滞检测｣ 取代 ｢任务运行超时｣ | 删除 `RunningState.TaskTimeoutMinutes` 与 `TimeoutOccurred`，改 `StallTimeoutMinutes`（默认 30）+ `EnableStallTimeout` + `NotifyOutputActivity()`（每次 AddLog 重置）+ `StallOccurred(owner, initial, accumulated)`，首次用 StallTimeout、之后按 ReminderInterval 累加。`24639a0fdd`、`a637cdad89` | 未对齐（更旧）。`RunTaskTimeoutReminderLoopAsync`（`TaskQueuePageViewModel.cs:6833`）仍是 ｢从开始计时 + 固定 Reminder 间隔｣，读 `TaskTimeoutMinutes`/`ReminderIntervalMinutes`（`SettingsPageViewModel.cs:3631`、`:3644`）；`StallTimeout*` 只存在于 `LegacyConfigurationKeys.cs:265-266`，无 UI、无实现 | 计时器改为按日志活动重置，挂到 `AppendLogEntry`；补设置项并迁移三键到通知分组 |
| P1-03 | 任务出错时可跳过完成后动作 + 完成通知带错误清单 | 新增 `PostActions.SkipOnError = 1<<8`；`TaskChainError` 按 `Owner == TaskQueue` 过滤后 `RecordFailedTask`（按 core taskId 去重、名称快照带多链后缀）；`AllTasksCompleted` 用 `TaskCompletedWithErrors` 标题 + `TaskErrorSummary` 清单，外部通知正文附清单；跳过后仍还原 ｢仅当次｣ 勾选。`cda1c3b07f` | 未实现。`PostActionConfig`（`Application/Models/TaskModuleSettings.cs:595`）无该位；`ExecutePostActionAfterCompletionAsync`（`:8845`）硬编码 `WasSuccessfulTaskChain: true`；无错误清单文案与通知分支 | 加位、错误任务快照与标题分支；清单同时进日志与外部通知正文 |
| P1-04 | 完成后动作新增 ｢锁屏｣ 与独立 ｢退出 PC 客户端｣，倒计时保持唤醒 | `PostActions.LockScreen = 1<<9`（含 60 s 可取消倒计时）；`ExitPCClient = 1<<10`，与 `ExitArknights`/`ExitEmulator` 一起按当前连接方式过滤显示与执行，切换连接配置不再改写 `ExitEmulator`；退出类选项在第一个 `await` 前取值快照；倒计时用 `SleepManagement.CreateKeepAwakeRequest` 防休眠冻结。`d58cb0825b`、`3b4f06dd86`、`0b048d5e94` | 未实现。`PostActionType`（`Platform/Interfaces.cs:442`）只有 7 项，无二者；`App/ViewModels/TaskQueue/PostActionModuleViewModel.cs` 无对应属性，且 `ExitArknights` 与 `ExitEmulator`/`BackToAndroidHome` 互斥清空；倒计时走 `IPostActionPromptService`，无 keep-awake | 扩枚举 + 三选项解耦 + 按连接类型显隐；执行前快照退出目标；macOS 锁屏另有平台差异，可先做 Windows 与占位实现 |
| P1-05 | 队列条目持有多条 Core 任务 id 与逐链状态 | `TaskItemViewModel.TaskId`(int) → `TaskIds`(List<int>)；逐链状态聚合 Error > InProgress > 全 Completed > Skipped > Idle，`CompletedChainCount` 供任务栏进度；`SerializeTask` 返回 `IEnumerable<int>`；新增 `GetConfigByTaskId` 反查配置。`9e846609aa`、`caec74ff1f`、`be31b86dbd` | 部分对齐。`UnifiedSessionService` 已能把多个 Core task id 映射到同一 queueIndex（`TryResolveTaskIndexByCoreTaskId`、`SetTaskIdMapping`），但 `TaskQueueItemViewModel` 只有单个 `Status`，`UpdateTaskStatus`（`:8835`）用最后一条链的事件覆盖整行 | 先做逐链容器与聚合（P0-08、P1-16 的前置），再做任务栏按链计数 |
| P1-06 | 外部通知配置重构为 `Delivery` 分节 + 历史日志打包 | 旧 `SendWhenComplete/Error/Stalled/EnableDetails` 迁到 `Gui.ExternalNotification.Delivery`，新增 `SendAfterLogCount/NewLogCount`、`SendWhenContentMatches/Whitelist/Blacklist`、`IncludePreviousLogs/MaxEntries`（默认 2，详情模式 100）、`TimeMinutes/FilterPreviousLogs`、`SendBeforeScheduledStart`；历史按新到旧取前 N 条、单次匹配 50 ms 预算，默认不再附全量日志。`f9c14e0be1` | 部分对齐。仍是 v6.3.7 扁平三开关 + `EnableDetails`（`SettingsPageViewModel.cs:402-404`、`:1637-1675`）；`BuildExternalLogDetails` 在 `EnableDetails` 时拼全部 `OverlayLogs`，另一分支 `TakeLast(5)`；无条数/时间窗/白黑名单 | 配置层先补字段模型（键可保持扁平以避免再迁移），`EnableDetails` 映射为 `IncludePreviousLogs=true, MaxEntries=100`；投递管线交通知工作流 |
| P1-07 | 外部通知渠道多实例化，`Discord Webhook` 渠道取消 | `ExternalNotification.Configs` 变为可增删的渠道实例数组（同类型可多份、独立凭据），UI 用 `AddConfig`/`RemoveConfig`；渠道收敛到 9 个，`DiscordWebhook` provider 文件删除并入 Discord。`f9c14e0be1` | 未对齐。固定 10 个 provider 名 + `ExternalNotification.Enabled` CSV 启用列表；`NotificationProviderFeatureService.Providers` 仍含 `DiscordWebhook`，写回见 `SettingsPageViewModel.RemoteNotification.cs:403-410` | 渠道集合化可延后，但须先定 `DiscordWebhook` 是兼容别名还是并入 Discord；若按 dev-v2 形状写回，必须同步 `Configs` 数组，否则 WPF 侧读不到 |
| P1-08 | 单渠道发送健壮性（超长 / 自定义头 / 占位符转义） | Telegram 超 4096 上限时截断保留末尾（不切断代理对）并记录失败响应体；CustomWebhook 支持自定义 Headers 且真正设置 `Content-Type`，`{title}/{content}` 占位符补 JSON 转义，日志 URL 截到域名防 token 泄漏。`571356b47b`、`0f25895c6d`、`69b1ef2280`、`b8e93a047c` | 未对齐。Telegram 直接整条发送（`SendTelegramAsync`），超长 400 整条丢失；CustomWebhook 用 `request.Headers.TryAddWithoutValidation("Content-Type", …)`（content header 走 request 头不生效），占位符只做换行转义 | 截断 + 保留尾部；`Content-Type` 改走 `request.Content.Headers`；占位符用 `JsonEncodedText`/`JsonSerializer` 序列化后替换 |
| P1-09 | 定时设置：计划任务唤醒 + 定点前通知 | `Timers.ScheduledWakeUp` 按定时项全量重建 Windows 计划任务（失败弹框），`TimerList` 变化 debounce 重建、启动后台对账；`NotifyBeforeScheduledStart`/`ScheduledStartNotificationMinutes`（1..1439，clamp）。`0b40a31009`、`ba602828e6` | 未实现。只有 `ForceScheduledStart`、`ShowWindowBeforeForceScheduledStart`、`CustomTimerConfig`（`:3701-3718`、`:10350-10352`），无唤醒注册、无前通知 | 平台抽象 `IScheduledWakeUpRegistrar`（Windows 用 Task Scheduler，mac/Linux 明确声明不支持并禁用开关）；前通知复用现有管线，键名 `Timer.NotifyBeforeScheduledStart` |
| P1-10 | 三方服务设置分节 + 一图流 OpenAPI（含 Copilot `operbox_data_path`） | 新增 `Gui.ThirdParty`（`ReportToPenguin`/`PenguinId`/`ReportToYituliu`）与设置分节，接入一图流 OpenAPI 干员识别；自动战斗可启用一图流数据预检，启用但缺数据时拒绝启动；任务参数新增 `operbox_data_path`。`247db567f3`、`85fd6a9939` | 部分对齐。上报开关以任务参数形式存在（`LegacyTaskSchemaConverter.cs:291-294` 的 `report_to_penguin`/`report_to_yituliu`），无设置页分节；`grep -rn "yituliu"` 与 `grep -rn "OperBoxAssist"` 均零命中 | 先接 Yituliu OpenAPI + 落盘（跨干员识别模块），再接参数与启动前校验；导入时把 `Gui.ThirdParty.*` 映射为任务参数默认值 |
| P1-11 | 设置页分区顺序与折叠态持久化 | 15 个 `Gui.ExpanderXxx` 布尔改为 `SettingKey` 枚举 + `Gui.SettingOrders` + `Gui.CollapesStates`（存 ｢已折叠｣、读取取反）；支持拖动排序且即时生效；迁移器把 `ExpanderXxx=true` 转为未折叠、重命名去重 `NotificationSettings`、清理未知 `SettingKey`。`0bc04350c0`、`f7f23f0386`、`ff2d6cd2e7`、`b5412be3a1` | 未实现。分区顺序是静态数组 `SectionOrder`（`SettingsPageViewModel.cs:123-140`），`SettingsSectionViewModel` 仅 `Key/DisplayName`，无折叠态与排序记忆 | 沿用左侧导航，改为 ｢分区顺序 + 记住上次分区｣；键名沿用 `GUI.SettingOrders`/`GUI.CollapesStates` 以便直接吃 WPF 配置；排序变化立即重建 `Sections` |
| P1-12 | 配置持久化链路：串行保存、敏感值不进日志、变更事件细化 | `Save/SaveAsync` 改 `SemaphoreSlim(1,1)`；`IsDemoMode` 不落盘；逐键日志升到 Information 且记 old → new，但 `GitHubToken`/`MirrorChyanCdk` 只写 ｢已更新｣；事件 key 从 `{cfg}.TaskQueue` 细化到 `Root.Configurations.<cfg>.TaskQueue[i]<Name>(<Type>).<Prop>`，`Move` 单独发事件；`ConfigList`→`ConfigKeys`。`382009b97b`、`53870a7a52` | 部分对齐。已有 `_saveSemaphore` 与事务锁（`UnifiedConfigurationService.cs:48`），敏感项外置存储（`SensitiveSettingKeyRegistry` 31 项 + SQLCipher/系统钥匙串，强于 WPF）；无 demo 模式、无逐键变更事件（`RaiseConfigChanged:713` 整体通知），`GitHubToken` 未登记为敏感项 | 做 ｢设置实时同步到运行中任务/远控｣ 时补带 path + old/new 的事件；把 `GitHubToken` 加入敏感键表并同步计数断言测试 |
| P1-13 | GitHub Token 与更新凭据输入 | 新增 `Update.GitHubToken`/`GitHubTokenExpiresAt`；仅当 https + `api.github.com` + 默认端口 + 无 userinfo 才附 `Authorization`，token 过期时间解析并校验；配套 `Models/UpdateCredentialModel.cs`。`53870a7a52` | 未实现。`VersionUpdatePolicy`/`AppUpdateWorkflowService` 无 token 字段，GitHub 请求匿名（`grep GitHubToken` 零命中） | 若沿用 GitHub API，则补 token（存 SensitiveStorage）并严格限定只对 `api.github.com` 附带，顺带做凭据过期提醒 |
| P1-14 | 更新后启动行为：跳过自动运行 + 倒计时确认 + 隐藏 updater 输出 | `StartUpSettings.SkipStartupAutoRunAfterUpdate`（默认 true），updater 重启时透传 `--skip-startup-auto-run`；启动自动运行前 10 s 倒计时确认；新增 `Update.ShowUpdaterConsole`/`ShowUpdaterProgress`。`60cf643a2d`、`84b560d3cd` | 未实现（`grep SkipStartupAutoRun` 零命中）；`VersionUpdatePolicy` 无输出开关字段 | 在 `App/Program.cs` 与 `AppLifecycleService` 的启动链路补开关与倒计时；两字段纳入 `VersionUpdatePolicy` |
| P1-15 | 运行期异常中断与错误分类 | `ResolutionChanged` 连接回调 → `Connected=false` + 错误日志；`TaskChainError` 解析 `details.error`：`OutOfMemory` 专用文案、`GpuDeviceRemoved` 额外 `MarkRequiresRestart()`。`f4d8fba923`、`c3b21524ea`、`75273dfca8` | 未实现。`ResolutionChanged`、`GpuDeviceRemoved` 全仓零命中；无 `OutOfMemory` 任务错误分类（`Platform/WindowsGpuCapabilityService.cs:938` 只处理进程级异常） | 回调映射补三类；GPU 移除接 ｢需重启｣ 状态（与 P0-01 共用状态位） |
| P1-16 | 干员养成任务 | 新增队列任务 `OperProgress`：`plans:[{role,name,elite,skill_level,skill_mastery[3]}]`，0 值省略；支持多语言干员名导入与计划拖拽排序。`a412e6a294`、`0008f4cae1`、`a69cbae69b` | 未实现（全仓无 `OperProgress`），导入归 unsupported | 新 task type + DTO + 计划列表编辑器；参数生成独立，可与库存保持并行 |
| P1-17 | 更换主题任务 | 新增队列任务 `SwitchTheme`：`themes` 候选列表，多个时每次随机选一个，空列表跳过。`9c86aae937` | 未实现 | 成本最低的新任务类型，可先落地：list 编辑 + 一个 Core `SwitchTheme` 任务 |
| P1-18 | 基建换班新增参数 | 新增 `fiammetta_targets`（≤3、去空白去重）、`fiammetta_recovery_enabled`、`use_pinus_sylvestris`/`use_perception_information`/`use_worldly_plight`/`use_abyssal_hunter`（跨设施组合）、基建周计划 `UseWeeklySchedule` + `WeeklySchedule`。`c281b7aaca`、`59963a90cb`、`97d5e8ac38`、`3e570d1c23` | 未实现。`ConvertInfrast`（`:472-497`）只发 `drones/continue_training/threshold/dorm_*/reception_*/mode/filename/plan_index/facility` | 参数与设置页同步补；周计划复用刷理智已有的 `UseWeeklySchedule` 结构与当日跳过逻辑 |
| P1-19 | 奖励任务限时签到 | `Award` 新增 `signinevent`（仅通用横版版式，不含新春）。`19699d8483` | 未实现。Award 参数只有 `award/mail/freegacha/orundum/mining/specialaccess`（`TaskModuleSettings.cs:552-580`） | 加 bool 参数与勾选项，Core 参数名 `signinevent` |
| P1-20 | Copilot 导航名覆盖字段 | `stage_name` 改名 `nav_name_override`，仅用户覆盖关卡名时下发；Core 只读 `nav_name_override`（`src/MaaCore/Task/Interface/CopilotTask.h:23`）；条目移除 `TabIndex`、新增 `IsNavNameOverride`。`c2021e694a` | 未对齐。仍下发 `["stage_name"]=item.Name`（`WpfParity.cs:3456-3470`），`CopilotItemViewModel.cs` 无 `IsNavNameOverride` | 改字段名并只在覆盖时发送；需实测 Core 对未知 `stage_name` 的行为（`MEO_JSONIZATION` 走 `MEO_OPT`，预期忽略） |
| P1-21 | Copilot payload 归一化（技能默认值、精英化推导、动作互斥、`role`） | `Oper.Skill` 默认 1 → 0（0 = 不指定技能）；技能 3 对阿米娅放行（`char_002_amiya`）；按 skill/技能等级/模组推导 `requirements.elite`（缺失补齐、过低抬高并输出 `Copilot.EliteEmpty`/`EliteAdjust`）；Skill/Retreat/BulletTime/SkillUsage 同填 `name`+`location` 时清 `name`/`role` 保坐标，Click 同填 `rect`+`location` 时清 `location`；协议新增 `role`；`module` 允许 5（β）。`b2d8591974`、`d9457ebd91`、`92bf7c7962`、`5994a8db49`、`62f60b9b98`、`1ca41b0847`、`cc4eae6e42`、`6341749473` | 部分对齐（停在 v6.3.7 语义）。缺省按 1、无阿米娅例外、无 elite 推导（`WpfParity.cs:3669-3712`）；自定义干员 skill 夹到 1..3、module 夹到 0..4（`:1682`、`:3004`） | 把 `ApplyUnsupportedSkillCorrections` 扩成 `CanonicalizeCopilotPayload` 集中处理并回写告警；改写必须保持未知字段原样透传。elite 推导直接影响自动编队达标，优先于纯提示项 |
| P1-22 | 战斗列表校验依据：页签归属 → 作业内容类型 | `d6601334f2` 起不再检查列表项 `tab_index`，改为读作业 JSON 判定类型（`GetCopilotTypeAsync`）：未知类型只记错误日志不阻断，混用两种具体类型才拒绝；`TabIndex` 字段随后删除。`d6601334f2`、`c2021e694a` | 未对齐（实现的是 v6.3.7 规则）。要求每项有 `TabIndex` 且与当前页签一致，缺失报 ｢旧版本条目｣（`WpfParity.cs:3083-3170`）；会把 dev-v2 写出的、已无 `tab_index` 的 `Copilot.TaskList` 整体判非法而拒绝启动 | 改为内容判定，`TabIndex` 仅保留作 UI 提示 |
| P1-23 | 作业站神秘代码格式 | 支持 `maa://<id>`、`prts://<id>`、`prts://s<id>`、`s<id>`、纯数字，按前缀从长到短匹配。`72213def33` | 未对齐。仅 `maa://` + 纯数字（`WpfParity.cs:21`、`:2142-2165`） | 扩展 `LooksLikeCopilotCodeSource` 与解析；作业集代码直接进 `LoadCurrentFromClipboardSetAsync` |
| P1-24 | 作业版本门控允许两段版本号 | `minimum_required` 解析加 `SemVersionStyles.OptionalPatch`，｢6.3｣ 视为 6.3.0 通过。`25481d16ca` | 未对齐。`SemanticVersion.TryParse` 强制三段（`Application/Services/Features/CopilotVersionGate.cs:205-211`） | 接受 2 段补 0，仍拒绝 1 段与带空白值 |
| P1-25 | Copilot 设置的存储归属（档案隔离） | `TaskList/UserAdditional/EnableUserAdditional/SupportMode/UseFormation/SelectFormation/LoopTimes` 迁入每档案 `CurrentConfig.Copilot`，旧扁平 global key 迁移后 `DeleteValue`。`382009b97b`、`5b39509df6` | 未对齐。仍读写扁平 global key（`WpfParity.cs:828-845`，`LegacyConfigurationKeys.Copilot*`），列表另用 scope `Config.Copilot.CopilotTaskList`（`CopilotPageViewModel.cs:40`） | 先定方向：跟随档案化则需迁移 + 去重；保持全局则至少保证与 dev-v2 配置互导时不丢键 |
| P1-26 | 自动像素画（整套新特性） | 图片 → 点阵转换与提交：文件/拖放/剪贴板取图、4 字以内文本、FitMode(Crop/Contain/Stretch)、DitherMode(Floyd/Atkinson/Illustration)、对比度/亮度/饱和度、去白、预览缩放拖动、可调点击间隔、运行中锁参数、OKLab 感知距离选色；提交 `AsstPixelPaint(groups, swipe, delay)`。`1f39c29587`、`d74158a121`、`03386a0ed5`、`cedb8070c2`、`a77ee625b1`、`2272f3d197`、`3c8080847b` | 未实现（`grep PixelPaint` 零命中） | 纯新增特性，跨 UI + 算法，独立里程碑；注意 `TaskParamCompiler.CompileCustom`（`:1652-1686`）目前只输出 `task_names`，会静默丢弃附加 `params`，实现时需补透传 |
| P1-27 | 小游戏选择器与新增小游戏任务 | 小游戏改分类列表（当前活动/常驻）+ 提示 + 日志；启动流程拆通用骨架 + 分派；新增 ｢自动升级潜能｣ 与 ｢活动商店黑名单｣（按客户端语言预置关键词）。`a105eccd05`、`a269cf5525`、`8a6ca5f276`、`1bc83ba6e6`、`e7a6f109fe` | 部分对齐。小游戏条目由 `StageActivity` 数据 + 内置 5 项驱动（`ToolboxAssetCatalog.cs:493-560`），平铺无分类（`ToolboxPageViewModel.cs:424`），无 AutoRaisePotential/EventShop 分派 | 先补分类与分派（共用 `StartSelectedMiniGame` 骨架），再接黑名单（只影响 EventShop 参数） |
| P1-28 | 干员识别：数据来源与导出/展示 | 支持从一图流 OpenAPI 拉取并落盘；导出 JSON/Markdown/CSV；已拥有卡片展示技能练度/模组，导出补主技能等级。`247db567f3`、`131f2d248d`、`86e58bda95` | 部分对齐。仅单一文本导出（`ToolboxPageViewModel.cs:4635` `BuildOperBoxExportText`），无 API 拉取、无技能/模组徽标 | 先做多格式导出（单文件、低风险）；OpenAPI 依赖 P1-10 |
| P1-29 | 仓库识别纳入五种基础材料 | 识别并统计 源石/合成玉/龙门币/赤金/采购凭证；排除表只剩家具 `3401`、碳 `3112-3114`、经验 `5001` 及非纯数字 ID，且只在掉落增量路径生效。`1dda1ec985` | 未对齐且内部不自洽。`ExcludedDepotItemIds` 额外排除 `4001`/`4003`/`4006`（`ToolboxPageViewModel.cs:88-98`），该集合只在掉落增量里判（`:2928`），识别解析不过滤（`:2856-2895`、`:4724-4746`）→ 三类材料 ｢显示但不累加｣ | 从排除表删掉 `4001/4003/4006`；顺手把排除集合同时用于识别解析，避免两套语义 |
| P1-30 | 空库存保护（识别与掉落两条写入路径） | 无库存数据时停止刷取；识别结果为空不再覆盖缓存；掉落增量在基线为空时整段跳过。`0302381585` | 未对齐。识别回调无条件 `ReplaceCollectionItems` + `PersistDepotAsync`（`ToolboxPageViewModel.cs:2808-2855`，解析为空时同样清空落盘）；增量既无空基线判断，还会为未识别物品新建条目（`:2898-2955`） | 识别为空且已有缓存时保留旧值并告警；增量在无库存快照时直接返回。否则一次识别失败会清空用户仓库，从未识别过的用户会得到伪造的部分库存 |
| P1-31 | 本地化 key 点分命名收口 + CI 校验脚本 | 五语言 2241 key 统一 `Domain[.Topic].Name`（提示 `.Tip`、标题 `.Title`）并字典序排序；`TaskType`/动作枚举到 key 改由 `LocalizationKeyExtensions` 集中映射；`StageInfo` 增加显式 key；新增 `SortLocalizationKeys.py`/`CheckLocalizationReferences.py` 并接入 CI。`00acd45bc3` | 部分对齐。`UiTexts.resx` 1991 key 与 dev-v2 仅 39 个同名（v6.3.7 平铺名 + 自建点分名混用）；无排序/引用/死 key 校验，仅 `Tests/LocalizationAuditRegressionTests.cs` 做局部断言 | 新 key 强制点分命名 + CI 校验先落地（冻结漂移）；旧 key 走映射表按功能域分批，不要一次性改名 |
| P1-32 | 本地化下拉条目链式配置（提示/禁用/未适配后缀） | `LocalizedEntry`：`(值, key)` 可直接 `.WithSecondaryKey(...)`/`.WithTip(...)`/`.WithEnabled(...)`；罗格主题对官服/B 服以外标 `Common.NotAdapted`，`ClientType` 变更时 `RefreshLocalization()`。`be179f2364`、`1237fd851a` | 未实现。`App/ViewModels/TaskQueue/TaskModuleOption.cs:3` 仅 `(Type, DisplayName)`，无 Tip/IsEnabled/SecondaryKey；选项只在语言变更时整体重建（`RoguelikeModuleViewModel.cs:1028`） | 扩展选项模型并在条件依赖变化后刷新；设置页下拉同法覆盖 |
| P1-33 | 设置指引改为 ｢任务设置演示教学 + 用户协议确认｣ | StepBar 7 步重排为 界面/使用指引（含协议）/游戏/连接/性能/任务设置/更新；协议未确认时下一步禁用并显示 `Guide.Agreement.Tip`；任务设置步为真实交互演示（开始唤醒、添加作战、重命名、选关、排序、周常、添加切换主题）；未完成对应动作 Next 禁用；红点与跳过确认。`0f4ba4c610`、`0e940011a9`、`80bcbd6fbd`、`b8a65381a6` | 未对齐。`TaskQueuePageViewModel.cs:333` 固定 7 步、`:1480` 仅做步骤→设置分节跳转，无协议门、无演示互锁、无红点、无跳过；`TaskQueueGuideView.axaml` 为静态文案 | 重做引导状态机，gate 独立成可测状态；协议确认单独持久化；红点复用徽标机制 |
| P1-34 | MessageBox 与 Growl 的内容、用户选择写入日志 | 弹窗前按 Ctrl+C 文本格式记录标题/正文/按钮文案，关闭后记录选择与停留时长，两条日志同序号便于配对；Growl 记录类型/内容/暂存/补发/失败。`9262180332`、`2b0ffbb455` | 部分对齐。`AvaloniaDialogService` 经 `DialogFeatureService` 记录 `Dialog.Open/Action/Close`（`FeatureServices.cs:7250`、`:7263`），但只有标题、来源与动作摘要；`UiDiagnosticsService.RecordEventAsync` 仅在 verbose 构建生效、Release 为 no-op | 补正文与按钮文案（反馈包能直接定位用户点了什么），并让 Release 也记录（脱敏）；通知内容同样记录 |
| P1-35 | 设置页区块重组，性能配置迁至全局 | 背景并入界面、外部通知并入通知、软件渲染并入性能；`Performance` 从档案级 `Gui.Performance` 迁到全局 `Root.Gui.Performance`，配套迁移及多轮正确性修复（枚举强转、死键清理、折叠态不继承）。`f464016813` | 未对齐。仍按档案存 GPU 设置（`SettingsPageViewModel.cs:10915` 注释明示 profile-scoped）；设置页仍是 Background/ExternalNotification/Performance 三个独立分节（`App/Features/Root/SettingsView.axaml:39`、`:53`、`:55`） | 与配置模块一起迁全局并写迁移；合并分节后同步导航与折叠态 |

## 按模块明细

### 任务队列运行时与会话

范围：`ViewModels/UI/TaskQueueViewModel.cs`、`ViewModels/UI/RootViewModel.cs`、`Main/AsstProxy.cs`、`Main/Bootstrapper.cs`、`States/`、`Services/MaaService.cs`；7 文件 +5066 / −1228。

| 编号 | 行为 | 证据（commit / WPF 文件） | MAAUnified 落点 | 状态 |
| --- | --- | --- | --- | --- |
| P0-01 | 停止超时强收 + 需重启 + 运行归属判定 + 结束脚本单入口 | `0f4497da86`、`baa0032f70`、`91cdccbb17`、`caec74ff1f`；`TaskQueueViewModel.cs`、`Bootstrapper.cs`、`AsstProxy.cs` | `TaskQueuePageViewModel.cs:5078`、`:7996`、`:8845`；`FeatureServices.cs:597`、`:5351`；`UnifiedSessionService.cs:1545`、`:2164` | 未实现 |
| P0-02 | 掉线恒停止 + `OfflineConfirmAfterBattle` + StartUp 豁免 | `01184e2484`、`e4897cd512`；`AsstProxy.cs` `case "OfflineConfirm"` | `TaskQueuePageViewModel.cs:6736`、`:7862`；`LegacyConfigurationKeys.cs:110` | 未对齐（更旧） |
| P0-03 | 资源破损态 + 统一启动拦截 | `2e0ff15edd`、`a413ff2cfd`、`43965095df`；`AsstProxy.cs` `ProcSubTaskError`、`Bootstrapper.cs` | `TaskQueuePageViewModel.cs:3818`；无 `ResourceBroken` | 未实现（原 P1 拦截项并入） |
| P1-01 | 单次运行时长上限 | `5a55fc8ac1`；`RunningState.cs`、`RuntimeSettings.cs` | 无 `RunDurationLimit`；`SettingsPageViewModel.cs:10857` | 未实现 |
| P1-02 | 日志停滞检测 | `24639a0fdd`、`a637cdad89`；`RunningState.cs`、`TaskQueueViewModel.cs` | `TaskQueuePageViewModel.cs:6833`；`LegacyConfigurationKeys.cs:265-266` | 未对齐（更旧） |
| P1-03 | 出错跳过完成后动作 + 错误清单 | `cda1c3b07f`；`PostActionSetting.cs`、`AsstProxy.cs` | `TaskModuleSettings.cs:595`；`:8845` | 未实现 |
| P1-04 | 锁屏 / 独立退出 PC 客户端 / 倒计时保活 | `d58cb0825b`、`3b4f06dd86`、`0b048d5e94` | `Platform/Interfaces.cs:442`；`PostActionModuleViewModel.cs:14-31`、`:119`、`:139` | 未实现 |
| P1-05 | 多条 Core 任务 id 与逐链状态 | `9e846609aa`、`caec74ff1f`、`be31b86dbd`；`TaskItemViewModel.cs`、`TaskSettingsViewModel.cs:66-78` | `TaskQueueItemViewModel.cs:15-171`；`TaskQueuePageViewModel.cs:8835` | 部分对齐（主导归任务参数模块） |
| P1-15 | 运行期异常分类 | `f4d8fba923`、`c3b21524ea`、`75273dfca8`；`AsstProxy.cs` `ProcConnectInfo`/`ProcTaskChainMsg` | 三类回调均无命中 | 未实现 |

### 设置与配置管理

范围：`ViewModels/UI/SettingsViewModel.cs`（550 提交）、`ViewModels/UserControl/Settings/`（270）、`Configuration/`（224，+3891 / −428，新增 41 / 删除 2 / 改名 3）、`ViewModels/Dialogs/`（41）、`Helper/ConfigConverter.cs`（58，+745 / −86）；并集 945 提交 / 82 文件。

| 编号 | 行为 | 证据（commit / WPF 文件） | MAAUnified 落点 | 状态 |
| --- | --- | --- | --- | --- |
| P0-04 | 配置树形状：`Gui`/`Update`/`Timers` + 每档案分节 | `382009b97b`、`53870a7a52`；`Configuration/Root.cs:43-57`、`Configuration/Global/Update.cs`、`Global/TimerSettings.cs:47-58`、`Converter/Specific/GlobalGuiRenameConverter.cs` | `GuiNewJsonConfigImporter.cs:94`、`:110-113`、`:308-331`；`LegacyConfigValueMappings.cs:154` | 未对齐（导入侧；共享 platform 模块） |
| P0-05 | 反序列化容错，不整档重置 | `8c72cac105`、`8ad75a3760`；`Configuration/Converter/FaultTolerantRootConverter.cs`、`ConfigFactory.cs:220-232`、`RootViewModel.cs:186-195` | `UnifiedConfigurationService.cs:401-410`；`AvaloniaJsonConfigStore.cs` | 未对齐 |
| P1-09 | 定时计划任务唤醒 + 定点前通知 | `0b40a31009`、`ba602828e6`；`TimerSettings.cs:47-58`、`TimerSettingsUserControlModel.cs:40-51`、`Utilities/ScheduledWakeUp.cs` | `SettingsPageViewModel.cs:3701-3718`、`:10350-10352` | 未实现 |
| P1-10 | 三方服务分节 + 上报字段 | `247db567f3`；`Settings/ThirdParty.cs`、`Converter/Specific/ThirdPartyMigrationConverter.cs` | `LegacyTaskSchemaConverter.cs:291-294` | 部分对齐 |
| P1-11 | 分区顺序与折叠态持久化 | `0bc04350c0`、`f7f23f0386`、`ff2d6cd2e7`、`b5412be3a1`；`Constants/Enums/SettingKey.cs`、`Converter/Specific/SettingsPageReorgMigrationConverter.cs` | `SettingsPageViewModel.cs:123-140`；`SettingsSectionViewModel.cs` | 未实现 |
| P1-12 | 串行保存 + 敏感值脱敏 + 细粒度变更事件 | `382009b97b`、`53870a7a52`；`ConfigFactory.cs:308-345`、`:350-420` | `UnifiedConfigurationService.cs:48`、`:713`；`SensitiveSettingKeyRegistry.cs` | 部分对齐（MAAUnified 凭据隔离更强） |
| P1-35 | 设置页区块重组 + 性能迁全局 | `f464016813`；`Configuration/Root.cs`、`SettingsView.xaml` | `SettingsPageViewModel.cs:10915`；`SettingsView.axaml:39`、`:53`、`:55` | 未对齐 |

核实说明：原报告中 ｢删除配置前无确认｣ 一条经查代码不成立——`App/Features/Settings/ConfigurationManagerView.axaml.cs:124-142` 已有 `ShowWarningDialogAsync` 确认（scope `Settings.ConfigurationManager.DeleteConfirm.Dialog`）；同名档案也已拦截（`ConfigurationProfileFeatureService.cs:64-68`）。该条整体从清单移除，热切换保留（优于此前的重启方案）。

### 任务参数与任务模型

范围：`ViewModels/UserControl/TaskQueue/`（17 文件 +6182 / −2571）、`Models/AsstTasks/`（15 文件 +370 / −74）、`TaskItemViewModel.cs`、`TaskSettingsViewModel.cs`、`StageInfo.cs`、`StageActivityInfo.cs`；合计 36 文件 +6756 / −2676，有效提交 161。

| 编号 | 行为 | 证据（commit / WPF 文件） | MAAUnified 落点 | 状态 |
| --- | --- | --- | --- | --- |
| P0-08 | 库存保持任务 `DepotMaintain` | `38b2e9e4f2`、`948f23f8ae`、`0e99c81442`、`e3ca235bbb`、`be9cc84fd2`、`a7ee2c2bf7`、`e7ef27745e`、`0302381585`；`Configuration/Single/MaaTask/DepotMaintainTask.cs`、`DepotMaintainTaskUserControlModel.cs:786` | `TaskModuleSettings.cs:8-20`；`LegacyTaskSchemaConverter.cs:80-92` | 未实现 |
| P0-09 | 肉鸽开局干员三顺位 | `f04fb1645b`；`AsstRoguelikeTask.cs`（`CoreCharList`）、`Converter/Specific/RoguelikeStartingOpersConverter.cs` | `TaskParamCompiler.cs:1180`；`RoguelikeModuleViewModel.cs:543`、`:562`；`LegacyTaskSchemaConverter.cs:637-641` | 部分对齐 |
| P0-10 | 公招 `preserve_tags` / 3 星许可保留 / 6 星确认 | `0289fa0381`、`3a7dc4c636`、`f24c0e237a`；`AsstRecruitTask.cs`、`RecruitTaskHoldTagsConverter.cs`、`Helper/ConfigConverter.cs:306` | `TaskParamCompiler.cs:1040`、`:1046`、`:1118`；`LegacyTaskSchemaConverter.cs:348`、`:394` | 部分对齐 |
| P1-05 | 队列条目多 task id 与逐链状态 | `9e846609aa`、`caec74ff1f`、`be31b86dbd` | `TaskQueueItemViewModel.cs:15-171`、`TaskQueuePageViewModel.cs:8835` | 部分对齐（阻塞项） |
| P1-16 | 干员养成任务 | `a412e6a294`、`0008f4cae1`、`a69cbae69b`；`AsstOperProgressTask.cs` | 无 `OperProgress` | 未实现 |
| P1-17 | 更换主题任务 | `9c86aae937`；`AsstSwitchThemeTask.cs` | 无 `SwitchTheme` | 未实现 |
| P1-18 | 基建换班新增参数 | `c281b7aaca`、`59963a90cb`、`97d5e8ac38`、`3e570d1c23`；`AsstInfrastTask.cs` | `LegacyTaskSchemaConverter.cs:472-497` | 未实现 |
| P1-19 | 奖励任务限时签到 | `19699d8483`；`AsstAwardTask.cs` | `TaskModuleSettings.cs:552-580` | 未实现 |

### Copilot 与工具箱

范围：Copilot 子域 83 提交、工具箱子域 80 提交，合计 148 有效提交 / 20 文件 +4403 / −1126。

| 编号 | 行为 | 证据（commit / WPF 文件） | MAAUnified 落点 | 状态 |
| --- | --- | --- | --- | --- |
| P0-06 | 工具箱分节迁移 + 生息演算迁入 | `66e003377c`、`1c0440436a`；`Converter/Specific/ToolboxSectionMigrationConverter.cs`、`Settings/Toolbox.cs`、`ReclamationUserControlModel.cs` | `TaskQueuePageViewModel.cs:130`、`:645`；`LegacyTaskSchemaConverter.cs:41`、`:108`、`:683`；`RemoteControlCommandDispatcher.cs:107` | 未对齐（结构差异 + 无导入映射） |
| P0-07 | 战斗列表 id 契约 | `4843310bec`；`AsstCopilotTask.cs:152`、`AsstProxy.cs:2736-2742`、`CopilotViewModel.cs:2036-2060` | `WpfParity.cs:3456-3470`；`CopilotPageViewModel.cs:1848-1885`、`:1934-1940` | 未对齐 |
| P1-20 | `nav_name_override` | `c2021e694a`；`AsstCopilotTask.cs:161`、`CopilotViewModel.cs:2281` | `WpfParity.cs:3456-3470` | 未对齐 |
| P1-21 | payload 归一化（skill 0 / 阿米娅 / elite / 互斥 / role / module 5） | `b2d8591974`、`d9457ebd91`、`92bf7c7962`、`5994a8db49`、`62f60b9b98`、`1ca41b0847`、`cc4eae6e42`、`6341749473`；`CopilotViewModel.cs:1342-1398` | `WpfParity.cs:1682`、`:3004`、`:3669-3712` | 部分对齐（停在 v6.3.7） |
| P1-22 | 列表校验改按内容类型 | `d6601334f2`、`c2021e694a`；`CopilotViewModel.cs:2184-2218` | `WpfParity.cs:3083-3170`；`CopilotItemViewModel.cs:101` | 未对齐 |
| P1-23 | 神秘代码格式扩展 | `72213def33`；`CopilotViewModel.cs:404-476` | `WpfParity.cs:21`、`:2142-2165` | 未对齐 |
| P1-24 | 版本门控两段版本号 | `25481d16ca`；`Helper/CopilotHelper.cs:258`、`:281` | `CopilotVersionGate.cs:205-211` | 未对齐 |
| P1-25 | Copilot 设置档案化 | `382009b97b`、`5b39509df6`；`Settings/Copilot.cs`、`Helper/ConfigConverter.cs:893-933` | `WpfParity.cs:828-845`；`CopilotPageViewModel.cs:40` | 未对齐（决策项） |
| P1-26 | 自动像素画 | `1f39c29587` 等 7 提交；`PixelPaintUserControl.xaml`、`PixelPaintHelper` | 无 `PixelPaint` | 未实现（新特性） |
| P1-27 | 小游戏分类与分派 | `a105eccd05`、`a269cf5525`、`8a6ca5f276`、`1bc83ba6e6`、`e7a6f109fe`；`ToolboxViewModel.cs:2753-3020` | `ToolboxAssetCatalog.cs:493-560`；`ToolboxPageViewModel.cs:424` | 部分对齐 |
| P1-28 | 干员识别数据来源与导出 | `247db567f3`、`131f2d248d`、`86e58bda95`；`ToolboxViewModel.cs:1923-2338` | `ToolboxPageViewModel.cs:4635` | 部分对齐 |
| P1-29 | 仓库识别五种基础材料 | `1dda1ec985`；`ToolboxViewModel.cs:1075-1101`、`:1128-1140` | `ToolboxPageViewModel.cs:88-98`、`:2928`、`:2856-2895` | 未对齐且不自洽 |
| P1-30 | 空库存保护 | `0302381585`；`ToolboxViewModel.cs:760-890`、`:1108-1130` | `ToolboxPageViewModel.cs:2808-2855`、`:2898-2955` | 未对齐 |

### 平台服务与基础设施

范围：`Services/`、`Helper/`、`Utilities/`、`Extensions/`；889 检索提交 / 124 文件 +12466 / −1575。子域分布：通知 83 提交 / 38 文件、更新 30 / 7、WebApi 77 / 7、远控 59 / 1、Utilities 79 / 18。

| 编号 | 行为 | 证据（commit / WPF 文件） | MAAUnified 落点 | 状态 |
| --- | --- | --- | --- | --- |
| P0-04 | 通知/远控/更新设置改为类型化嵌套 | `382009b97b`、`f9c14e0be1`；`Configuration/Root.cs`、`Single/SpecificConfig.cs`、`Single/Settings/ExternalNotification.cs` | `LegacyConfigurationKeys.cs:299-331`；`GuiJsonConfigImporter.cs`；`UnifiedConfigurationService.TryGetProfileNode` | 未对齐（主导归设置模块） |
| P0-11 | 通知触发面扩大 + 统一入口 | `f9c14e0be1`；`Services/Notification/NotificationService.cs`、`AsstProxy.cs:1468`、`:1660`、`:2059`、`:2156` | `TaskQueuePageViewModel.BuildAutomaticExternalNotificationRequests` | 未实现 |
| P0-12 | 完整性校验 + 更新失败跨启动可见 | `6591943eaf`、`da79ff88ad`、`217db38484`；`Services/ResourceIntegrityChecker.cs`、`PendingUpdateApplier.cs` | `PendingAppUpdateService.cs` | 未实现 |
| P0-13 | 更新器委托、占用重试与回滚 | `4ce5e9bf97`、`208d28d6fa`、`42473f1ede`、`63d39b42c1`、`112b242020`；`PendingUpdateApplier.cs` | `PendingAppUpdateService.TryRollback` | 部分对齐 |
| P1-06 | 通知正文打包策略 | `f9c14e0be1`；`NotificationHistory.cs`、`NotificationFilter.cs` | `BuildExternalLogDetails` | 未对齐 |
| P1-07 | 渠道多实例化 + DiscordWebhook 收敛 | `f9c14e0be1`；`ExternalNotificationChannel.cs`、删除的 `DiscordWebhookNotificationProvider.cs` | `NotificationProviderFeatureService.Providers`；`SettingsPageViewModel.RemoteNotification.cs:403-410` | 未对齐 |
| P1-08 | 单渠道发送健壮性 | `571356b47b`、`0f25895c6d`、`69b1ef2280`、`b8e93a047c`；`TelegramNotificationProvider.cs`、`CustomWebhookNotificationProvider.cs` | `NotificationProviderFeatureService.SendTelegramAsync`、`SendCustomWebhookAsync` | 未对齐 |
| P1-13 | GitHub Token 与更新凭据 | `53870a7a52`；`Helper/GitHubUpdateHelper.cs`、`Configuration/Global/Update.cs`、`Models/UpdateCredentialModel.cs` | `VersionUpdatePolicy`；`AppUpdateWorkflowService` | 未实现 |
| P1-14 | 更新后启动行为 | `60cf643a2d`、`84b560d3cd`；`StartUpSettings.cs`、`Bootstrapper.cs` | `App/Program.cs`、`AppLifecycleService` | 未实现 |

远控侧另有两条已对齐、一条依赖：轮询间隔可配（`RemoteControlFeatureService.DelayAsync` clamp 500–60000 ms）与 `GetFreshImageAsync` 已对齐；｢启动成功写开始时间｣ 随 P1-01 落地后复用。远控 `StopTask` 语义与 `LinkStart` 前置判定分别并入 P0-01、P0-03。

### UI、本地化与视图行为

范围：`Views/`（1208 提交 / 101 文件）、`Res/`（1435 / 157）、`Res/Localizations/`（1351 / 5）、`Styles/`（62 / 24）、`Converters/`（7 / 5）、`App.xaml(.cs)`（49）；合计 288 文件 +26437 / −13755。本地化 `zh-cn.xaml` 1460 → 2279 条（点分命名 294 → 2274）；MAAUnified `UiTexts.resx` 1991 条，与 dev-v2 同名仅 39 个。

| 编号 | 行为 | 证据（commit / WPF 文件） | MAAUnified 落点 | 状态 |
| --- | --- | --- | --- | --- |
| P1-31 | key 点分命名 + 枚举映射收口 + CI 校验 | `00acd45bc3`；`Extensions/LocalizationKeyExtensions.cs`、`Models/StageInfo.cs`、`tools/SortLocalizationKeys.py` | `Application/Resources/Localization/UiTexts.resx`；`Tests/LocalizationAuditRegressionTests.cs` | 部分对齐 |
| P1-32 | `LocalizedEntry` 链式配置 | `be179f2364`、`1237fd851a`；`Utilities/ValueType/LocalizedEntry.cs`、`GameSettingsUserControlModel.cs` | `App/ViewModels/TaskQueue/TaskModuleOption.cs:3` | 未实现 |
| P1-33 | 引导：任务设置演示 + 协议确认 | `0f4ba4c610`、`0e940011a9`、`80bcbd6fbd`、`b8a65381a6`；`Views/UserControl/GuideUserControl.xaml`、`SettingsViewModel.cs` | `TaskQueuePageViewModel.cs:333`、`:1480`；`TaskQueueGuideView.axaml` | 未对齐 |
| P1-34 | 弹窗/通知内容入日志 | `9262180332`、`2b0ffbb455`；`Helper/MessageBoxHelper.cs`、`Helper/GrowlHelper.cs` | `AvaloniaDialogService.cs`；`FeatureServices.cs:7250`、`:7263`；`UiDiagnosticsService.cs:103-110` | 部分对齐 |
| P1-04 | 独立退出 PC 客户端（视图侧） | `3b4f06dd86`；`PostActionUserControl.xaml`（`IsPCConnectConfig` 显隐） | `PostActionModuleViewModel.cs:14-31` | 未对齐（主导归任务队列） |
| P1-35 | 设置页分节合并 + 性能迁全局（视图侧） | `f464016813`；`SettingsView.xaml` | `App/Features/Root/SettingsView.axaml:39`、`:53`、`:55` | 未对齐（主导归设置模块） |

本模块无独占 P0：唯一 P0（生息演算迁移）已并入 P0-06。

## 建议的并行实施划分

四条工作流的 P0/P1 归属除 P1-06 外互不重叠（P1-06 由 A 定字段模型、D 落管线，单独标注依赖），可同时开工。

### W-A 配置契约与导入（config-contract）

- **目标**：dev-v2 生成的 `gui.json` / `gui.new.json` 导入不再静默丢键，损坏档不再整档重置。
- **范围**：`Application/Configuration/GuiNewJsonConfigImporter.cs`、`GuiJsonConfigImporter.cs`、`LegacyConfigValueMappings.cs`、`AvaloniaJsonConfigStore.cs`、`Application/Services/UnifiedConfigurationService.cs`、`Compat/Constants/LegacyConfigurationKeys.cs`、`Application/Security/SensitiveSettingKeyRegistry.cs`、`App/ViewModels/Settings/SettingsPageViewModel*.cs`（分节顺序/三方/更新凭据）、`Application/Models/VersionUpdatePolicy.cs`（token 字段）、`CI/`（本地化 key 校验脚本）。
- **条目**：P0-04、P0-05、P1-09、P1-11、P1-12、P1-13、P1-25、P1-31、P1-35，以及 P1-06 的字段模型部分。
- **验收**：用一份 dev-v2 生成的 `gui.new.json` 跑导入，`ImportReport.Warnings` 里不再出现 `Timer entry 'List' was not recognized`；连接/启动/定时/更新设置在 `avalonia.json` 中可见；人为写入非法枚举值后加载，仅该键回退、其余键保留并生成 `avalonia.json.broken.<ts>`；快照测试比对导入结果。
- **依赖/冲突**：P0-04 是 P0-11、P1-06、P1-07、P1-10 的前置，须最先合并。与 W-C 在 `LegacyTaskSchemaConverter` 有交界（任务条目 vs 设置键），改动分区处理、不要同文件并行；`SettingsPageViewModel` 是多工作流共用的巨型文件，建议先拆分再分配。

### W-B 运行会话与故障收口（run-session）

- **目标**：停止/完成判定按运行归属收口，超时与资源破损不再把 UI 卡在 ｢运行中｣。
- **范围**：`App/ViewModels/TaskQueue/TaskQueuePageViewModel.cs`（`StopAsync`、`QueueSpecialCallbackNotification`、完成/后置动作段）、`App/ViewModels/TaskQueue/TaskQueueItemViewModel.cs`、`Application/Services/Features/FeatureServices.cs`（`ConnectFeatureService.StopAsync`、`PostActionFeatureService.ExecuteEndScriptAsync`）、`Application/Orchestration/UnifiedSessionService.cs`、`Application/Services/MAAUnifiedRuntime.cs`（新增拦截与破损态）、`App/ViewModels/TaskQueue/PostActionModuleViewModel.cs`、`Platform/Interfaces.cs`。
- **条目**：P0-01、P0-02、P0-03、P1-01、P1-02、P1-03、P1-04、P1-05、P1-15、P1-33。
- **验收**：模拟 Core 不响应停止（阻塞 `AsstStopAsync`）→ 60 s 后 UI 回到空闲并给出需重启提示，二次启动被拦截；`AllTasksCompleted` 在 Copilot 归属下不误判主队列完成；结束脚本每轮只发一次（日志计数）；`OfflineConfirmAfterBattle` 与 `StartUp` 链分别验证；`ProcessTask/UnknownTask` 注入后队列停止并进入资源破损态。
- **依赖/冲突**：P0-03 的拦截入口被 W-D（远控）与 W-C（Copilot 启动门）复用，接口先定签名；`TaskQueuePageViewModel.cs` 已近万行，是最大冲突面，建议与 W-C/W-D 约定 ｢只改各自方法段｣ 并在合并前 rebase。P1-05 是 P0-08、P1-16 的硬前置，需要优先交付。

### W-C 任务类型与 Copilot/工具箱（task-copilot-toolbox）

- **目标**：三种新任务类型与公招/肉鸽/基建参数落地，Copilot 契约与 WPF 一致，工具箱不再丢数据。
- **范围**：`Application/Models/TaskModuleSettings.cs`、`Application/Services/TaskParams/TaskParamCompiler.cs`、`Application/Configuration/LegacyTaskSchemaConverter.cs`、`App/ViewModels/TaskQueue/RoguelikeModuleViewModel.cs`、`App/ViewModels/TaskQueue/RecruitTaskModuleViewModel.cs`、`App/ViewModels/TaskQueue/InfrastModuleViewModel.cs`、`App/ViewModels/Copilot/CopilotPageViewModel.cs` 与 `CopilotPageViewModel.WpfParity.cs`、`Application/Services/Features/CopilotVersionGate.cs`、`App/ViewModels/Toolbox/ToolboxPageViewModel.cs`、`App/ViewModels/Toolbox/ToolboxAssetCatalog.cs`。
- **条目**：P0-06、P0-07、P0-08、P0-09、P0-10、P1-10、P1-16、P1-17、P1-18、P1-19、P1-20、P1-21、P1-22、P1-23、P1-24、P1-26、P1-27、P1-28、P1-29、P1-30、P1-32。
- **验收**：v6.3.7 旧配置导入后生息演算参数不丢、`Toolbox-Reclamation` 可启动、队列不出现 Reclamation 条目；三项任务类型能进队列并序列化出预期 Core 任务链；三项列表中间项失败时不取消错误条目的勾选；`Copilot.TaskList`（无 `tab_index`）不再被判非法；仓库识别后龙门币/合成玉/采购凭证可见，再跑一次空结果切图不清空既有仓库。
- **依赖/冲突**：P1-05（W-B）为 P0-08 前置；P1-10 的一图流 OpenAPI 依赖 P0-04 的设置键，且与 P1-28 共用客户端；P0-06 需要 W-D 的远控别名同步。`LegacyTaskSchemaConverter.cs` 与 W-A 有交界，按 ｢设置键 / 任务条目｣ 分段。

### W-D 通知投递、更新与诊断（notify-update）

- **目标**：任务级通知事件可外发、更新失败可发现可修复、反馈包能定位用户在弹窗里做了什么。
- **范围**：新增 `Application/Services/Notifications/`（Dispatch/History/Filter/Trigger）、`App/ViewModels/TaskQueue/TaskQueuePageViewModel.cs`（外发与日志详情段）、`Application/Services/Features/NotificationProviderFeatureService.cs`、`Application/Services/VersionUpdate/PendingAppUpdateService.cs`、`AppUpdateWorkflowService.cs`、`Application/Services/AppLifecycleService.cs`、`App/Program.cs`、`App/Features/Dialogs/AvaloniaDialogService.cs`、`App/Views/InAppNotificationPresentationQueue.cs`、`Application/Services/RemoteControl/RemoteControlCommandDispatcher.cs`。
- **条目**：P0-11、P0-12、P0-13、P1-06、P1-07、P1-08、P1-14、P1-34。
- **验收**：三类 `kind` 各触发一次外发，白/黑名单边界符合预期；Telegram 超 4096 字符仍送达且保留尾部；CustomWebhook 的 `Content-Type` 与 JSON 转义生效；删掉 `filelist.txt` 中一个文件后启动给出缺失提示并可修复；存在 `pending-update-failure.txt` 时下次启动仍提醒；反馈包的 `gui.log` 能看到弹窗标题、正文、按钮与用户选择。
- **依赖/冲突**：P1-06 的配置字段由 W-A 提供模型，管线在 D 落地，接口需先对齐；P0-12/P0-13 可能与 CI 产出（`filelist.txt`）和打包脚本耦合，改前确认不触碰 `ci.yml` / `release-nightly-ota.yml` 的双工作流约束；远控命令名与 W-C 的 P0-06 共用，放在 W-C 提交或明确串行。

## 不建议跟进

### 架构差异，照搬无收益

- `RunningState` 计时器加锁与 `UntilIdleAsync` 信号化（`a637cdad89`、`baa0032f70`）：WPF 用 `System.Timers.Timer` 才需要锁与引用计数；MAAUnified 是 `CancellationTokenSource` + `Task.Delay` + `SessionStateMachine`，无同类竞态。
- `RunControlState` 单例镜像（`91cdccbb17` 新增 `States/RunControlState.cs`）：为 XAML 全局绑定做的 Stylet 镜像，MAAUnified 由 `MainShellViewModel` 承担。仅 ｢停止按钮标出当前运行归属｣ 这条用户可见信息缺失，可作 P2 文案备忘。
- 任务序列化框架重构（`560ba65278`、`69157915b5`、`26b91963d9`、`db1b3b0a8d`）：`SerializeTask` 那套 Stylet VM 抽象不适用，只吸收 ｢返回多条 taskId｣ 语义（已入 P1-05）。
- `StageInfo` 展示模型扩展（`f70864634a`、`aafd14f62e`、`be9cc84fd2` 的 `DisplayKey`/`TipKey`/`DropBrushKeys`）：MAAUnified 的 `BuildDailyResourceHint`、`BuildInventoryHint` 已用另一套实现覆盖语义。
- 生息演算 `Mode` 由枚举改 int（`2e0f985019`）：已有 `ReclamationModes` 白名单，语义等价。
- 静态设置 VM 单例 + 切换档案即重启：MAAUnified 单存储 + 热切换更优，不倒退；只需回归 ｢切换后设置页与运行中任务一致｣。
- WPF 双存储退役（`acefc4810d`、`63b99e0722`、`f9013ddde7`）：MAAUnified 只有 `avalonia.json`，无对应物。
- `SettingsPageReorgMigrationConverter` 的性能设置迁移、`DiscordWebhookFixConverter` 的历史脏数据修正：均因 MAAUnified 无对应结构/模板而无迁移需求；导入含 `CustomWebhook.Body` 的旧档时在导入期做一次字符串修正即可，不必建转换器。

### Windows/WPF 专有基础设施

- `--skip-core-init` 预览模式（`2f7f469f1e`）、README 截图演示模式 `DemoShotMode`（`cddceaa07d`）。
- `Growl`→`GrowlHelper`（`2b0ffbb455`）、`MessageBoxHelper`（TaskDialog → Win32）、`ToastNotification`、`NotificationImplWinRT`：`Platform/PlatformProviders.cs` 的 `DesktopNotificationService` 已覆盖系统通知与站内回退。
- `ScheduledWakeUp` 的 Win32 任务计划实现：若要做，应分别用 launchd / systemd timer 重新设计（与 P1-09 的平台抽象一致，但不要移植 Win32 代码）。
- `GameAudioMuteManager`（`2e89bebb81`，WASAPI 会话静音）：PC 端连接专用，低频便利项，MAAUnified 仅有 `VolumeMute` 热键，P2 以下。
- `ComboBoxExtensions`（可搜索下拉、虚拟化、输入法组字）：Avalonia 控件模型不同。
- `InstanceIdentifier`（8 位哈希用于注册表自启键与计划任务名）：MAAUnified 自启走平台层，仅在多安装共存时有键名冲突的低频风险，可在平台层顺手加同源哈希。
- `HttpService` 传输层重构（gzip / HTTP2 / SOCKS5 / ETag / 429 Retry-After）：实现差异；仅 ｢下载 idle 超时｣ 与 ｢429 Retry-After｣ 值得单列评估。
- 通知横幅点击跳转与成就内容扩充（`ed6c85d50`、`a14809cc4d`、`85024b6141`）：成就 id 89 → 112 的补齐属 P2，等通知链路（P0-11）落地后再取。

### 工程与文案类，随功能跟进

- i18n key 全量重命名（`00acd45bc3`）：不跟改名，只保证映射表能承接；注意该提交专门恢复过 9 个被误改的**配置迁移键值**——迁移键字符串必须保持稳定，不能跟着文案一起改。
- 本地化直接替换 1991 条 resx 不可取：新 key 走点分命名，旧 key 走映射表分批（P1-31）。
- 日志卡片/缩略图/`AddLogSection` 分段与任务名去重（`0d9a257fde`、`1c8b1097b4`、`8cff63b23a`、`5e502a8110`、`e08d2e501e`）：纯 WPF 日志渲染层。
- 每任务用时输出 `Asst.TaskQueue.TaskTime`（`8a347797a9`）：纯文案，P2。
- 页签过渡动画与设置页平滑滚动（`dd4e2ef275`、`b8bda57430`、`b73daa6cbe`）：观感项，补动画档位即可，不逐处接动画。
- Tooltip 宽度 500 与按句换行（`67805e2821`、`9dd445d2e8`）：`App/Controls/TooltipHint.axaml:36` 现为 340，可顺手调至 500，不单列条目。
- 悬浮窗（`382009b97b` 的 `OverlayTarget` 迁移）：MAAUnified 已有 `OverlayTargetPersistence` 与 `OverlayPresentationViewModel`，视为已对齐。
- 彩蛋（`eb318012c4` 无痛喝酒 / Pallas 语言）：非阻塞对话框，不属 Copilot 行为。
- 成就解锁散点调用：MAAUnified 已有 `AchievementTrackerService` 并在对应位置调用，机制差异，不单列。
- 纯工程项：`TreatWarningsAsErrors`/StyleCop（`bcdc925bb2`）、`Log.ForContext(typeof(T))`（`978dbc95aa`）、`NotifyPropertyChangedWithValue`（`255e59357a`）、版权年份、`file-scoped namespace`、`using` 排序。
- 资源素材（`Res/Backgrounds` 125 张壁纸、`Res/Video`、`Res/Img`）：素材同步，登记资源清单即可。
- WPF 绑定缺陷类：`20dc0e22dd`（`Setter.Value` 裸 `c:Binding` 致自定任务菜单可见，MAAUnified 已用 `ShouldShowDebugTask` 达成同一 gating）、`57dc233729`（NotifyIcon 双击间隔）、`2193645c3b`/`f94fd5ab95`（BadModules 弹窗崩溃回退）。

### 已对齐，无需动作（列出以免重复排查）

- `d17249649e` 不重启切换语言 + 帕拉斯语言（`UiLanguageCoordinator`、`PallasLanguageState`、`{key=}` 内联展开）。
- `9799abc28f` 任务齿轮右键上移/下移（`App/Features/Root/TaskQueueView.axaml.cs:699-700`）。
- `0968272b20` 删除配置前确认（`ConfigurationManagerView.axaml.cs:124-142`）。
- `53403c3316` 语言热切换后选项/计数不刷新：已用 `LocalizedOptionSpec` + `RebuildLocalizedOptions()` 规避。
- `43965095df` 内核未就绪阻止开始任务：该分支已由 `EnsureCoreReadyAsync` 覆盖（实现方式不同，无需照搬 Growl）；缺的是资源破损与需重启的优先级（见 P0-03）。
- `e5bc496a5e` 硬编码 UI 文本资源化：`Tests/LocalizationAuditRegressionTests.cs` 方向一致（残留中文兜底属长期治理）。
- 任务跳过原因日志（`bd9fdc91cb`）、刷理智目标库存缺口与达标跳过（`5efdfa1b5c`）、次数不整除告警、周计划当日跳过、`expiring_medicine`→`medicine_expire_days` 双写：均已在 `TaskParamCompiler` / `UnifiedSessionService` 中对齐。
- 退出时销毁 MaaCore（`4dee067b0f`）：已由 `MAAUnifiedRuntime.DisposeAsync` → `CoreBridge.DisposeAsync()` 覆盖。
- 远控轮询间隔与最新帧截图（`e1b58dd884`、`8fc0e56556`）：已对齐。

## 实施状态（2026-11-12）

按两组并行波次完成，均已合并进 `feat/wpf-parity` 并通过全量测试（3045 通过，3 条沙箱环境失败：MuMu/LDPlayer 路径与 macOS 钥匙串）。

- 波次一：运行生命周期（P0-01/P1-01/P1-02/P1-15）、更新完整性（P0-12/P0-13/P1-13/P1-14）、Copilot 协议与设置（P1-20…P1-25）、通知渠道健壮性（P1-06/P1-07/P1-08/P1-34）、工具箱数据链路（P1-10/P1-28…P1-30）、新任务类型（P1-16…P1-19）。
- 波次二：资源破损与统一启动闸门（P0-03）、逐链状态与库存保持（P1-05/P0-08）、生息演算迁工具箱（P0-06/P1-27）、任务级通知与出错后置动作（P0-11/P1-03）、锁屏与退出 PC 客户端（P1-04）、设置分区与全局性能（P1-11/P1-12/P1-35）、定时唤醒与前通知（P1-09）。
- 波次三：本地化 key 审计与选项链（P1-31/P1-32）、引导状态机（P1-33）、外部更新器委托与强制完整包（P0-12/P0-13 收尾）、干员识别自动接入 Copilot 与导出列（P1-10/P1-28 收尾）。

仍保留的已知边界（不阻塞本 PR）：Windows 真实 `MAA.Updater.exe` 端到端未在 macOS 验证；resx 历史乱序按基线冻结、未做全量重排；引导任务演示未 1:1 复刻 WPF 的拖拽与 11 个 gate；干员卡片技能/模组徽标与 `BlackFlow` 主题未接入；Copilot 悖论列表对象数组契约要求 Core ≥ `4843310bec`（随包 Core 已满足）。

## 复核补缺（2026-11-12 第二轮）

四/五波继续补齐复核发现的缺失项：自动像素画（P1-26，整块新特性：点阵/OKLab/Fit/Dither/文本排版/预览交互/AsstPixelPaint 参数）、运行时长上限三态（P1-01，bool? null=仅本次）、外部更新器成功标记消费与同版本修复入口（P0-12/P0-13）、干员卡技能/模组徽标（P1-28）、外部通知 Delivery 历史日志与触发设置（P1-06）。

仍存的明确边界：
- P1-07 渠道实例化未做：配置仍是固定 provider + `ExternalNotification.Enabled` CSV，未写 `Configs` 数组，WPF dev-v2 互导时多实例渠道会丢；单实例凭据与 DiscordWebhook 兼容别名可用。
- P1-31 仅落点分命名/集合审计与冻结预算，未做 resx 全量重排。
- P1-32 `Common.NotAdapted` 只提供链式能力，`BlackFlow` 主题未引入。
- P1-33 演示已覆盖 7 个 gate（含拖拽排序入口），未 1:1 复刻 WPF 的全部拖拽细节。
- P1-04 macOS/Linux 锁屏为占位降级；P0-12 Windows 真实 `MAA.Updater.exe` 端到端未在 macOS 验证。

## 第三轮补缺（2026-11-12）

- P0-13：新增"导入本地更新包"入口（文件选择、包名/平台/架构校验、同版本完整包修复提示），与既有外部更新器委托、失败标记、强制完整包路径衔接。
- P1-04：macOS 锁屏改走系统 `CGSession -suspend`（存在即支持，缺失显式降级）；Linux 保留 loginctl/xdg-screensaver。
- P1-31：六份 `UiTexts*.resx` 按 key 字节序全量重排，本地化审计基线随之归零乱序预算。
- P1-32：引入 `BlackFlow`（黑流树海）肉鸽主题，非官服/B 服客户端显示 `Common.NotAdapted` 后缀，客户端切换后刷新条目。
- P1-27：小游戏分类/分派与 AutoRaisePotential、EventShop 任务名已在工具箱链路落地（本轮仅核验，无代码改动）。

P1-07 渠道实例化仍未做：WPF dev-v2 的 `ExternalNotification.Configs` 是按 provider 类型多态的 `$type` 数组，逐类映射需同步 9 个 provider 的模型与加密字段，改动面大且易破坏现有通知发送链路；当前仍以固定 provider + `Enabled` CSV 写回，单实例能力完整。

## 第四轮补缺（2026-11-12）

- P1-07 读侧落地：`ExternalNotificationChannels.ResolveEnabled` 优先解析 WPF `Gui.ExternalNotification.Configs` 的多态 `$type` 数组（Smtp/ServerChan/Bark/Discord/DingTalk/Telegram/Qmsg/Gotify/CustomWebhook，PascalCase 字段映射），支持同类型多实例发送；数组未知或损坏时回退 `Enabled` CSV + 扁平键。WPF 写入的加密凭据不做解密，发送失败会显式暴露。写侧（MAAUnified 自己的多实例编辑/保存）仍未做，UI 仍按 provider 单实例编辑。
- P1-33 收尾：`SettingsGuideDemoAction` 补 `ReorderTheme`、`AddNightTheme` 两个 gate（与 WPF dev-v2 的 9 个 gate 对齐），五语言文案同步。

## 第五轮补缺（2026-11-12）

- P1-07 写侧完成：外部通知设置新增"渠道实例"区块，可添加/删除实例、编辑 `key=value` 参数；配置数组只写 `$type` 与 `Id`，参数值写入动态注册的敏感键（`ExternalNotification.Instance.<id>.<param>`，随 `SensitiveSettingKeyRegistry` 提取进敏感存储）；保存后重载可恢复，旧扁平键与 `Enabled` CSV 继续双写，单实例链路不变。
- 至此 P1 执行清单中 P1-07 读、写两侧均闭环；仅剩平台/环境性限制（Windows 真实 updater 端到端、macOS 锁屏依赖系统 CGSession、WPF 加密凭据在非 Windows 无法解密）。

## 第六轮（2026-11-13，故障修复与 P2）

- 故障修复：MuMu/LDPlayer 自动探测测试改用 `MAAUNIFIED_USER_PROFILE_ROOT` 临时目录；macOS 无交互钥匙串时系统存储测试接受已知错误码优雅退出；`LinkStart` 停止竞态测试改为等待连接层 Stop 计数，消除全量并行下的偶发失败。
- P2：任务完成日志追加用时（`TaskQueue.TaskTime` 五语言）；停止按钮 tooltip 标出当前运行归属；Tooltip 最大宽度 340→500；zh-CN 补齐 en-US 独有的 7 个 Toolbox key；更新下载补 429/503 `Retry-After` 单次重试与读流空闲超时（60 s，可测试覆盖）。
- 仍待办：成就 id 88→112（23 条定义与触发点）、页签过渡动画、资源素材清单、GameAudioMuteManager；P1-07 的 provider 字段化编辑与 WPF 写回互操作；README 截图未重拍。

## 第七轮（2026-11-13，告警与并行稳定性）

- 编译告警 272 → 24（仅剩离线环境的 NU1900 漏洞库不可达）：清除 30 条 resx 重复 key（`RecruitingTips`/`RecruitmentOfStar`/`TaskStallWarning`）、全部 xUnit 规则告警、nullable 系列；测试替身 CS0067 与 Tmds.DBus `Variant` 过时按项目注释抑制。
- Copilot/GameSettings 迁移到 Avalonia 新剪贴板（`TryGetTextAsync`）与 `DragEventArgs.DataTransfer`/`TryGetFile`，不再使用过时 `GetTextAsync`/`IDataObject`。
- 修复两个全量并行 flake：托盘停止通知等待通知到达；LinkStart 停止测试允许“连接先被取消、无需 Core Stop”的调度路径。
- 仍待办：成就 88→112（23 条定义与触发点）、页签过渡动画、资源素材清单、GameAudioMuteManager；P1-07 provider 字段化编辑与 WPF 写回互操作；README 截图。

## 第八轮（2026-11-13，P2 成就补齐）

- 成就目录 88 → 111：补 WPF dev-v2 第三批 23 条（TimeManagementMaster、DoubleSync、ResumeRecord、QueueExpansion、QueueSimplifier、SortingMaster、AchievementObserver、DisappearTrick、TitleTweaker、AllChannelBroadcast、ConnectionTester、MasterDirector、PrivateDormManager、OperatorRoster、ChangelogReader、LatestVersionInspector、TimeMachine、LoadLastSave、PallasStarter、PallasCheers、OneMoreLook、SlackingOff、BackstageExplorer），五语言标题/描述/条件均已有文本。
- 已接触发点：任务增删（QueueExpansion/QueueSimplifier）、自定义任务（MasterDirector）、任务排序（SortingMaster）、成就列表（AchievementObserver）、成就备份/恢复（TimeMachine/LoadLastSave）、版本已最新（LatestVersionInspector）、更新日志（ChangelogReader）、打开 debug 目录（BackstageExplorer）、最小化到托盘（DisappearTrick）、全部通知渠道启用（AllChannelBroadcast）、干员识别导出（OperatorRoster）、识别间隔超 7 天（ResumeRecord）、基建自定义计划（PrivateDormManager）、Pallas 酒（PallasCheers）。
- 仍未接触发点：ConnectionTester、DoubleSync、OneMoreLook、PallasStarter、SlackingOff、TimeManagementMaster（对应交互在 MAAUnified 尚无同形入口，后续按可用入口补）。

## 第九轮（2026-11-13，P1-07 字段化与 P2 收尾）

- P1-07 provider 字段化编辑：渠道实例按 provider 支持的参数生成逐字段输入框（`sendKey`/`server`/`botToken` 等），未知参数保留在原始 key=value 附加表，保存仍写入实例敏感键；旧文本接口保留兼容。
- P2 页签过渡：主界面四个根页面 ContentControl 换为 `TransitioningContentControl` + 180 ms `CrossFade`。
- P2 成就触发补充：截图测试（ConnectionTester）、点击预览图重测（OneMoreLook）、用户数据更新同时勾选干员与仓库（DoubleSync）。
- 仍未接触发点：PallasStarter、SlackingOff、TimeManagementMaster（MAAUnified 无同形入口）；GameAudioMuteManager 属 Windows PC 端低频项，不移植。
