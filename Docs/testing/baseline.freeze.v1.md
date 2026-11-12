# MAAUnified 基线冻结 v1

## 摘要
- Frozen at (UTC): `2026-03-04T07:48:13Z`
- WPF baseline commit: `23ddd271b40a3d0c18de8a6ae8305357cc032e19`
- Scope: `src/MAAUnified/**`
- Matrix mode: `tiered`
- Themes: `Light, Dark`
- Locales: `zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas`
- Feature items: `38`
- System items: `12`
- Config keys: `304`
- Fallback records: `15`

## Feature Parity
- Aligned: `19`
- Gap: `19`
- Waived: `0`

| Item ID | Group | Parity | Avalonia Path |
| --- | --- | --- | --- |
| `TaskQueueRoot` | Root | Aligned | `App/Features/Root/TaskQueueView.axaml` |
| `SettingsRoot` | Root | Aligned | `App/Features/Root/SettingsView.axaml` |
| `Settings.About` | Settings | Aligned | `App/Features/Settings/AboutSettingsView.axaml` |
| `Settings.Achievement` | Settings | Aligned | `App/Features/Settings/AchievementSettingsView.axaml` |
| `Settings.Background` | Settings | Aligned | `App/Features/Settings/BackgroundSettingsView.axaml` |
| `Settings.ConfigurationManager` | Settings | Gap | `App/Features/Settings/ConfigurationManagerView.axaml` |
| `Settings.Connect` | Settings | Gap | `App/Features/Settings/ConnectSettingsView.axaml` |
| `Settings.ExternalNotification` | Settings | Gap | `App/Features/Settings/ExternalNotificationSettingsView.axaml` |
| `Settings.Game` | Settings | Gap | `App/Features/Settings/GameSettingsView.axaml` |
| `Settings.Gui` | Settings | Gap | `App/Features/Settings/GuiSettingsView.axaml` |
| `Settings.HotKey` | Settings | Aligned | `App/Features/Settings/HotKeySettingsView.axaml` |
| `Settings.IssueReport` | Settings | Aligned | `App/Features/Settings/IssueReportView.axaml` |
| `Settings.Performance` | Settings | Gap | `App/Features/Settings/PerformanceSettingsView.axaml` |
| `Settings.RemoteControl` | Settings | Gap | `App/Features/Settings/RemoteControlSettingsView.axaml` |
| `Settings.Start` | Settings | Gap | `App/Features/Settings/StartSettingsView.axaml` |
| `Settings.Timer` | Settings | Gap | `App/Features/Settings/TimerSettingsView.axaml` |
| `Settings.VersionUpdate` | Settings | Aligned | `App/Features/Settings/VersionUpdateSettingsView.axaml` |
| `Task.StartUp` | TaskQueue | Aligned | `App/Features/TaskQueue/StartUpTaskView.axaml` |
| `Task.Fight` | TaskQueue | Gap | `App/Features/TaskQueue/FightSettingsView.axaml` |
| `Task.Recruit` | TaskQueue | Gap | `App/Features/TaskQueue/RecruitSettingsView.axaml` |
| `Task.Infrast` | TaskQueue | Gap | `App/Features/TaskQueue/InfrastSettingsView.axaml` |
| `Task.Mall` | TaskQueue | Gap | `App/Features/TaskQueue/MallSettingsView.axaml` |
| `Task.Award` | TaskQueue | Gap | `App/Features/TaskQueue/AwardSettingsView.axaml` |
| `Task.Roguelike` | TaskQueue | Gap | `App/Features/TaskQueue/RoguelikeSettingsView.axaml` |
| `Task.Reclamation` | TaskQueue | Gap | `App/Features/TaskQueue/ReclamationSettingsView.axaml` |
| `Task.Custom` | TaskQueue | Aligned | `App/Features/TaskQueue/CustomSettingsView.axaml` |
| `Task.PostAction` | TaskQueue | Aligned | `App/Features/TaskQueue/PostActionSettingsView.axaml` |
| `Advanced.Copilot` | Advanced | Aligned | `App/Features/Advanced/CopilotView.axaml` |
| `Advanced.Toolbox` | Advanced | Gap | `App/Features/Advanced/ToolboxView.axaml` |
| `Advanced.StageManager` | Advanced | Gap | `App/Features/Advanced/StageManagerView.axaml` |
| `Advanced.WebApi` | Advanced | Gap | `App/Features/Advanced/WebApiView.axaml` |
| `Dialog.Announcement` | Dialogs | Aligned | `App/Features/Dialogs/AnnouncementDialogView.axaml` |
| `Dialog.VersionUpdate` | Dialogs | Aligned | `App/Features/Dialogs/VersionUpdateDialogView.axaml` |
| `Dialog.ProcessPicker` | Dialogs | Aligned | `App/Features/Dialogs/ProcessPickerDialogView.axaml` |
| `Dialog.EmulatorPath` | Dialogs | Aligned | `App/Features/Dialogs/EmulatorPathSelectionDialogView.axaml` |
| `Dialog.Error` | Dialogs | Aligned | `App/Features/Dialogs/ErrorDialogView.axaml` |
| `Dialog.AchievementList` | Dialogs | Aligned | `App/Features/Dialogs/AchievementListDialogView.axaml` |
| `Dialog.TextDialog` | Dialogs | Aligned | `App/Features/Dialogs/TextDialogView.axaml` |

