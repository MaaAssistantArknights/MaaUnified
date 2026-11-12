# MAAUnified ↔ WPF 本地化 key 映射表（P1-31）

> 目标基线：MaaAssistantArknights `upstream/dev-v2` @ `96c2c4ee`（父仓工作副本 `fbe0132a47`，2026-11-12）；
> 对照实现：`src/MaaWpfGui/Res/Localizations/*.xaml`。本表是 P1-31 的交付物之一：冻结现存 key 漂移，
> 规划按功能域分批迁移。MAAUnified 现有 key **不做全量改名**，新 key 一律按右列风格（WPF 点分命名）取名。

## 计数口径

| 项 | 数值 |
| --- | --- |
| 五语言 key 并集（zh-cn / zh-tw / en-us / ja-jp / ko-kr） | 2157 |
| zh-cn / en-us / zh-tw / ja-jp / ko-kr 条目数 | 2153 / 2160 / 1886 / 1885 / 1885 |
| qps-Ploc（`pallas` 伪语言）条目数 | 97（允许为五语言子集） |
| 与 WPF dev-v2 同名 key | 66（含本波新增 `Common.NotAdapted`） |
| 点分命名 key（五语言并集） | 1956 |
| 不符合 `Domain.Topic.Name[.Tip]` 的历史 key | 213（201 个平铺名 + 12 个历史点分残留） |

清单 `wpf-parity-drift-v6.3.7.md` 记录的 39 个同名 key 来自更早的父仓快照；按当前父仓 WPF 工作副本实测为 66 个（本波新增 `Common.NotAdapted` 后与 WPF 同名），以本表为准。

## 同名 key（66）

以下 key 在 MAAUnified 与 WPF dev-v2 中拼写完全一致，迁移时可直接复用（新增语言时也按此名补齐）：

| key | MAAUnified 侧域 |
| --- | --- |
| `Common.NotAdapted` | Common |
| `Copilot.Title` | Copilot |
| `Game.Role.Caster` | Game |
| `Game.Role.Medic` | Game |
| `Game.Role.Pioneer` | Game |
| `Game.Role.Sniper` | Game |
| `Game.Role.Special` | Game |
| `Game.Role.Support` | Game |
| `Game.Role.Tank` | Game |
| `Game.Role.Warrior` | Game |
| `Infrast.ContinueTraining` | Infrast |
| `Infrast.Fiammetta.Recovery` | Infrast |
| `Infrast.Fiammetta.Recovery.Tip` | Infrast |
| `Infrast.Fiammetta.Target.None` | Infrast |
| `Infrast.Fiammetta.Targets` | Infrast |
| `Infrast.Fiammetta.Targets.Tip` | Infrast |
| `Infrast.Mode` | Infrast |
| `Infrast.Mode.Custom` | Infrast |
| `Infrast.Mode.Normal` | Infrast |
| `Infrast.Mode.Rotation` | Infrast |
| `Infrast.UseAbyssalHunter` | Infrast |
| `Infrast.UseAbyssalHunter.Tip` | Infrast |
| `Infrast.UsePerceptionInformation` | Infrast |
| `Infrast.UsePerceptionInformation.Tip` | Infrast |
| `Infrast.UsePinusSylvestris` | Infrast |
| `Infrast.UsePinusSylvestris.Tip` | Infrast |
| `Infrast.UseWorldlyPlight` | Infrast |
| `Infrast.UseWorldlyPlight.Tip` | Infrast |
| `Mall.Blacklist` | Mall |
| `Mall.CreditFight` | Mall |
| `Mall.Drink` | Mall |
| `Mall.OnlyOnceADay` | Mall |
| `Mall.ReceptionOptionsRequireInfrast` | Mall |
| `Recruit.AutoSelectLevel6` | Recruit |
| `Recruit.ForceRefresh` | Recruit |
| `Recruit.PreserveTags` | Recruit |
| `Recruit.SelectStrategy` | Recruit |
| `Roguelike.DelayAbortUntilCombatComplete` | Roguelike |
| `Roguelike.Difficulty` | Roguelike |
| `Roguelike.InvestmentEnabled` | Roguelike |
| `Roguelike.InvestmentWithMoreScore` | Roguelike |
| `Roguelike.OnlyStartWithEliteTwo` | Roguelike |
| `Roguelike.RefreshTraderWithDice` | Roguelike |
| `Roguelike.StartWithEliteTwo` | Roguelike |
| `Roguelike.StartWithSeed` | Roguelike |
| `Roguelike.StartWithSelectList` | Roguelike |
| `Roguelike.StopAtFinalBoss` | Roguelike |
| `Roguelike.StopAtMaxLevel` | Roguelike |
| `Roguelike.Theme` | Roguelike |
| `Settings.Background.BlurRadius` | Settings |
| `Settings.Background.Opacity` | Settings |
| `Settings.Background.StretchMode` | Settings |
| `Settings.Game.BlockSleep` | Settings |
| `Settings.Game.BlockSleepWithScreenOn` | Settings |
| `Settings.Game.ClientType` | Settings |
| `Settings.Game.DeploymentWithPause` | Settings |
| `Settings.Game.EndsWithScript` | Settings |
| `Settings.Game.StartsWithScript` | Settings |
| `Settings.ThirdParty.PenguinId` | Settings |
| `TaskQueue.RunDurationLimit.Reached` | TaskQueue |
| `TaskQueue.Stall.Warning` | TaskQueue |
| `TaskQueue.UserDataUpdate.TriggerInterval` | TaskQueue |
| `Toolbox.MiniGame.InteractiveExhibition.UncollectedNotification.Content` | Toolbox |
| `Toolbox.MiniGame.InteractiveExhibition.UncollectedNotification.Title` | Toolbox |
| `Toolbox.MiniGame.Name` | Toolbox |
| `Toolbox.Title` | Toolbox |

