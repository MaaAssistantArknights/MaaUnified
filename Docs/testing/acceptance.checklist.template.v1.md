# MAAUnified 验收清单模板 v1

## 摘要
- Schema version: `1.2.0`
- Baseline ref: `src/MAAUnified/Compat/Mapping/Baseline/baseline.freeze.v1.json`
- Case count: `51`

## Matrix Strategy
- Tier-1: Root + Settings + System entries, full platform/theme/locale matrix.
- Tier-2: Other feature pages, main path plus all-language text/error key validation.

| Tier | Platforms | Themes | Locales |
| --- | --- | --- | --- |
| Tier-1 | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| Tier-2 | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |

## Global Requirements
- Any failure must not crash the process.
- Any fallback must be visible to users, recorded in logs, and locatable by scope/case id.
- All checklist items remain P0 for Package A.

## Acceptance Cases
| Case ID | Tier | Item ID | Status | Execution Evidence | Platforms | Themes | Locales |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `ACC-002` | Tier-1 | `TaskQueueRoot` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-003` | Tier-1 | `SettingsRoot` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-004` | Tier-1 | `Settings.About` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-005` | Tier-1 | `Settings.Achievement` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-006` | Tier-1 | `Settings.Background` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-007` | Tier-1 | `Settings.ConfigurationManager` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-008` | Tier-1 | `Settings.Connect` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-009` | Tier-1 | `Settings.ExternalNotification` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-010` | Tier-1 | `Settings.Game` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-011` | Tier-1 | `Settings.Gui` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-012` | Tier-1 | `Settings.HotKey` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-014` | Tier-1 | `Settings.IssueReport` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-015` | Tier-1 | `Settings.Performance` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-016` | Tier-1 | `Settings.RemoteControl` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-017` | Tier-1 | `Settings.Start` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-018` | Tier-1 | `Settings.Timer` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-019` | Tier-1 | `Settings.VersionUpdate` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-020` | Tier-2 | `Task.StartUp` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-021` | Tier-2 | `Task.Fight` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-022` | Tier-2 | `Task.Recruit` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-023` | Tier-2 | `Task.Infrast` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-024` | Tier-2 | `Task.Mall` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-025` | Tier-2 | `Task.Award` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-026` | Tier-2 | `Task.Roguelike` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-027` | Tier-2 | `Task.Reclamation` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-028` | Tier-2 | `Task.Custom` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-029` | Tier-2 | `Task.PostAction` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-030` | Tier-2 | `Advanced.Copilot` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-031` | Tier-2 | `Advanced.Toolbox` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-032` | Tier-2 | `Advanced.StageManager` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-033` | Tier-2 | `Advanced.WebApi` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-038` | Tier-2 | `Dialog.Announcement` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-039` | Tier-2 | `Dialog.VersionUpdate` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-040` | Tier-2 | `Dialog.ProcessPicker` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-041` | Tier-2 | `Dialog.EmulatorPath` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-042` | Tier-2 | `Dialog.Error` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-043` | Tier-2 | `Dialog.AchievementList` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-044` | Tier-2 | `Dialog.TextDialog` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-045` | Tier-1 | `System.Connect` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-046` | Tier-1 | `System.ImportLegacyConfig` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-047` | Tier-1 | `System.TrayMenu.Start` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-048` | Tier-1 | `System.TrayMenu.Stop` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-050` | Tier-1 | `System.TrayMenu.ForceShow` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-051` | Tier-1 | `System.TrayMenu.HideTray` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-052` | Tier-1 | `System.TrayMenu.ToggleOverlay` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-053` | Tier-1 | `System.TrayMenu.Restart` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-054` | Tier-1 | `System.TrayMenu.Exit` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-055` | Tier-1 | `System.TrayMenu.SwitchLanguage` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-056` | Tier-1 | `System.GlobalErrorChannel` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-057` | Tier-1 | `System.PageErrorChannel` | NotExecuted | - | windows, macos, linux | Light, Dark | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |
| `ACC-058` | Tier-2 | `TaskQueueRoot` | NotExecuted | - | windows | Light | zh-cn, zh-tw, en-us, ja-jp, ko-kr, pallas |

## Case Waivers
- None

## Waiver Policy
- Allowed: `True`
- Required fields: `owner, reason, expires_on, alternative_validation`
- Rule: Waiver is allowed only for blockers and must include expiry and replacement validation plan.

## Notes
- This checklist file is generated from `acceptance.template.v1.json`.
- Checklist steps describe future or manual acceptance work and do not prove that a case has run.