## System Entry Parity
| Item ID | Parity | Avalonia Path |
| --- | --- | --- |
| `System.Connect` | Aligned | `App/Views/MainWindow.axaml` |
| `System.ImportLegacyConfig` | Aligned | `App/Views/MainWindow.axaml` |
| `System.TrayMenu.Start` | Aligned | `App/Views/MainWindow.axaml` |
| `System.TrayMenu.Stop` | Aligned | `App/Views/MainWindow.axaml` |
| `System.TrayMenu.ForceShow` | Aligned | `App/Views/MainWindow.axaml` |
| `System.TrayMenu.HideTray` | Aligned | `App/Views/MainWindow.axaml` |
| `System.TrayMenu.ToggleOverlay` | Aligned | `App/Views/MainWindow.axaml` |
| `System.TrayMenu.Restart` | Aligned | `App/Views/MainWindow.axaml` |
| `System.TrayMenu.Exit` | Aligned | `App/Views/MainWindow.axaml` |
| `System.TrayMenu.SwitchLanguage` | Gap | `App/ViewModels/MainShellViewModel.cs` |
| `System.GlobalErrorChannel` | Aligned | `App/Views/MainWindow.axaml` |
| `System.PageErrorChannel` | Aligned | `App/ViewModels/Infrastructure/PageViewModelBase.cs` |

## Config Key Mapping Summary
- Aligned: `63`
- Gap: `241`
- Waived: `0`

