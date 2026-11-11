# MAAUnified 功能对齐矩阵（完整清单）

## 说明

- 基线：`src/MaaWpfGui` 现有功能。
- 目标：`src/MAAUnified` 全量复刻。
- 状态：`Implemented` / `InProgress` / `Pending`。
- 本表需与 `Docs/testing/baseline.freeze.v1.md` 保持同步；若冲突，以冻结基线为准。
- 状态口径（2026-10 收紧，对应 M01-R3-02）：页面存在或清单登记不等于 Aligned；若某功能仍拥有未闭环的冻结 WPF P0 配置键（基线中为 Gap），该功能行记为 `InProgress`，配置闭环与执行证据补齐后方可回到 `Implemented`。

## A. 主框架与主流程

| 模块 | 状态 |
| --- | --- |
| Root 主框架 | Implemented |
| 连接设置 | Implemented |
| 任务队列 | Implemented |
| Start/Stop 状态机 | Implemented |
| 实时日志 | Implemented |

## B. 设置页

| 模块 | 状态 |
| --- | --- |
| About | Implemented |
| Achievement | Implemented |
| Background | Implemented |
| Configuration Manager | InProgress |
| Connect Settings | InProgress |
| External Notification | InProgress |
| Game Settings | InProgress |
| GUI Settings | InProgress |
| HotKey Settings | Implemented |
| HotKey Editor | Implemented |
| Issue Report | Implemented |
| Performance | InProgress |
| Remote Control | InProgress |
| Start Settings | InProgress |
| Timer Settings | InProgress |
| Version Update Settings | Implemented |

## C. 任务配置页

| 模块 | 状态 |
| --- | --- |
| StartUp | Implemented |
| Fight | InProgress |
| Recruit | InProgress |
| Infrast | InProgress |
| Mall | InProgress |
| Award | InProgress |
| Roguelike | InProgress |
| Reclamation | InProgress |
| Custom | Implemented |
| PostAction | Implemented |

## D. 高级模块

| 模块 | 状态 |
| --- | --- |
| Copilot | Implemented |
| Toolbox | InProgress |
| StageManager | InProgress |
| Web API | InProgress |
| Remote Control Center | InProgress |
| Overlay | Implemented |
| Tray Integration | InProgress |
| Notification Providers | Implemented |

Toolbox note: Avalonia 侧当前为 WPF 对齐的 6-tab 运行工具页，风险提示仅保留在抽卡页。功能级仍为 InProgress：Toolbox 名下 11 个冻结 WPF 配置键中 10 个已验证读写路径（Toolbox.Recruit.ChooseLevel3/4/5.Time、Depot.DepotResult、OperBox.Data、Gacha.ShowDisclaimerNoMore、Peep.TargetFps、MiniGame.TaskName/SecretFrontEnding/SecretFrontEvent 均 Aligned），唯一剩余 Gap 为 `Debug.TaskName`（冻结 WPF 键，无 Avalonia 消费方），该键阻塞功能级关闭。配置键归属计数：Advanced.Toolbox Aligned=10，Gap=1，Waived=0。

Remote Control Center note: 远程控制相关冻结 WPF 配置键（Settings.RemoteControl 名下）仍为 Gap，配置闭环前功能行保持 InProgress。配置键归属计数：Settings.RemoteControl Aligned=0，Gap=5，Waived=0。

Tray Integration note: 冻结 WPF 托盘菜单含 SwitchLanguage 项（NotifyIcon.xaml switchLangMenu）；Avalonia 保留了 TrayCommandId.SwitchLanguage 及处理逻辑，但 Windows/macOS/Linux 托盘菜单构建器均未暴露该入口（基线 System.TrayMenu.SwitchLanguage 为 Gap），补齐前保持 InProgress。

StageManager note: 冻结 WPF 只有被任务流程消费的 StageManager 服务；Avalonia 独立页面当前没有产品导航入口，因此不能以页面文件或测试内直接实例化作为可达性证据。

Web API note: 冻结 WPF 树中没有 WebApi 对应项，Avalonia 服务和页面属于扩展且当前没有产品导航入口，不计入 WPF parity 完成项。

## E. 对话框

| 模块 | 状态 |
| --- | --- |
| AnnouncementDialog | Implemented |
| VersionUpdateDialog | Implemented |
| ProcessPickerDialog | Implemented |
| EmulatorPathSelectionDialog | Implemented |
| ErrorDialog | Implemented |
| AchievementListDialog | Implemented |
| TextDialog | Implemented |