## 规划映射（按功能域分批）

迁移方向：左列为 MAAUnified 现有 key（保持稳定，不随文案改名；`gui.json` / 任务参数中的迁移键值同样不动），
右列为 WPF dev-v2 的点分 key，作为后续批次迁移与新增文案的目标名。
本波（P1-32）已把 Roguelike 主题 / 模式 / 难度 / squad 接入 `LocalizedObservableList` 条目链，但资源 key 仍是旧的平铺名。

### Roguelike 主题

| MAAUnified | WPF |
| --- | --- |
| `RoguelikeThemePhantom` | `Roguelike.Theme.Phantom` |
| `RoguelikeThemeMizuki` | `Roguelike.Theme.Mizuki` |
| `RoguelikeThemeSami` | `Roguelike.Theme.Sami` |
| `RoguelikeThemeSarkaz` | `Roguelike.Theme.Sarkaz` |
| `RoguelikeThemeJieGarden` | `Roguelike.Theme.JieGarden` |

WPF 的 `Roguelike.Theme.*.Tip`（主题推荐配置提示）在 MAAUnified 尚无对应 key，新增时直接使用该名。

### Roguelike 模式（策略）

| MAAUnified | WPF |
| --- | --- |
| `RoguelikeStrategyExp` | `Roguelike.Strategy.Exp` |
| `RoguelikeStrategyGold` | `Roguelike.Strategy.Gold` |
| `RoguelikeStrategyLastReward` | `Roguelike.Strategy.LastReward` |
| `RoguelikeStrategyMonthlySquad` | `Roguelike.Strategy.MonthlySquad` |
| `RoguelikeStrategyDeepExploration` | `Roguelike.Strategy.DeepExploration` |
| `RoguelikeStrategyCollapse` | `Roguelike.Strategy.Collapse` |
| `RoguelikeStrategyFindPlaytime` | `Roguelike.Strategy.FindPlaytime` |

### Roguelike squad