### Aligned Config Keys
- `Achievement.PopupAutoClose` -> `AchievementPolicy`
- `Achievement.PopupDisabled` -> `AchievementPolicy`
- `Announcement.AnnouncementInfo` -> `AnnouncementState`
- `Announcement.DoNotRemindThisAnnouncementAgain` -> `AnnouncementState`
- `Announcement.DoNotShowAnnouncement` -> `AnnouncementState`
- `Configurations` -> `UnifiedConfig.Profiles`
- `Copilot.AddUserAdditional` -> `CopilotPageViewModel.AddUserAdditional`
- `Copilot.CopilotTaskList` -> `CopilotPageViewModel.Items`
- `Copilot.LoopTimes` -> `CopilotPageViewModel.LoopTimes`
- `Copilot.SelectFormation` -> `CopilotPageViewModel.FormationIndex`
- `Copilot.SupportUnitUsage` -> `CopilotPageViewModel.SupportUnitUsage`
- `Copilot.UserAdditional` -> `CopilotPageViewModel.UserAdditional`
- `Current` -> `UnifiedConfig.CurrentProfile`
- `Default` -> `UnifiedConfig.Profiles[Default]`
- `Depot.DepotResult` -> `ToolboxPageViewModel.DepotResult`
- `GUI.Background.BlurEffectRadius` -> `SettingsPageViewModel.BackgroundBlur`
- `GUI.Background.ImagePath` -> `SettingsPageViewModel.BackgroundImagePath`
- `GUI.Background.Opacity` -> `SettingsPageViewModel.BackgroundOpacity`
- `GUI.Background.StretchMode` -> `SettingsPageViewModel.BackgroundStretchMode`
- `Gacha.ShowDisclaimerNoMore` -> `ToolboxPageViewModel.GachaShowDisclaimerNoMore`
- `Global` -> `UnifiedConfig.GlobalValues`
- `HotKeys` -> `UnifiedConfig.GlobalValues[HotKeys]`
- `MainFunction.PostActions` -> `PostActionFeatureService.LoadAsync`
- `MiniGame.SecretFrontEnding` -> `ToolboxPageViewModel.MiniGameSecretFrontEnding`
- `MiniGame.SecretFrontEvent` -> `ToolboxPageViewModel.MiniGameSecretFrontEvent`
- `MiniGame.TaskName` -> `ToolboxPageViewModel.MiniGameTaskName`
- `OperBox.Data` -> `ToolboxPageViewModel.OperBoxHaveList`
- `Overlay.PreviewPinned` -> `OverlayTargetPersistence.LoadPreviewPreference`
- `Overlay.Target` -> `OverlayTargetPersistence.Load`
- `Peep.TargetFps` -> `ToolboxPageViewModel.PeepTargetFps`
- `Penguin.EnablePenguin` -> `SettingsPageViewModel.EnablePenguin`
- `Penguin.Id` -> `SettingsPageViewModel.PenguinId`
- `Penguin.IsDrGrandet` -> `UnifiedTaskItem.Params[IsDrGrandet]`
- `RuntimeSettings.EnableRunDurationLimit` -> `SettingsPageViewModel.EnableRunDurationLimit`
- `RuntimeSettings.RunDurationLimitExecutePostActions` -> `SettingsPageViewModel.RunDurationLimitExecutePostActions`
- `RuntimeSettings.RunDurationLimitMinutes` -> `SettingsPageViewModel.RunDurationLimitMinutes`
- `TimeOut.Timer.ReminderIntervalMinutes` -> `SettingsPageViewModel.ReminderIntervalMinutes`
- `TimeOut.Timer.StallTimeoutEnabled` -> `SettingsPageViewModel.EnableStallTimeout`
- `TimeOut.Timer.StallTimeoutMinutes` -> `SettingsPageViewModel.StallTimeoutMinutes`
- `Toolbox.Recruit.ChooseLevel3.Time` -> `ToolboxPageViewModel.RecruitLevel3Time`
- `Toolbox.Recruit.ChooseLevel4.Time` -> `ToolboxPageViewModel.RecruitLevel4Time`
- `Toolbox.Recruit.ChooseLevel5.Time` -> `ToolboxPageViewModel.RecruitLevel5Time`
- `VersionUpdate.AllowNightlyUpdates` -> `VersionUpdatePolicy`
- `VersionUpdate.AutoDownloadUpdatePackage` -> `VersionUpdatePolicy`
- `VersionUpdate.AutoInstallUpdatePackage` -> `VersionUpdatePolicy`
- `VersionUpdate.HasAcknowledgedNightlyWarning` -> `VersionUpdatePolicy`
- `VersionUpdate.Proxy` -> `VersionUpdatePolicy`
- `VersionUpdate.ProxyType` -> `VersionUpdatePolicy`
- `VersionUpdate.ResourceApi` -> `VersionUpdatePolicy`
- `VersionUpdate.ResourceUpdateSource` -> `VersionUpdatePolicy`
- `VersionUpdate.ResourceUpdateSource.MirrorChyanCdk` -> `VersionUpdatePolicy`
- `VersionUpdate.ScheduledUpdateCheck` -> `VersionUpdatePolicy`
- `VersionUpdate.StartupUpdateCheck` -> `VersionUpdatePolicy`
- `VersionUpdate.UpdateSource.ForceGithubGlobalSource` -> `VersionUpdatePolicy`
- `VersionUpdate.UpdateSource.MirrorChyanCdkExpired` -> `VersionUpdatePolicy`
- `VersionUpdate.UseAria2` -> `VersionUpdatePolicy`
- `VersionUpdate.VersionType` -> `VersionUpdatePolicy`
- `VersionUpdate.body` -> `VersionUpdatePolicy`
- `VersionUpdate.doNotShowUpdate` -> `VersionUpdatePolicy`
- `VersionUpdate.isfirstboot` -> `VersionUpdatePolicy`
- `VersionUpdate.name` -> `VersionUpdatePolicy`
- `VersionUpdate.package` -> `VersionUpdatePolicy`
- `Yituliu.EnableYituliu` -> `SettingsPageViewModel.EnableYituliu`

## Waiver Entries
- None

## Platform Fallback Records
| Capability | Platform | Expected | Current | Parity | Visible | Recorded | Locatable |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Tray | windows | full | full | Aligned | True | True | True |
| Tray | macos | full | full | Aligned | True | True | True |
| Tray | linux | degrade-visible | conditional-native-with-fallback | Aligned | True | True | True |
| Notification | windows | full | full | Aligned | True | True | True |
| Notification | macos | degrade-visible | conditional-native-with-fallback | Aligned | True | True | True |
| Notification | linux | degrade-visible | conditional-native-with-fallback | Aligned | True | True | True |
| Hotkey | windows | full | full | Aligned | True | True | True |
| Hotkey | macos | degrade-visible | conditional-native-with-fallback | Aligned | True | True | True |
| Hotkey | linux | degrade-visible | conditional-native-with-fallback | Aligned | True | True | True |
| Autostart | windows | full | full | Aligned | True | True | True |
| Autostart | macos | degrade-visible | conditional-native-with-fallback | Aligned | True | True | True |
| Autostart | linux | degrade-visible | conditional-native-with-fallback | Aligned | True | True | True |
| Overlay | windows | full | full | Aligned | True | True | True |
| Overlay | macos | degrade-visible | conditional-native-with-fallback | Aligned | True | True | True |
| Overlay | linux | degrade-visible | conditional-native-with-fallback | Aligned | True | True | True |

## Notes
- This file is generated from `baseline.freeze.v1.json` during Package A freeze.
- Any baseline change must follow `baseline-change-control.v1.md`.
- Projection consistency and acceptance locators are not runtime evidence; Aligned rows require executed behavior verification.
