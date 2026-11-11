# WPF -> Avalonia 字段/命令映射（P0 冻结）

## 页面映射

| Windows (WPF) | Avalonia |
| --- | --- |
| `RootView` | `App/Views/MainWindow.axaml` |
| `TaskQueueView` | `App/Features/Root/TaskQueueView.axaml` |
| `CopilotView` | `App/Features/Advanced/CopilotView.axaml` |
| `ToolboxView` | `App/Features/Advanced/ToolboxView.axaml` |
| `SettingsView` | `App/Features/Root/SettingsView.axaml` |
| `NotifyIcon` | `MainWindow` 托盘菜单入口 + `Platform/ITrayService` |
| `OverlayWindow` | `TaskQueuePageViewModel` Overlay 控制区 + `IOverlayCapabilityService` |

## 高级扩展分类

| WPF 冻结项 | Avalonia 实现 | 冻结状态 |
| --- | --- | --- |
| StageManager 服务（无独立页） | `StageManagerFeatureService` 被 Fight/TaskQueue 消费；`StageManagerView` 无产品导航入口 | Gap |
| 无 WebApi 对应项 | `WebApiFeatureService` / `WebApiView` 扩展；无产品导航入口 | Gap |

## 任务模块映射（10）

| WPF 模块 | Avalonia 模块视图 |
| --- | --- |
| StartUp | `Features/TaskQueue/StartUpTaskView.axaml` |
| Fight | `Features/TaskQueue/FightSettingsView.axaml` |
| Recruit | `Features/TaskQueue/RecruitSettingsView.axaml` |
| Infrast | `Features/TaskQueue/InfrastSettingsView.axaml` |
| Mall | `Features/TaskQueue/MallSettingsView.axaml` |
| Award | `Features/TaskQueue/AwardSettingsView.axaml` |
| Roguelike | `Features/TaskQueue/RoguelikeSettingsView.axaml` |
| Reclamation | `Features/TaskQueue/ReclamationSettingsView.axaml` |
| Custom | `Features/TaskQueue/CustomSettingsView.axaml` |
| PostAction | `Features/TaskQueue/PostActionSettingsView.axaml` |

## 设置段映射（15）

| WPF 设置段 | Avalonia 视图 |
| --- | --- |
| Configuration Manager | `Settings/ConfigurationManagerView.axaml` |
| Timer | `Settings/TimerSettingsView.axaml` |
| Performance | `Settings/PerformanceSettingsView.axaml` |
| Game | `Settings/GameSettingsView.axaml` |
| Connect | `Settings/ConnectSettingsView.axaml` |
| Start | `Settings/StartSettingsView.axaml` |
| Remote Control | `Settings/RemoteControlSettingsView.axaml` |
| GUI | `Settings/GuiSettingsView.axaml` |
| Background | `Settings/BackgroundSettingsView.axaml` |
| External Notification | `Settings/ExternalNotificationSettingsView.axaml` |
| HotKey | `Settings/HotKeySettingsView.axaml` |
| Achievement | `Settings/AchievementSettingsView.axaml` |
| Version Update | `Settings/VersionUpdateSettingsView.axaml` |
| Issue Report | `Settings/IssueReportView.axaml` |
| About | `Settings/AboutSettingsView.axaml` |

## 对话框映射（7）

| WPF Dialog | Avalonia Dialog View |
| --- | --- |
| Announcement | `Dialogs/AnnouncementDialogView.axaml` |
| VersionUpdate | `Dialogs/VersionUpdateDialogView.axaml` |
| ProcessPicker | `Dialogs/ProcessPickerDialogView.axaml` |
| EmulatorPathSelection | `Dialogs/EmulatorPathSelectionDialogView.axaml` |
| ErrorDialog | `Dialogs/ErrorDialogView.axaml` |
| AchievementList | `Dialogs/AchievementListDialogView.axaml` |
| TextDialog | `Dialogs/TextDialogView.axaml` |

## 配置键定位示例

| Legacy key (`ConfigurationKeys`) | Avalonia 候选落点 | 冻结状态 |
| --- | --- | --- |
| `GUI.Localization` | `SettingsPageViewModel.Language` | Gap |
| `GUI.UseTray` | `SettingsPageViewModel.UseTray` | Gap |
| `GUI.MinimizeToTray` | `SettingsPageViewModel.MinimizeToTray` | Gap |
| `GUI.WindowTitleScrollable` | `SettingsPageViewModel.WindowTitleScrollable` | Gap |
| `GUI.Background.ImagePath` | `SettingsPageViewModel.BackgroundImagePath` | Aligned |
| `GUI.Background.StretchMode` | `SettingsPageViewModel.BackgroundStretchMode` | Aligned |
| `GUI.Background.Opacity` | `SettingsPageViewModel.BackgroundOpacity` | Aligned |
| `GUI.Background.BlurEffectRadius` | `SettingsPageViewModel.BackgroundBlur` | Aligned |
| `RemoteControl.RemoteControlGetTaskEndpointUri` | `SettingsPageViewModel.RemoteGetTaskEndpoint` | Gap |
| `RemoteControl.RemoteControlReportStatusUri` | `SettingsPageViewModel.RemoteReportEndpoint` | Gap |
| `RemoteControl.RemoteControlPollIntervalMs` | `SettingsPageViewModel.RemotePollInterval` | Gap |

这些候选落点不能单独证明对齐。四个 `GUI.Background.*` 行已记录真实 `mapping_target`、读取入口、写入入口，并附有已执行的行为测试（见 `baseline.freeze.v1.json` 各行 notes），状态为 Aligned；其余上述行保持 Gap，直到补齐同样的证据。本表的冻结状态列由 `BaselineFieldMappingDocConsistencyTests` 与基线逐行核对，防止文档与基线漂移。

## 命令映射（关键）

| Windows 命令 | Avalonia 实现 |
| --- | --- |
| LinkStart | `TaskQueuePageViewModel.StartAsync` |
| Stop | `TaskQueuePageViewModel.StopAsync` |
| WaitAndStop | `TaskQueuePageViewModel.WaitAndStopAsync` |
| 无对应 Windows 命令（冻结 WPF 仅在启动时通过 `ConfigConverter` 自动转换旧配置） | `MainShellViewModel.ManualImportAsync`（Avalonia 扩展入口） |
| 生成支持包 | `SettingsPageViewModel.BuildIssueReportAsync` |
| Overlay 目标选择 | `TaskQueuePageViewModel.ReloadOverlayTargetsAsync` + `TaskQueuePageViewModel.SelectAndPersistOverlayTargetAsync`（选择落点委托 `OverlayFeatureService.SelectOverlayTargetAsync`） |

命令表记录调用定位，不是运行结果。Start/Stop/WaitAndStop、导入、支持包和 Overlay 的对齐结论必须以对应 ViewModel/Service 行为测试或保存的人工验收产物为准。