| MAAUnified | WPF |
| --- | --- |
| `First-ClassSquad` | `Roguelike.Squad.FirstClass` |
| `GatheringSquad` | `Roguelike.Squad.Gathering` |
| `IS2NewSquad1` | `Roguelike.Squad.IS2NewSquad1` |
| `IS2NewSquad2` | `Roguelike.Squad.IS2NewSquad2` |
| `IS2NewSquad3` | `Roguelike.Squad.IS2NewSquad3` |
| `IS3NewSquad1` | `Roguelike.Squad.IS3NewSquad1` |
| `IS3NewSquad2` | `Roguelike.Squad.IS3NewSquad2` |
| `IS3NewSquad3` | `Roguelike.Squad.IS3NewSquad3` |
| `IS3NewSquad4` | `Roguelike.Squad.IS3NewSquad4` |
| `IS4NewSquad1` | `Roguelike.Squad.IS4NewSquad1` |
| `IS4NewSquad2` | `Roguelike.Squad.IS4NewSquad2` |
| `IS4NewSquad3` | `Roguelike.Squad.IS4NewSquad3` |
| `IS4NewSquad4` | `Roguelike.Squad.IS4NewSquad4` |
| `IS4NewSquad5` | `Roguelike.Squad.IS4NewSquad5` |
| `IS4NewSquad6` | `Roguelike.Squad.IS4NewSquad6` |
| `IS4NewSquad7` | `Roguelike.Squad.IS4NewSquad7` |
| `IS4NewSquad8` | `Roguelike.Squad.IS4NewSquad8` |
| `IS5NewSquad1` | `Roguelike.Squad.IS5NewSquad1` |
| `IS5NewSquad10` | `Roguelike.Squad.IS5NewSquad10` |
| `IS5NewSquad11` | `Roguelike.Squad.IS5NewSquad11` |
| `IS5NewSquad2` | `Roguelike.Squad.IS5NewSquad2` |
| `IS5NewSquad3` | `Roguelike.Squad.IS5NewSquad3` |
| `IS5NewSquad4` | `Roguelike.Squad.IS5NewSquad4` |
| `IS5NewSquad5` | `Roguelike.Squad.IS5NewSquad5` |
| `IS5NewSquad6` | `Roguelike.Squad.IS5NewSquad6` |
| `IS5NewSquad7` | `Roguelike.Squad.IS5NewSquad7` |
| `IS5NewSquad8` | `Roguelike.Squad.IS5NewSquad8` |
| `IS5NewSquad9` | `Roguelike.Squad.IS5NewSquad9` |
| `LeaderSquad` | `Roguelike.Squad.Leader` |
| `ResearchSquad` | `Roguelike.Squad.Research` |
| `SpearheadSquad` | `Roguelike.Squad.Spearhead` |
| `SpecialForceSquad` | `Roguelike.Squad.SpecialForce` |
| `SupportSquad` | `Roguelike.Squad.Support` |
| `TacticalAssaultOperative` | `Roguelike.Squad.TacticalAssault` |
| `TacticalDestructionOperative` | `Roguelike.Squad.TacticalDestruction` |
| `TacticalFortificationOperative` | `Roguelike.Squad.TacticalFortification` |
| `TacticalRangedOperative` | `Roguelike.Squad.TacticalRanged` |

### 难度与其他

| MAAUnified | WPF | 说明 |
| --- | --- | --- |
| `NotSwitch` | `TaskQueue.NotSwitch` | 难度档位 ｢不切换｣；数值档位（`MIN (0)` / `MAX (n)` / 数字）为字面量，WPF 同样无 key |
| `Roguelike.FindPlaytimeTarget` | `Roguelike.FindPlaytimeTarget` | 同名 |
| `RoguelikePlaytimeLing` / `RoguelikePlaytimeShu` / `RoguelikePlaytimeNian` | `Roguelike.Playtime.Ling` / `.Shu` / `.Nian` | 规划迁移，先保留旧名 |

### 分域迁移批次（规划）

| 批次 | MAAUnified 域 | WPF 目标前缀 | 说明 |
| --- | --- | --- | --- |
| 1 | `Roguelike*` | `Roguelike.*` | 本波已接条目链，下一步按上表改名并同步五语言 |
| 2 | `Fight*` / `TaskQueue*` | `Fight.*` / `TaskQueue.*` | 与任务队列波次协同，避免同文件并行改名 |
| 3 | `Settings*` | `Settings.*` | 同名较多（见上表），优先补齐缺语言条目 |
| 4 | `Copilot*` / `Toolbox*` | `Copilot.*` / `Toolbox.*` | 按界面域分批 |
| 5 | 其余平铺 UI 文案 | 按界面域 | 低优先级；禁止一次性全量改名 |

## 审计与排序

- `Tests/LocalizationResxAuditTests` 校验：新增 key 满足 `Domain.Topic.Name[.Tip]`；五语言集合一致（`qps-Ploc` 允许子集且不得超出 zh-cn）；
  新增 key 不得使文件内字典序乱序数量超过冻结预算。
- `Tests/Localization/LocalizationResxBaseline.json` 冻结接入前的漂移（213 个非规范 key、各语言既有缺失、既有重复 key 与乱序数量），
  由 `python3 CI/update-localization-audit-baseline.py` 重新生成；该脚本只应在有意增删 key 后运行。
- 排序按 Ordinal 字典序。历史文件仍有约 200 处乱序，本波不整体重排（避免与并行波次冲突）；新增 key 必须插到正确位置，
  否则乱序数量增加即失败。
- 旧 key 迁移时必须同步五语言资源并更新基线；本地化 key 之外的**配置迁移键值**保持稳定，不随文案改名。
