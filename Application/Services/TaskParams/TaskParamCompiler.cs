using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services.Features;
using MAAUnified.Compat.Runtime;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Application.Services.TaskParams;

public sealed class TaskCompileOutput
{
    public required string NormalizedType { get; init; }

    public required JsonObject Params { get; init; }

    public required IReadOnlyList<TaskValidationIssue> Issues { get; init; }

    public bool HasBlockingIssues => Issues.Any(i => i.Blocking);
}

public static class TaskParamCompiler
{
    private const string UiStagePlan = "_ui_stage_plan";
    private const string UiIsStageManually = "_ui_is_stage_manually";
    private const string UiUseMedicine = "_ui_use_medicine";
    private const string UiUseStone = "_ui_use_stone";
    private const string UiEnableTimesLimit = "_ui_enable_times_limit";
    private const string UiEnableTargetDrop = "_ui_enable_target_drop";
    private const string UiDropId = "_ui_drop_id";
    private const string UiDropCount = "_ui_drop_count";
    private const string UiIsInventoryTarget = "_ui_is_inventory_target";
    private const string UiUseExpireMedicineForActivity = "_ui_use_expire_medicine_for_activity";
    private const string UiUseAlternateStage = "_ui_use_alternate_stage";
    private const string UiHideUnavailableStage = "_ui_hide_unavailable_stage";
    private const string UiStageResetMode = "_ui_stage_reset_mode";
    private const string UiUseCustomAnnihilation = "_ui_use_custom_annihilation";
    private const string UiAnnihilationStage = "_ui_annihilation_stage";
    private const string UiHideSeries = "_ui_hide_series";
    private const string UiAllowUseStoneSave = "_ui_allow_use_stone_save";
    private const string UiUseWeeklySchedule = "_ui_use_weekly_schedule";
    private const string UiWeeklyScheduleSunday = "_ui_weekly_schedule_sunday";
    private const string UiWeeklyScheduleMonday = "_ui_weekly_schedule_monday";
    private const string UiWeeklyScheduleTuesday = "_ui_weekly_schedule_tuesday";
    private const string UiWeeklyScheduleWednesday = "_ui_weekly_schedule_wednesday";
    private const string UiWeeklyScheduleThursday = "_ui_weekly_schedule_thursday";
    private const string UiWeeklyScheduleFriday = "_ui_weekly_schedule_friday";
    private const string UiWeeklyScheduleSaturday = "_ui_weekly_schedule_saturday";
    private const string UiMallCreditFightLastTime = "_ui_mall_credit_fight_last_time";
    private const string UiMallVisitFriendsLastTime = "_ui_mall_visit_friends_last_time";
    private const string UserDataUpdateOperBox = "update_oper_box";
    private const string UserDataUpdateDepot = "update_depot";
    private const string UserDataUpdateTriggerInterval = "trigger_interval";
    private const string UiRecruitPreserveTagsEnabled = "_ui_preserve_tags_enabled";
    private const string SkipAppendIssueCode = "TaskCompileSkipAppend";
    private static readonly Regex RoguelikeSeedRegex = new("^[0-9A-Za-z]+,rogue_\\d+,\\d+$", RegexOptions.Compiled);
    private static readonly HashSet<int> RoguelikeModes = [0, 1, 4, 5, 6, 7, 20001];
    private static readonly HashSet<string> RoguelikeThemes = new(StringComparer.OrdinalIgnoreCase) { "JieGarden", "Phantom", "Mizuki", "Sami", "Sarkaz" };
    private static readonly HashSet<string> ReclamationThemes = new(StringComparer.OrdinalIgnoreCase) { "Tales", "Fire", "RelaunchAnchor" };
    private static readonly HashSet<int> ReclamationModes = [0, 1, 16, 32, 48];
    private static readonly HashSet<int> ReclamationProsperityModes = [0, 1];
    private static readonly HashSet<int> ReclamationRelaunchAnchorModes = [16, 32, 48];
    private static readonly HashSet<string> RoguelikeProfessionalSquads = new(StringComparer.Ordinal)
    {
        "突击战术分队",
        "堡垒战术分队",
        "远程战术分队",
        "破坏战术分队",
    };
    private static readonly HashSet<string> CustomKnownTaskTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartUp",
        "CloseDown",
        "Fight",
        "Recruit",
        "Infrast",
        "Mall",
        "Award",
        "Roguelike",
        "Reclamation",
        "UserDataUpdate",
        "SingleStep",
        "Custom",
        "PostAction",
    };
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> MallBlacklistByClientType =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Official"] = ["讯使", "嘉维尔", "坚雷"],
            ["Bilibili"] = ["讯使", "嘉维尔", "坚雷"],
            ["EN"] = ["Courier", "Gavial", "Dur-nar"],
            ["JP"] = ["クーリエ", "ガヴィル", "ジュナー"],
            ["KR"] = ["쿠리어", "가비알", "듀나"],
            ["Txwy"] = ["訊使", "嘉維爾", "堅雷"],
        };

    public static string NormalizeTaskType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "Unknown";
        }

        var normalized = type.Trim();
        if (normalized.EndsWith("Task", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized switch
        {
            "StartUp" => "StartUp",
            "CloseDown" => "CloseDown",
            "Fight" => "Fight",
            "Recruit" => "Recruit",
            "Infrast" => "Infrast",
            "Mall" => "Mall",
            "Award" => "Award",
            "Roguelike" => "Roguelike",
            "Reclamation" => "Reclamation",
            "UserDataUpdate" => "UserDataUpdate",
            "SingleStep" => "SingleStep",
            "Custom" => "Custom",
            "PostAction" => "PostAction",
            _ => normalized,
        };
    }

    public static (string NormalizedType, JsonObject Params) NormalizeTypeAndCreateDefaultParams(
        string type,
        UnifiedProfile profile,
        UnifiedConfig config)
    {
        var normalizedType = NormalizeTaskType(type);

        return normalizedType switch
        {
            "StartUp" => (normalizedType, CompileStartUp(new StartUpTaskParamsDto(), profile, config).Params),
            "Fight" => (normalizedType, CompileFight(new FightTaskParamsDto(), profile, config).Params),
            "Recruit" => (normalizedType, CompileRecruit(new RecruitTaskParamsDto(), profile, config).Params),
            "Roguelike" => (normalizedType, CompileRoguelike(new RoguelikeTaskParamsDto(), profile, config).Params),
            "Reclamation" => (normalizedType, CompileReclamation(new ReclamationTaskParamsDto(), profile, config).Params),
            "UserDataUpdate" => (normalizedType, CompileUserDataUpdate(new UserDataUpdateTaskParamsDto()).Params),
            "SingleStep" => (normalizedType, CompileSingleStep(new SingleStepTaskParamsDto()).Params),
            "Custom" => (normalizedType, CompileCustom(new CustomTaskParamsDto(), profile, config).Params),
            _ => (normalizedType, new JsonObject()),
        };
    }

    public static (StartUpTaskParamsDto Dto, IReadOnlyList<TaskValidationIssue> Issues) ReadStartUp(
        UnifiedTaskItem task,
        UnifiedProfile profile,
        UnifiedConfig config,
        bool strict)
    {
        var issues = new List<TaskValidationIssue>();
        var parameters = task.Params ?? new JsonObject();
        var profileClientType = ResolveStringSetting(profile, config, "ClientType", "Start.ClientType");
        var clientType = !string.IsNullOrWhiteSpace(profileClientType)
            ? profileClientType
            : ReadString(parameters, "client_type", strict, issues, "start_up.client_type", "Official");
        var startGameEnabled = TryResolveBooleanSetting(profile, config, out var profileStartGame, "StartGame", "Start.StartGame")
            ? profileStartGame
            : ReadBool(parameters, "start_game_enabled", strict, issues, "start_up.start_game_enabled", true);

        var dto = new StartUpTaskParamsDto
        {
            ClientType = clientType,
            StartGameEnabled = startGameEnabled,
            AccountName = ReadString(parameters, "account_name", strict, issues, "start_up.account_name", string.Empty),
            ConnectConfig = ResolveStringSetting(profile, config, "ConnectConfig", "Connect.ConnectConfig") ?? "General",
            ConnectAddress = ResolveStringSetting(profile, config, "ConnectAddress", "Connect.Address") ?? "127.0.0.1:5555",
            AdbPath = ResolveStringSetting(profile, config, "AdbPath", "Connect.AdbPath") ?? string.Empty,
            MacUseBundledAdb = MacBundledAdbPolicy.ReadUseBundledAdb(profile),
            TouchMode = ResolveStringSetting(profile, config, "TouchMode", "Connect.TouchMode") ?? "MaaFwAdb",
            AutoDetectConnection = ResolveBooleanSetting(profile, config, true, "AutoDetect", "Connect.AutoDetect"),
            AttachWindowScreencapMethod = ResolveStringSetting(profile, config, "AttachWindowScreencapMethod", "Connect.AttachWindow.ScreencapMethod") ?? "2",
            AttachWindowMouseMethod = ResolveStringSetting(profile, config, "AttachWindowMouseMethod", "Connect.AttachWindow.MouseMethod") ?? "64",
            AttachWindowKeyboardMethod = ResolveStringSetting(profile, config, "AttachWindowKeyboardMethod", "Connect.AttachWindow.KeyboardMethod") ?? "64",
        };

        return (dto, issues);
    }

    public static TaskCompileOutput CompileStartUp(
        StartUpTaskParamsDto dto,
        UnifiedProfile profile,
        UnifiedConfig config)
    {
        var issues = new List<TaskValidationIssue>();

        var clientType = (dto.ClientType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(clientType))
        {
            issues.Add(new TaskValidationIssue("ClientTypeMissing", "start_up.client_type", "Client type cannot be empty."));
            clientType = "Official";
        }

        var startGameEnabled = dto.StartGameEnabled;
        if (string.Equals(dto.ConnectConfig, "PC", StringComparison.OrdinalIgnoreCase))
        {
            startGameEnabled = false;
        }

        var accountName = (dto.AccountName ?? string.Empty).Trim();
        if (!string.Equals(clientType, "Official", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(clientType, "Bilibili", StringComparison.OrdinalIgnoreCase))
        {
            accountName = string.Empty;
        }

        return new TaskCompileOutput
        {
            NormalizedType = "StartUp",
            Params = new JsonObject
            {
                ["client_type"] = clientType,
                ["start_game_enabled"] = startGameEnabled,
                ["account_name"] = accountName,
            },
            Issues = issues,
        };
    }

    public static (FightTaskParamsDto Dto, IReadOnlyList<TaskValidationIssue> Issues) ReadFight(
        UnifiedTaskItem task,
        bool strict)
    {
        var issues = new List<TaskValidationIssue>();
        var parameters = task.Params ?? new JsonObject();

        var storedStage = FightStageSelection.NormalizeStoredValue(
            ReadString(parameters, "stage", strict, issues, "fight.stage", FightStageSelection.CurrentOrLast));
        var medicine = ReadInt(parameters, "medicine", strict, issues, "fight.medicine", 0);
        var stone = ReadInt(parameters, "stone", strict, issues, "fight.stone", 0);
        var medicineExpireDays = ReadInt(parameters, "medicine_expire_days", false, issues, "fight.medicine_expire_days", 0);
        var legacyExpiringMedicine = ReadInt(parameters, "expiring_medicine", false, issues, "fight.expiring_medicine", 0);
        var expiringMedicine = medicineExpireDays > 0 ? medicineExpireDays : legacyExpiringMedicine;
        var times = ReadInt(parameters, "times", strict, issues, "fight.times", int.MaxValue);
        var series = ReadInt(parameters, "series", strict, issues, "fight.series", 1);

        string dropId = string.Empty;
        var dropCount = 1;
        if (parameters["drops"] is JsonObject drops)
        {
            var firstDrop = drops.FirstOrDefault();
            dropId = firstDrop.Key ?? string.Empty;
            if (firstDrop.Value is JsonValue value && value.TryGetValue(out int count))
            {
                dropCount = count;
            }
        }

        if (string.IsNullOrWhiteSpace(dropId))
        {
            dropId = ReadString(parameters, UiDropId, false, issues, "fight.drop_id", string.Empty);
        }

        if (parameters.TryGetPropertyValue(UiDropCount, out var uiDropCountNode)
            && uiDropCountNode is JsonValue uiDropCountValue
            && uiDropCountValue.TryGetValue(out int uiDropCount))
        {
            dropCount = uiDropCount;
        }

        var useCustomAnnihilation = ReadBool(parameters, UiUseCustomAnnihilation, false);
        var annihilationStage = ReadString(parameters, UiAnnihilationStage, false, issues, "fight.annihilation_stage", "Annihilation");
        var stagePlan = ReadFightStagePlan(parameters, strict, issues, storedStage);
        var isStageManually = ReadBool(parameters, UiIsStageManually, false);
        var stage = ResolveFightDisplayStage(storedStage);

        var dto = new FightTaskParamsDto
        {
            Stage = stage,
            StagePlan = stagePlan,
            IsStageManually = isStageManually,
            Medicine = Math.Max(0, medicine),
            UseMedicine = ReadNullableBool(parameters, UiUseMedicine, medicine > 0, issues, "fight.use_medicine"),
            Stone = Math.Max(0, stone),
            UseStone = ReadNullableBool(parameters, UiUseStone, stone > 0, issues, "fight.use_stone"),
            Times = times,
            EnableTimesLimit = ReadNullableBool(parameters, UiEnableTimesLimit, times != int.MaxValue, issues, "fight.enable_times_limit"),
            Series = series,
            IsDrGrandet = ReadBool(parameters, "DrGrandet", false),
            UseExpiringMedicine = expiringMedicine > 0,
            ExpiringMedicine = expiringMedicine > 0 ? expiringMedicine : 9999,
            UseExpireMedicineForActivity = ReadBool(parameters, UiUseExpireMedicineForActivity, false),
            EnableTargetDrop = ReadNullableBool(parameters, UiEnableTargetDrop, !string.IsNullOrWhiteSpace(dropId), issues, "fight.enable_target_drop"),
            DropId = dropId,
            DropCount = Math.Max(1, dropCount),
            IsInventoryTarget = ReadBool(parameters, UiIsInventoryTarget, false),
            UseCustomAnnihilation = useCustomAnnihilation,
            AnnihilationStage = annihilationStage,
            UseAlternateStage = ReadBool(parameters, UiUseAlternateStage, false),
            HideUnavailableStage = ReadBool(parameters, UiHideUnavailableStage, true),
            StageResetMode = ReadString(parameters, UiStageResetMode, false, issues, "fight.stage_reset_mode", "Current"),
            HideSeries = ReadBool(parameters, UiHideSeries, false),
            AllowUseStoneSave = ReadBool(parameters, UiAllowUseStoneSave, false),
            UseWeeklySchedule = ReadBool(parameters, UiUseWeeklySchedule, false),
            WeeklyScheduleSunday = ReadBool(parameters, UiWeeklyScheduleSunday, false, issues, "fight.weekly_schedule.sunday", true),
            WeeklyScheduleMonday = ReadBool(parameters, UiWeeklyScheduleMonday, false, issues, "fight.weekly_schedule.monday", true),
            WeeklyScheduleTuesday = ReadBool(parameters, UiWeeklyScheduleTuesday, false, issues, "fight.weekly_schedule.tuesday", true),
            WeeklyScheduleWednesday = ReadBool(parameters, UiWeeklyScheduleWednesday, false, issues, "fight.weekly_schedule.wednesday", true),
            WeeklyScheduleThursday = ReadBool(parameters, UiWeeklyScheduleThursday, false, issues, "fight.weekly_schedule.thursday", true),
            WeeklyScheduleFriday = ReadBool(parameters, UiWeeklyScheduleFriday, false, issues, "fight.weekly_schedule.friday", true),
            WeeklyScheduleSaturday = ReadBool(parameters, UiWeeklyScheduleSaturday, false, issues, "fight.weekly_schedule.saturday", true),
        };

        return (dto, issues);
    }

    public static TaskCompileOutput CompileFight(
        FightTaskParamsDto dto,
        UnifiedProfile profile,
        UnifiedConfig config,
        DateTime? nowUtc = null)
    {
        var issues = new List<TaskValidationIssue>();

        var useAlternateStage = dto.UseAlternateStage;
        var hideUnavailableStage = dto.HideUnavailableStage;
        var stageResetMode = string.IsNullOrWhiteSpace(dto.StageResetMode) ? "Current" : dto.StageResetMode;
        if (!string.Equals(stageResetMode, "Current", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stageResetMode, "Ignore", StringComparison.OrdinalIgnoreCase))
        {
            stageResetMode = "Current";
        }

        if (useAlternateStage)
        {
            hideUnavailableStage = false;
            stageResetMode = "Ignore";
        }

        if (hideUnavailableStage)
        {
            useAlternateStage = false;
            stageResetMode = "Current";
        }

        var useMedicine = dto.UseMedicine;
        var useStone = dto.UseStone;
        var enableTimesLimit = dto.EnableTimesLimit;
        var enableTargetDrop = dto.EnableTargetDrop;
        var rawStagePlan = dto.StagePlan.Count > 0 ? dto.StagePlan : [];
        if (rawStagePlan.Count == 0
            || (rawStagePlan.Count == 1
                && FightStageSelection.IsCurrentOrLast(rawStagePlan[0])
                && !FightStageSelection.IsCurrentOrLast(dto.Stage)))
        {
            rawStagePlan = [dto.Stage];
        }

        var stagePlan = FightStageSelection.NormalizeStagePlan(rawStagePlan);

        if (!useAlternateStage && stagePlan.Count > 1)
        {
            stagePlan = [stagePlan[0]];
        }

        var stage = ResolveFightExecutionStage(
            stagePlan,
            dto.IsStageManually,
            useAlternateStage,
            stageResetMode,
            dto.UseCustomAnnihilation,
            dto.AnnihilationStage,
            ResolveStringSetting(profile, config, "ClientType", "Start.ClientType") ?? "Official");

        if (dto.Series is < -1 or > 6)
        {
            issues.Add(new TaskValidationIssue("FightSeriesOutOfRange", "fight.series", "Fight series must be between -1 and 6."));
        }

        if (dto.Times < 0)
        {
            issues.Add(new TaskValidationIssue("FightTimesOutOfRange", "fight.times", "Fight times must be greater than or equal to zero."));
        }

        if (enableTargetDrop != false && string.IsNullOrWhiteSpace(dto.DropId))
        {
            issues.Add(new TaskValidationIssue("FightDropMissing", "fight.drop_id", "Target drop id cannot be empty when target drop is enabled."));
        }

        if (enableTimesLimit != false && dto.Series > 0 && dto.Times > 0 && dto.Times % dto.Series != 0)
        {
            issues.Add(new TaskValidationIssue(
                "FightTimesMayNotExhausted",
                "fight.times",
                "Fight times may not be fully exhausted under current series.",
                Blocking: false));
        }

        if (string.Equals(stage, "Annihilation", StringComparison.OrdinalIgnoreCase)
            && dto.UseCustomAnnihilation
            && !string.IsNullOrWhiteSpace(dto.AnnihilationStage))
        {
            stage = dto.AnnihilationStage.Trim();
        }

        var expiringMedicine = dto.UseExpiringMedicine ? Math.Max(1, dto.ExpiringMedicine) : 0;
        var activityExpireDays = ResolveActivityExpireMedicineDays(dto, profile, config, nowUtc);
        var effectiveMedicineExpireDays = Math.Max(expiringMedicine, activityExpireDays);

        var parameters = new JsonObject
        {
            ["stage"] = stage,
            ["medicine"] = useMedicine != false ? Math.Max(0, dto.Medicine) : 0,
            ["expiring_medicine"] = effectiveMedicineExpireDays,
            ["medicine_expire_days"] = effectiveMedicineExpireDays,
            ["stone"] = useStone != false ? Math.Max(0, dto.Stone) : 0,
            ["times"] = enableTimesLimit != false ? Math.Max(0, dto.Times) : int.MaxValue,
            ["series"] = dto.Series,
            ["DrGrandet"] = dto.IsDrGrandet,
            ["report_to_penguin"] = ResolveBooleanSetting(profile, config, "EnablePenguin"),
            ["report_to_yituliu"] = ResolveBooleanSetting(profile, config, "EnableYituliu"),
            ["penguin_id"] = ResolveStringSetting(profile, config, "PenguinId") ?? string.Empty,
            ["yituliu_id"] = ResolveYituliuId(profile, config),
            ["server"] = ResolveStringSetting(profile, config, "ServerType") ?? "CN",
            ["client_type"] = ResolveStringSetting(profile, config, "ClientType", "Start.ClientType") ?? "Official",
            [UiStagePlan] = ToJsonArray(stagePlan),
            [UiIsStageManually] = dto.IsStageManually,
            [UiUseMedicine] = JsonValue.Create(useMedicine),
            [UiUseStone] = JsonValue.Create(useStone),
            [UiEnableTimesLimit] = JsonValue.Create(enableTimesLimit),
            [UiEnableTargetDrop] = JsonValue.Create(enableTargetDrop),
            [UiDropId] = dto.DropId.Trim(),
            [UiDropCount] = Math.Max(1, dto.DropCount),
            [UiIsInventoryTarget] = dto.IsInventoryTarget,
            [UiUseExpireMedicineForActivity] = dto.UseExpireMedicineForActivity,
            [UiUseAlternateStage] = useAlternateStage,
            [UiHideUnavailableStage] = hideUnavailableStage,
            [UiStageResetMode] = stageResetMode,
            [UiUseCustomAnnihilation] = dto.UseCustomAnnihilation,
            [UiAnnihilationStage] = dto.AnnihilationStage,
            [UiHideSeries] = dto.HideSeries,
            [UiAllowUseStoneSave] = dto.AllowUseStoneSave,
            [UiUseWeeklySchedule] = dto.UseWeeklySchedule,
            [UiWeeklyScheduleSunday] = dto.WeeklyScheduleSunday,
            [UiWeeklyScheduleMonday] = dto.WeeklyScheduleMonday,
            [UiWeeklyScheduleTuesday] = dto.WeeklyScheduleTuesday,
            [UiWeeklyScheduleWednesday] = dto.WeeklyScheduleWednesday,
            [UiWeeklyScheduleThursday] = dto.WeeklyScheduleThursday,
            [UiWeeklyScheduleFriday] = dto.WeeklyScheduleFriday,
            [UiWeeklyScheduleSaturday] = dto.WeeklyScheduleSaturday,
        };

        if (enableTargetDrop != false && !string.IsNullOrWhiteSpace(dto.DropId))
        {
            var dropId = dto.DropId.Trim();
            var dropCount = Math.Max(1, dto.DropCount);
            if (dto.IsInventoryTarget)
            {
                var depotCounts = ReadDepotCounts(config);
                if (depotCounts.Count == 0)
                {
                    issues.Add(new TaskValidationIssue(
                        "FightInventoryTargetDepotMissing",
                        "fight.drops",
                        "Target inventory mode requires depot data. Update depot recognition before starting.",
                        Blocking: false));
                    dropCount = 0;
                }
                else
                {
                    if (!depotCounts.TryGetValue(dropId, out var currentInventory) || currentInventory < 0)
                    {
                        currentInventory = 0;
                    }

                    dropCount = Math.Max(dropCount - currentInventory, 0);
                    if (dropCount <= 0)
                    {
                        issues.Add(new TaskValidationIssue(
                            "FightInventoryTargetReached",
                            "fight.drops",
                            $"Target inventory for drop `{dropId}` is already reached.",
                            Blocking: false));
                    }
                }
            }

            if (dropCount > 0)
            {
                parameters["drops"] = new JsonObject
                {
                    [dropId] = dropCount,
                };
            }
            else if (dto.IsInventoryTarget)
            {
                issues.Add(new TaskValidationIssue(
                    SkipAppendIssueCode,
                    "fight.drops",
                    "Target inventory mode resolved to no runnable fight task.",
                    Blocking: false));
            }
        }

        return new TaskCompileOutput
        {
            NormalizedType = "Fight",
            Params = parameters,
            Issues = issues,
        };
    }

    private static List<string> ReadFightStagePlan(
        JsonObject parameters,
        bool strict,
        ICollection<TaskValidationIssue> issues,
        string fallbackStage)
    {
        if (!parameters.TryGetPropertyValue(UiStagePlan, out var stagePlanNode) || stagePlanNode is null)
        {
            return FightStageSelection.NormalizeStagePlan([fallbackStage]);
        }

        if (stagePlanNode is not JsonArray stagePlanArray)
        {
            if (strict)
            {
                issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", "fight.stage_plan", $"Task field `{UiStagePlan}` has incompatible type."));
            }

            return FightStageSelection.NormalizeStagePlan([fallbackStage]);
        }

        var list = new List<string>(stagePlanArray.Count);
        foreach (var entry in stagePlanArray)
        {
            switch (entry)
            {
                case JsonValue value when value.TryGetValue(out string? text):
                    list.Add(FightStageSelection.NormalizePlanEntry(text));
                    break;
                case JsonValue value when value.TryGetValue(out bool _):
                    issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", "fight.stage_plan", $"Task field `{UiStagePlan}` contains incompatible values."));
                    break;
                case null:
                    list.Add(FightStageSelection.CurrentOrLast);
                    break;
                default:
                    issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", "fight.stage_plan", $"Task field `{UiStagePlan}` contains incompatible values."));
                    break;
            }
        }

        return FightStageSelection.NormalizeStagePlan(list);
    }

    private static int ResolveActivityExpireMedicineDays(
        FightTaskParamsDto dto,
        UnifiedProfile profile,
        UnifiedConfig config,
        DateTime? nowUtc = null)
    {
        if (!dto.UseExpireMedicineForActivity)
        {
            return 0;
        }

        var timestamp = nowUtc ?? DateTime.UtcNow;
        var clientType = ResolveStringSetting(profile, config, "ClientType", "Start.ClientType") ?? "Official";
        if (!IsAnyActivityExpiringWithin48Hours(clientType, timestamp))
        {
            return 0;
        }

        var yjDate = MallDailyResetHelper.GetYjDate(timestamp, clientType);
        return ((7 - (int)yjDate.DayOfWeek + 7) % 7) + 1;
    }

    private static bool IsAnyActivityExpiringWithin48Hours(string clientType, DateTime nowUtc)
    {
        foreach (var path in ResolveStageActivityCandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                {
                    continue;
                }

                if (TryResolveStageActivityClientNode(root, clientType, out var clientNode)
                    && IsActivityCollectionExpiringWithin48Hours(clientNode["sideStoryStage"], nowUtc))
                {
                    return true;
                }

                if (!string.Equals(clientType, "Official", StringComparison.OrdinalIgnoreCase)
                    && TryResolveStageActivityClientNode(root, "Official", out clientNode)
                    && IsActivityCollectionExpiringWithin48Hours(clientNode["sideStoryStage"], nowUtc))
                {
                    return true;
                }
            }
            catch
            {
                continue;
            }
        }

        return false;
    }

    private static IEnumerable<string> ResolveStageActivityCandidatePaths()
    {
        foreach (var root in EnumerateRuntimeRoots())
        {
            yield return Path.Combine(root, "cache", "gui", "StageActivityV2.json");
            yield return Path.Combine(root, "gui", "StageActivityV2.json");
            yield return Path.Combine(root, "resource", "gui", "StageActivityV2.json");
        }
    }

    private static IEnumerable<string> EnumerateRuntimeRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new[]
        {
            RuntimeLayout.ResolveRuntimeBaseDirectory(),
            Environment.CurrentDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        };

        foreach (var candidate in candidates.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            var current = new DirectoryInfo(Path.GetFullPath(candidate));
            while (current is not null)
            {
                if (seen.Add(current.FullName))
                {
                    yield return current.FullName;
                }

                current = current.Parent;
            }
        }
    }

    private static bool TryResolveStageActivityClientNode(JsonObject root, string clientType, out JsonObject clientNode)
    {
        clientNode = null!;
        foreach (var pair in root)
        {
            if (string.Equals(NormalizeFightClientType(pair.Key), NormalizeFightClientType(clientType), StringComparison.OrdinalIgnoreCase)
                && pair.Value is JsonObject node)
            {
                clientNode = node;
                return true;
            }
        }

        return false;
    }

    private static bool IsActivityCollectionExpiringWithin48Hours(JsonNode? sideStoryNode, DateTime nowUtc)
    {
        if (sideStoryNode is not JsonObject sideStoryObject)
        {
            return false;
        }

        foreach (var group in sideStoryObject)
        {
            if (group.Value is not JsonObject groupObject)
            {
                continue;
            }

            var activity = groupObject["Activity"] ?? groupObject["activity"];
            if (IsActivityExpiringWithin48Hours(activity, nowUtc))
            {
                return true;
            }

            var stageArray = groupObject["Stages"] as JsonArray ?? groupObject["stages"] as JsonArray;
            if (stageArray is null)
            {
                continue;
            }

            foreach (var stageNode in stageArray)
            {
                if (stageNode is JsonObject stage
                    && IsActivityExpiringWithin48Hours(stage["Activity"] ?? stage["activity"] ?? activity, nowUtc))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsActivityExpiringWithin48Hours(JsonNode? activityNode, DateTime nowUtc)
    {
        if (activityNode is not JsonObject activity
            || !TryReadActivityTime(activity, "UtcExpireTime", out var expireTime))
        {
            return false;
        }

        var remaining = expireTime - nowUtc;
        return remaining > TimeSpan.Zero && remaining <= TimeSpan.FromHours(48);
    }

    private static bool TryReadActivityTime(JsonObject activity, string key, out DateTime utcTime)
    {
        utcTime = default;
        if (!TryReadString(activity[key], out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var timezone = 0;
        if (activity["TimeZone"] is JsonValue timezoneValue)
        {
            _ = timezoneValue.TryGetValue(out timezone);
        }

        if (DateTime.TryParseExact(
                raw,
                "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            utcTime = DateTime.SpecifyKind(parsed.AddHours(-timezone), DateTimeKind.Utc);
            return true;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
        {
            utcTime = parsed.ToUniversalTime();
            return true;
        }

        return false;
    }

    private static IReadOnlyDictionary<string, int> ReadDepotCounts(UnifiedConfig? config)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (config is null
            || !config.GlobalValues.TryGetValue(LegacyConfigurationKeys.DepotResult, out JsonNode? node)
            || node is null)
        {
            return result;
        }

        var raw = ExtractRawDepotPayload(node);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            TryReadDepotCountsFromPayload(raw, result);
        }

        return result;
    }

    private static string ExtractRawDepotPayload(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            return text ?? string.Empty;
        }

        return node.ToJsonString();
    }

    private static void TryReadDepotCountsFromPayload(string payload, IDictionary<string, int> counts)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(payload);
        }
        catch
        {
            return;
        }

        if (parsed is not JsonObject root)
        {
            return;
        }

        ReadFlatDepotCountMap(root, counts);

        if (root["data"] is JsonObject dataObject)
        {
            ReadFlatDepotCountMap(dataObject, counts);
        }
        else if (root["data"] is JsonValue dataValue && dataValue.TryGetValue(out string? dataText))
        {
            TryReadDepotCountsFromPayload(dataText ?? string.Empty, counts);
        }

        if (root["items"] is JsonArray itemsArray)
        {
            ReadDepotItemsArray(itemsArray, counts);
        }

        if (root["arkplanner"]?["object"]?["items"] is JsonArray arkPlannerItems)
        {
            ReadDepotItemsArray(arkPlannerItems, counts);
        }
    }

    private static void ReadFlatDepotCountMap(JsonObject source, IDictionary<string, int> counts)
    {
        foreach (var pair in source)
        {
            if (!int.TryParse(pair.Key, out _))
            {
                continue;
            }

            if (TryReadDepotCount(pair.Value, out var count))
            {
                counts[pair.Key] = count;
            }
        }
    }

    private static void ReadDepotItemsArray(JsonArray items, IDictionary<string, int> counts)
    {
        foreach (var node in items)
        {
            if (node is not JsonObject item
                || !TryReadString(item["id"], out var id)
                || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (TryReadDepotCount(item["have"], out var have))
            {
                counts[id] = have;
                continue;
            }

            if (TryReadDepotCount(item["count"], out var count))
            {
                counts[id] = count;
            }
        }
    }

    private static bool TryReadDepotCount(JsonNode? node, out int count)
    {
        count = 0;
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int intValue))
            {
                count = intValue;
                return true;
            }

            if (value.TryGetValue(out string? text) && int.TryParse(text, out intValue))
            {
                count = intValue;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? parsed) || string.IsNullOrWhiteSpace(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string ResolveFightDisplayStage(string storedStage)
    {
        return storedStage;
    }

    private static string ResolveFightExecutionStage(
        IReadOnlyList<string> stagePlan,
        bool isStageManually,
        bool useAlternateStage,
        string stageResetMode,
        bool useCustomAnnihilation,
        string annihilationStage,
        string clientType)
    {
        var normalizedPlan = FightStageSelection.NormalizeStagePlan(stagePlan);
        var normalizedClientType = NormalizeFightClientType(clientType);
        var currentDay = MallDailyResetHelper.GetYjDate(DateTime.UtcNow, normalizedClientType).DayOfWeek;

        if (isStageManually)
        {
            return FightStageSelection.NormalizeStoredValue(normalizedPlan[0]);
        }

        var stageManager = new StageManagerFeatureService();
        var availableStageCodes = stageManager.GetStageCodes(normalizedClientType).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in normalizedPlan)
        {
            if (FightStageSelection.IsCurrentOrLast(candidate))
            {
                return FightStageSelection.CurrentOrLast;
            }

            if (string.Equals(candidate, "Annihilation", StringComparison.OrdinalIgnoreCase))
            {
                return "Annihilation";
            }

            if (!availableStageCodes.Contains(candidate))
            {
                if (useAlternateStage)
                {
                    continue;
                }

                return string.Equals(stageResetMode, "Current", StringComparison.OrdinalIgnoreCase)
                    ? FightStageSelection.CurrentOrLast
                    : candidate;
            }

            if (IsFightStageOpenToday(candidate, currentDay))
            {
                return candidate;
            }

            if (!useAlternateStage)
            {
                return string.Equals(stageResetMode, "Current", StringComparison.OrdinalIgnoreCase)
                    ? FightStageSelection.CurrentOrLast
                    : candidate;
            }
        }

        var fallback = normalizedPlan[0];
        if (string.Equals(fallback, "Annihilation", StringComparison.OrdinalIgnoreCase)
            && useCustomAnnihilation
            && !string.IsNullOrWhiteSpace(annihilationStage))
        {
            return "Annihilation";
        }

        return FightStageSelection.NormalizeStoredValue(fallback);
    }

    private static string NormalizeFightClientType(string? clientType)
    {
        if (string.IsNullOrWhiteSpace(clientType)
            || string.Equals(clientType, "Official", StringComparison.OrdinalIgnoreCase)
            || string.Equals(clientType, "Bilibili", StringComparison.OrdinalIgnoreCase))
        {
            return "Official";
        }

        return clientType.Trim();
    }

    private static bool IsFightStageOpenToday(string stageCode, DayOfWeek dayOfWeek)
    {
        return stageCode switch
        {
            "CE-6" => dayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Thursday or DayOfWeek.Saturday or DayOfWeek.Sunday,
            "AP-5" => dayOfWeek is DayOfWeek.Monday or DayOfWeek.Thursday or DayOfWeek.Saturday or DayOfWeek.Sunday,
            "CA-5" => dayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Friday or DayOfWeek.Sunday,
            "SK-5" => dayOfWeek is DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday or DayOfWeek.Saturday,
            "PR-A-1" or "PR-A-2" => dayOfWeek is DayOfWeek.Monday or DayOfWeek.Thursday or DayOfWeek.Friday or DayOfWeek.Sunday,
            "PR-B-1" or "PR-B-2" => dayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Friday or DayOfWeek.Saturday,
            "PR-C-1" or "PR-C-2" => dayOfWeek is DayOfWeek.Wednesday or DayOfWeek.Thursday or DayOfWeek.Saturday or DayOfWeek.Sunday,
            "PR-D-1" or "PR-D-2" => dayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Saturday or DayOfWeek.Sunday,
            _ => true,
        };
    }

    public static (RecruitTaskParamsDto Dto, IReadOnlyList<TaskValidationIssue> Issues) ReadRecruit(
        UnifiedTaskItem task,
        bool strict)
    {
        var issues = new List<TaskValidationIssue>();
        var parameters = task.Params ?? new JsonObject();

        var confirm = ReadIntArray(parameters, "confirm");
        var select = ReadIntArray(parameters, "select");

        var recruitmentTime = parameters["recruitment_time"] as JsonObject ?? new JsonObject();
        var dto = new RecruitTaskParamsDto
        {
            Refresh = ReadBool(parameters, "refresh", strict, issues, "recruit.refresh", true),
            ForceRefresh = ReadBool(parameters, "force_refresh", strict, issues, "recruit.force_refresh", true),
            Times = ReadInt(parameters, "times", strict, issues, "recruit.times", 4),
            SetTime = ReadBool(parameters, "set_time", strict, issues, "recruit.set_time", true),
            UseExpedited = ReadBool(parameters, "expedite", false),
            SkipRobot = ReadBool(parameters, "skip_robot", false, issues, "recruit.skip_robot", true),
            ExtraTagsMode = ReadInt(parameters, "extra_tags_mode", false, issues, "recruit.extra_tags_mode", 0),
            FirstTags = ReadStringArray(parameters, "first_tags"),
            PreserveTagsEnabled = ReadRecruitPreserveTagsEnabled(parameters),
            PreserveTags = ReadRecruitPreserveTags(parameters),
            ChooseLevel3 = confirm.Contains(3),
            ChooseLevel4 = confirm.Contains(4),
            ChooseLevel5 = confirm.Contains(5),
            ChooseLevel6 = confirm.Contains(6) || select.Contains(6),
            Level3Time = ReadInt(recruitmentTime, "3", strict, issues, "recruit.time.3", 540),
            Level4Time = ReadInt(recruitmentTime, "4", strict, issues, "recruit.time.4", 540),
            Level5Time = ReadInt(recruitmentTime, "5", strict, issues, "recruit.time.5", 540),
        };

        if (confirm.Contains(1))
        {
            dto.SkipRobot = true;
        }

        if (select.Count == 0 && !confirm.Contains(4) && !confirm.Contains(5))
        {
            dto.ChooseLevel4 = false;
            dto.ChooseLevel5 = false;
        }

        return (dto, issues);
    }

    private static List<string> ReadRecruitPreserveTags(JsonObject parameters)
    {
        var preserveTags = ReadStringArray(parameters, "preserve_tags");
        if (preserveTags.Count > 0)
        {
            return preserveTags;
        }

        return ReadStringArray(parameters, "PreserveTags");
    }

    public static TaskCompileOutput CompileRecruit(
        RecruitTaskParamsDto dto,
        UnifiedProfile profile,
        UnifiedConfig config)
    {
        var issues = new List<TaskValidationIssue>();

        if (dto.Times < 0)
        {
            issues.Add(new TaskValidationIssue("RecruitTimesOutOfRange", "recruit.times", "Recruit times must be greater than or equal to zero."));
        }

        ValidateRecruitTime(dto.Level3Time, "recruit.time.3", issues);
        ValidateRecruitTime(dto.Level4Time, "recruit.time.4", issues);
        ValidateRecruitTime(dto.Level5Time, "recruit.time.5", issues);

        var refresh = dto.Refresh;
        var forceRefresh = dto.ForceRefresh;
        if (!refresh)
        {
            forceRefresh = false;
        }

        var select = new JsonArray();
        var confirm = new JsonArray();

        if (dto.SkipRobot)
        {
            confirm.Add(1);
        }

        if (dto.ChooseLevel3)
        {
            confirm.Add(3);
        }

        if (dto.ChooseLevel4)
        {
            select.Add(4);
            confirm.Add(4);
        }

        if (dto.ChooseLevel5)
        {
            select.Add(5);
            confirm.Add(5);
        }

        if (dto.ChooseLevel6)
        {
            select.Add(6);
            confirm.Add(6);
        }

        var preserveTags = dto.PreserveTagsEnabled ? NormalizeRecruitPreserveTags(dto.PreserveTags) : [];
        var parameters = new JsonObject
        {
            ["refresh"] = refresh,
            ["force_refresh"] = forceRefresh,
            ["select"] = select,
            ["confirm"] = confirm,
            ["times"] = Math.Max(0, dto.Times),
            ["set_time"] = dto.SetTime,
            ["expedite"] = dto.UseExpedited,
            ["skip_robot"] = dto.SkipRobot,
            ["extra_tags_mode"] = dto.ExtraTagsMode,
            ["first_tags"] = ToJsonArray(dto.FirstTags),
            ["preserve_tags"] = ToJsonArray(preserveTags),
            [UiRecruitPreserveTagsEnabled] = dto.PreserveTagsEnabled,
            ["recruitment_time"] = new JsonObject
            {
                ["3"] = ClampRecruitTime(dto.Level3Time),
                ["4"] = ClampRecruitTime(dto.Level4Time),
                ["5"] = ClampRecruitTime(dto.Level5Time),
            },
            ["report_to_penguin"] = ResolveBooleanSetting(profile, config, "EnablePenguin"),
            ["report_to_yituliu"] = ResolveBooleanSetting(profile, config, "EnableYituliu"),
            ["penguin_id"] = ResolveStringSetting(profile, config, "PenguinId") ?? string.Empty,
            ["yituliu_id"] = ResolveYituliuId(profile, config),
            ["server"] = ResolveStringSetting(profile, config, "ServerType") ?? "CN",
        };

        if (dto.UseExpedited)
        {
            parameters["expedite_times"] = Math.Max(0, dto.Times);
        }

        return new TaskCompileOutput
        {
            NormalizedType = "Recruit",
            Params = parameters,
            Issues = issues,
        };
    }

    private static bool ReadRecruitPreserveTagsEnabled(JsonObject parameters)
    {
        if (parameters.TryGetPropertyValue(UiRecruitPreserveTagsEnabled, out var enabledNode)
            && enabledNode is JsonValue enabledValue
            && enabledValue.TryGetValue(out bool enabled))
        {
            return enabled;
        }

        return ReadRecruitPreserveTags(parameters).Count > 0;
    }

    private static List<string> NormalizeRecruitPreserveTags(IEnumerable<string> tags)
    {
        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static (RoguelikeTaskParamsDto Dto, IReadOnlyList<TaskValidationIssue> Issues) ReadRoguelike(
        UnifiedTaskItem task,
        bool strict)
    {
        var issues = new List<TaskValidationIssue>();
        var parameters = task.Params ?? new JsonObject();

        var mode = ReadInt(parameters, "mode", strict, issues, "roguelike.mode", 0);

        var dto = new RoguelikeTaskParamsDto
        {
            Mode = mode,
            Theme = ReadString(parameters, "theme", strict, issues, "roguelike.theme", "JieGarden"),
            Difficulty = ReadInt(parameters, "difficulty", strict, issues, "roguelike.difficulty", int.MaxValue),
            StartsCount = ReadInt(parameters, "starts_count", strict, issues, "roguelike.starts_count", 999999),
            InvestmentEnabled = ReadBool(parameters, "investment_enabled", strict, issues, "roguelike.investment_enabled", true),
            InvestmentWithMoreScore = ReadBool(parameters, "investment_with_more_score", false, issues, "roguelike.investment_with_more_score", false),
            InvestmentsCount = ReadInt(parameters, "investments_count", false, issues, "roguelike.investments_count", 999),
            StopWhenInvestmentFull = ReadBool(parameters, "stop_when_investment_full", false, issues, "roguelike.stop_when_investment_full", false),
            Squad = ReadString(parameters, "squad", false, issues, "roguelike.squad", string.Empty),
            Roles = ReadString(parameters, "roles", false, issues, "roguelike.roles", string.Empty),
            CoreChar = ReadString(parameters, "core_char", false, issues, "roguelike.core_char", string.Empty),
            UseSupport = ReadBool(parameters, "use_support", strict, issues, "roguelike.use_support", false),
            UseNonfriendSupport = ReadBool(parameters, "use_nonfriend_support", strict, issues, "roguelike.use_nonfriend_support", false),
            RefreshTraderWithDice = ReadBool(parameters, "refresh_trader_with_dice", strict, issues, "roguelike.refresh_trader_with_dice", false),
            StopAtFinalBoss = ReadBool(parameters, "stop_at_final_boss", false, issues, "roguelike.stop_at_final_boss", false),
            StopAtMaxLevel = ReadBool(parameters, "stop_at_max_level", false, issues, "roguelike.stop_at_max_level", false),
            CollectibleModeShopping = ReadBool(parameters, "collectible_mode_shopping", false, issues, "roguelike.collectible_mode_shopping", false),
            CollectibleModeSquad = ReadString(parameters, "collectible_mode_squad", false, issues, "roguelike.collectible_mode_squad", string.Empty),
            StartWithEliteTwo = ReadBool(parameters, "start_with_elite_two", false, issues, "roguelike.start_with_elite_two", false),
            OnlyStartWithEliteTwo = ReadBool(parameters, "only_start_with_elite_two", false, issues, "roguelike.only_start_with_elite_two", false),
            CollectibleModeStartList = ReadCollectibleStartList(parameters["collectible_mode_start_list"], issues, "roguelike.collectible_mode_start_list"),
            MonthlySquadAutoIterate = ReadBool(parameters, "monthly_squad_auto_iterate", false, issues, "roguelike.monthly_squad_auto_iterate", true),
            MonthlySquadCheckComms = ReadBool(parameters, "monthly_squad_check_comms", false, issues, "roguelike.monthly_squad_check_comms", true),
            DeepExplorationAutoIterate = ReadBool(parameters, "deep_exploration_auto_iterate", false, issues, "roguelike.deep_exploration_auto_iterate", true),
            FindPlayTimeTarget = ReadIntWithAliases(
                parameters,
                ["find_playTime_target", "find_playtime_target"],
                false,
                issues,
                "roguelike.find_playTime_target",
                1),
            FirstFloorFoldartal = ReadString(parameters, "first_floor_foldartal", false, issues, "roguelike.first_floor_foldartal", string.Empty),
            StartFoldartalList = ReadStringArrayCompat(parameters, "start_foldartal_list", false, issues, "roguelike.start_foldartal_list"),
            ExpectedCollapsalParadigms = ReadStringArrayCompat(parameters, "expected_collapsal_paradigms", false, issues, "roguelike.expected_collapsal_paradigms"),
            StartWithSeed = ReadString(parameters, "start_with_seed", false, issues, "roguelike.start_with_seed", string.Empty),
        };

        return (dto, issues);
    }

    public static TaskCompileOutput CompileRoguelike(
        RoguelikeTaskParamsDto dto,
        UnifiedProfile profile,
        UnifiedConfig config)
    {
        _ = profile;
        _ = config;

        var issues = new List<TaskValidationIssue>();

        var mode = dto.Mode;
        if (!RoguelikeModes.Contains(mode))
        {
            issues.Add(new TaskValidationIssue("RoguelikeModeInvalid", "roguelike.mode", "Roguelike mode is not supported by current schema."));
            mode = 0;
        }

        var theme = string.IsNullOrWhiteSpace(dto.Theme) ? "JieGarden" : dto.Theme.Trim();
        if (!RoguelikeThemes.Contains(theme))
        {
            issues.Add(new TaskValidationIssue("RoguelikeThemeUnknown", "roguelike.theme", "Unknown roguelike theme, fallback to JieGarden.", Blocking: false));
            theme = "JieGarden";
        }

        var difficulty = dto.Difficulty;
        if (difficulty < -1)
        {
            issues.Add(new TaskValidationIssue("RoguelikeDifficultyOutOfRange", "roguelike.difficulty", "Difficulty must be greater than or equal to -1.", Blocking: false));
            difficulty = -1;
        }

        if (dto.StartsCount < 0)
        {
            issues.Add(new TaskValidationIssue("RoguelikeStartsCountOutOfRange", "roguelike.starts_count", "Starts count must be greater than or equal to zero.", Blocking: false));
        }

        if (dto.InvestmentsCount < 0)
        {
            issues.Add(new TaskValidationIssue("RoguelikeInvestmentsCountOutOfRange", "roguelike.investments_count", "Investments count must be greater than or equal to zero.", Blocking: false));
        }

        if (!string.Equals(theme, "Mizuki", StringComparison.OrdinalIgnoreCase) && dto.RefreshTraderWithDice)
        {
            issues.Add(new TaskValidationIssue("RoguelikeRefreshTraderThemeMismatch", "roguelike.refresh_trader_with_dice", "Refresh trader with dice is only supported in Mizuki theme.", Blocking: false));
        }

        if (mode == 20001 && !string.Equals(theme, "JieGarden", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new TaskValidationIssue("RoguelikeFindPlaytimeThemeMismatch", "roguelike.theme", "FindPlaytime mode requires JieGarden theme, fallback applied.", Blocking: false));
            theme = "JieGarden";
        }

        var findPlayTimeTarget = dto.FindPlayTimeTarget;
        if (mode == 20001 && (findPlayTimeTarget is < 1 or > 3))
        {
            issues.Add(new TaskValidationIssue("RoguelikeFindPlaytimeTargetOutOfRange", "roguelike.find_playTime_target", "FindPlaytime target must be between 1 and 3.", Blocking: false));
            findPlayTimeTarget = 1;
        }

        var startWithEliteTwo = dto.StartWithEliteTwo;
        var onlyStartWithEliteTwo = dto.OnlyStartWithEliteTwo;
        var squad = (dto.Squad ?? string.Empty).Trim();
        var modeAllowsEliteTwo = mode == 4
            && (string.Equals(theme, "Mizuki", StringComparison.OrdinalIgnoreCase) || string.Equals(theme, "Sami", StringComparison.OrdinalIgnoreCase))
            && IsRoguelikeProfessionalSquad(squad);
        if (startWithEliteTwo && !modeAllowsEliteTwo)
        {
            issues.Add(new TaskValidationIssue("RoguelikeEliteTwoModeMismatch", "roguelike.start_with_elite_two", "StartWithEliteTwo is only supported in collectible mode with professional squad.", Blocking: false));
            startWithEliteTwo = false;
        }

        if (onlyStartWithEliteTwo && !startWithEliteTwo)
        {
            issues.Add(new TaskValidationIssue("RoguelikeOnlyEliteTwoRequiresEliteTwo", "roguelike.only_start_with_elite_two", "OnlyStartWithEliteTwo requires StartWithEliteTwo to be enabled.", Blocking: false));
            onlyStartWithEliteTwo = false;
        }

        if (dto.UseSupport && startWithEliteTwo)
        {
            issues.Add(new TaskValidationIssue("RoguelikeEliteTwoSupportConflict", "roguelike.use_support", "UseSupport conflicts with StartWithEliteTwo under current strategy.", Blocking: false));
            startWithEliteTwo = false;
            onlyStartWithEliteTwo = false;
        }

        var startFoldartalList = ParseDelimitedList(dto.StartFoldartalList);
        if (startFoldartalList.Count > 3)
        {
            issues.Add(new TaskValidationIssue("RoguelikeStartFoldartalListTrimmed", "roguelike.start_foldartal_list", "Start foldartal list exceeds max size and will be trimmed to 3.", Blocking: false));
            startFoldartalList = startFoldartalList.Take(3).ToList();
        }

        var expectedCollapsalParadigms = ParseDelimitedList(dto.ExpectedCollapsalParadigms);

        var startWithSeed = (dto.StartWithSeed ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(startWithSeed) && !RoguelikeSeedRegex.IsMatch(startWithSeed))
        {
            issues.Add(new TaskValidationIssue("RoguelikeStartWithSeedInvalid", "roguelike.start_with_seed", "Seed format is invalid. Expected `<alnum>,rogue_<id>,<step>`."));
        }

        var parameters = new JsonObject
        {
            ["mode"] = mode,
            ["theme"] = theme,
            ["difficulty"] = difficulty,
            ["starts_count"] = Math.Max(0, dto.StartsCount),
            ["investment_enabled"] = dto.InvestmentEnabled,
            ["use_support"] = dto.UseSupport,
            ["use_nonfriend_support"] = dto.UseNonfriendSupport,
            ["refresh_trader_with_dice"] = string.Equals(theme, "Mizuki", StringComparison.OrdinalIgnoreCase) && dto.RefreshTraderWithDice,
        };

        if (dto.InvestmentEnabled)
        {
            parameters["investment_with_more_score"] = dto.InvestmentWithMoreScore && mode == 1;
            parameters["investments_count"] = Math.Max(0, dto.InvestmentsCount);
            parameters["stop_when_investment_full"] = dto.StopWhenInvestmentFull;
        }

        if (!string.IsNullOrWhiteSpace(squad))
        {
            parameters["squad"] = squad;
        }

        if (!string.IsNullOrWhiteSpace(dto.Roles))
        {
            parameters["roles"] = dto.Roles.Trim();
        }

        if (!string.IsNullOrWhiteSpace(dto.CoreChar))
        {
            parameters["core_char"] = dto.CoreChar.Trim();
        }

        if (mode == 0)
        {
            parameters["stop_at_final_boss"] = dto.StopAtFinalBoss;
            parameters["stop_at_max_level"] = dto.StopAtMaxLevel;
        }

        if (mode == 4)
        {
            parameters["collectible_mode_shopping"] = dto.CollectibleModeShopping;
            parameters["collectible_mode_squad"] = dto.CollectibleModeSquad.Trim();
            parameters["start_with_elite_two"] = startWithEliteTwo;
            parameters["only_start_with_elite_two"] = onlyStartWithEliteTwo;
            parameters["collectible_mode_start_list"] = CompileCollectibleStartList(dto.CollectibleModeStartList);
        }

        if (mode == 6)
        {
            parameters["monthly_squad_auto_iterate"] = dto.MonthlySquadAutoIterate;
            parameters["monthly_squad_check_comms"] = dto.MonthlySquadCheckComms;
        }

        if (mode == 7)
        {
            parameters["deep_exploration_auto_iterate"] = dto.DeepExplorationAutoIterate;
        }

        if (mode == 20001)
        {
            parameters["find_playTime_target"] = findPlayTimeTarget;
        }

        if (!string.IsNullOrWhiteSpace(dto.FirstFloorFoldartal))
        {
            parameters["first_floor_foldartal"] = dto.FirstFloorFoldartal.Trim();
        }

        if (startFoldartalList.Count > 0)
        {
            parameters["start_foldartal_list"] = ToJsonArray(startFoldartalList);
        }

        if (mode == 5)
        {
            parameters["expected_collapsal_paradigms"] = ToJsonArray(expectedCollapsalParadigms);
        }

        if (!string.IsNullOrWhiteSpace(startWithSeed))
        {
            parameters["start_with_seed"] = startWithSeed;
        }

        return new TaskCompileOutput
        {
            NormalizedType = "Roguelike",
            Params = parameters,
            Issues = issues,
        };
    }

    public static (ReclamationTaskParamsDto Dto, IReadOnlyList<TaskValidationIssue> Issues) ReadReclamation(
        UnifiedTaskItem task,
        bool strict)
    {
        var issues = new List<TaskValidationIssue>();
        var parameters = task.Params ?? new JsonObject();

        var dto = new ReclamationTaskParamsDto
        {
            Theme = ReadString(parameters, "theme", strict, issues, "reclamation.theme", "Tales"),
            Mode = ReadInt(parameters, "mode", strict, issues, "reclamation.mode", 1),
            IncrementMode = ReadInt(parameters, "increment_mode", strict, issues, "reclamation.increment_mode", 0),
            NumCraftBatches = ReadInt(parameters, "num_craft_batches", strict, issues, "reclamation.num_craft_batches", 16),
            ToolsToCraft = ReadStringArrayCompat(parameters, "tools_to_craft", false, issues, "reclamation.tools_to_craft"),
            ClearStore = ReadBool(parameters, "clear_store", strict, issues, "reclamation.clear_store", true),
        };

        return (dto, issues);
    }

    public static TaskCompileOutput CompileReclamation(
        ReclamationTaskParamsDto dto,
        UnifiedProfile profile,
        UnifiedConfig config)
    {
        _ = profile;
        _ = config;

        var issues = new List<TaskValidationIssue>();

        var theme = string.IsNullOrWhiteSpace(dto.Theme) ? "Tales" : dto.Theme.Trim();
        if (!ReclamationThemes.Contains(theme))
        {
            issues.Add(new TaskValidationIssue("ReclamationThemeUnknown", "reclamation.theme", "Unknown reclamation theme, fallback to Tales.", Blocking: false));
            theme = "Tales";
        }

        var mode = dto.Mode;
        var allowedModes = string.Equals(theme, "RelaunchAnchor", StringComparison.OrdinalIgnoreCase)
            ? ReclamationRelaunchAnchorModes
            : ReclamationProsperityModes;
        var fallbackMode = string.Equals(theme, "RelaunchAnchor", StringComparison.OrdinalIgnoreCase) ? 16 : 1;
        if (!ReclamationModes.Contains(mode) || !allowedModes.Contains(mode))
        {
            issues.Add(new TaskValidationIssue("ReclamationModeInvalid", "reclamation.mode", "Reclamation mode is not supported by current schema.", Blocking: false));
            mode = fallbackMode;
        }

        var incrementMode = dto.IncrementMode;
        if (incrementMode is < 0 or > 1)
        {
            issues.Add(new TaskValidationIssue("ReclamationIncrementModeOutOfRange", "reclamation.increment_mode", "Increment mode must be 0 or 1.", Blocking: false));
            incrementMode = 0;
        }

        var numCraftBatches = dto.NumCraftBatches;
        if (numCraftBatches is < 0 or > 99999)
        {
            issues.Add(new TaskValidationIssue("ReclamationNumCraftBatchesOutOfRange", "reclamation.num_craft_batches", "NumCraftBatches must be between 0 and 99999.", Blocking: false));
            numCraftBatches = Math.Clamp(numCraftBatches, 0, 99999);
        }

        var toolsToCraft = ParseDelimitedList(dto.ToolsToCraft);
        if (toolsToCraft.Any(ContainsStructuredToken))
        {
            issues.Add(new TaskValidationIssue("ReclamationToolNameInvalid", "reclamation.tools_to_craft", "ToolsToCraft contains unparseable structured tokens."));
        }

        if (mode != 1 && toolsToCraft.Count > 0)
        {
            issues.Add(new TaskValidationIssue("ReclamationToolsIgnoredInNoArchive", "reclamation.tools_to_craft", "ToolsToCraft is ignored in no-archive mode and will be cleared.", Blocking: false));
            toolsToCraft = [];
        }

        var clearStore = dto.ClearStore;
        if (mode != 0 && clearStore)
        {
            issues.Add(new TaskValidationIssue(
                "ReclamationClearStoreIgnoredInArchive",
                "reclamation.clear_store",
                "ClearStore is ignored in archive mode and will be disabled.",
                Blocking: false));
            clearStore = false;
        }

        var parameters = new JsonObject
        {
            ["theme"] = theme,
            ["mode"] = mode,
            ["increment_mode"] = incrementMode,
            ["num_craft_batches"] = numCraftBatches,
            ["tools_to_craft"] = ToJsonArray(toolsToCraft),
            ["clear_store"] = clearStore,
        };

        return new TaskCompileOutput
        {
            NormalizedType = "Reclamation",
            Params = parameters,
            Issues = issues,
        };
    }

    public static (CustomTaskParamsDto Dto, IReadOnlyList<TaskValidationIssue> Issues) ReadCustom(
        UnifiedTaskItem task,
        bool strict)
    {
        var issues = new List<TaskValidationIssue>();
        var parameters = task.Params ?? new JsonObject();
        var taskNames = ReadStringArrayCompat(parameters, "task_names", strict, issues, "custom.task_names");

        var dto = new CustomTaskParamsDto
        {
            TaskNames = taskNames,
        };

        return (dto, issues);
    }

    public static TaskCompileOutput CompileCustom(
        CustomTaskParamsDto dto,
        UnifiedProfile profile,
        UnifiedConfig config)
    {
        _ = profile;
        _ = config;

        var issues = new List<TaskValidationIssue>();
        var taskNames = ParseDelimitedList(dto.TaskNames);
        if (taskNames.Count == 0)
        {
            issues.Add(new TaskValidationIssue("CustomTaskNamesEmpty", "custom.task_names", "Custom task names list is empty.", Blocking: false));
        }

        var normalizedTaskNames = new List<string>(taskNames.Count);
        var normalizedChanged = false;
        foreach (var taskName in taskNames)
        {
            if (ContainsStructuredToken(taskName))
            {
                issues.Add(new TaskValidationIssue("CustomTaskNameInvalid", "custom.task_names", $"Custom task name `{taskName}` contains unparseable structured tokens."));
                continue;
            }

            var normalizedName = NormalizeTaskType(taskName);
            if (!CustomKnownTaskTypes.Contains(normalizedName))
            {
                issues.Add(new TaskValidationIssue("CustomTaskNameUnknown", "custom.task_names", $"Custom task name `{taskName}` is not recognized.", Blocking: false));
            }

            normalizedTaskNames.Add(normalizedName);
            normalizedChanged |= !string.Equals(taskName, normalizedName, StringComparison.Ordinal);
        }

        if (normalizedChanged || normalizedTaskNames.Count != taskNames.Count || taskNames.Count != dto.TaskNames.Count)
        {
            issues.Add(new TaskValidationIssue("CustomTaskNamesNormalized", "custom.task_names", "Custom task names were normalized and deduplicated.", Blocking: false));
        }

        var parameters = new JsonObject
        {
            ["task_names"] = ToJsonArray(normalizedTaskNames),
        };

        return new TaskCompileOutput
        {
            NormalizedType = "Custom",
            Params = parameters,
            Issues = issues,
        };
    }

    public static (UserDataUpdateTaskParamsDto Dto, IReadOnlyList<TaskValidationIssue> Issues) ReadUserDataUpdate(
        UnifiedTaskItem task,
        bool strict)
    {
        var issues = new List<TaskValidationIssue>();
        var parameters = task.Params ?? TaskModuleParameterDefaults.CreateUserDataUpdateDefaults();

        var dto = new UserDataUpdateTaskParamsDto
        {
            UpdateOperBox = ReadBool(parameters, UserDataUpdateOperBox, strict, issues, "user_data_update.update_oper_box", true),
            UpdateDepot = ReadBool(parameters, UserDataUpdateDepot, strict, issues, "user_data_update.update_depot", true),
            TriggerInterval = NormalizeUserDataUpdateTriggerInterval(
                ReadString(
                    parameters,
                    UserDataUpdateTriggerInterval,
                    strict,
                    issues,
                    "user_data_update.trigger_interval",
                    UserDataUpdateTaskParamsDto.TriggerEveryTime)),
        };

        return (dto, issues);
    }

    public static TaskCompileOutput CompileUserDataUpdate(UserDataUpdateTaskParamsDto dto)
    {
        var issues = new List<TaskValidationIssue>();
        if (!dto.UpdateOperBox && !dto.UpdateDepot)
        {
            issues.Add(new TaskValidationIssue(
                "UserDataUpdateNoTarget",
                "user_data_update.target",
                "User data update has no enabled sync target.",
                Blocking: false));
        }

        var parameters = new JsonObject
        {
            [UserDataUpdateOperBox] = dto.UpdateOperBox,
            [UserDataUpdateDepot] = dto.UpdateDepot,
            [UserDataUpdateTriggerInterval] = NormalizeUserDataUpdateTriggerInterval(dto.TriggerInterval),
        };

        return new TaskCompileOutput
        {
            NormalizedType = TaskModuleTypes.UserDataUpdate,
            Params = parameters,
            Issues = issues,
        };
    }

    public static (SingleStepTaskParamsDto Dto, IReadOnlyList<TaskValidationIssue> Issues) ReadSingleStep(
        UnifiedTaskItem task,
        bool strict)
    {
        var issues = new List<TaskValidationIssue>();
        var parameters = task.Params ?? new JsonObject();

        var dto = new SingleStepTaskParamsDto
        {
            Type = ReadString(parameters, "type", strict, issues, "single_step.type", string.Empty),
            Subtype = ReadString(parameters, "subtype", strict, issues, "single_step.subtype", string.Empty),
            Details = parameters["details"]?.DeepClone(),
        };

        return (dto, issues);
    }

    public static TaskCompileOutput CompileSingleStep(SingleStepTaskParamsDto dto)
    {
        var issues = new List<TaskValidationIssue>();
        var type = (dto.Type ?? string.Empty).Trim();
        var subtype = (dto.Subtype ?? string.Empty).Trim();

        if (!string.Equals(type, "copilot", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new TaskValidationIssue(
                "SingleStepTypeMissing",
                "single_step.type",
                "SingleStep requires type `copilot` and a supported subtype."));
        }

        if (string.IsNullOrWhiteSpace(subtype)
            || (!string.Equals(subtype, "stage", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(subtype, "start", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(subtype, "action", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new TaskValidationIssue(
                "SingleStepSubtypeMissing",
                "single_step.subtype",
                "SingleStep requires subtype `stage`, `start`, or `action`."));
        }

        if ((string.Equals(subtype, "stage", StringComparison.OrdinalIgnoreCase)
                || string.Equals(subtype, "action", StringComparison.OrdinalIgnoreCase))
            && dto.Details is null)
        {
            issues.Add(new TaskValidationIssue(
                "SingleStepDetailsMissing",
                "single_step.details",
                "SingleStep stage/action tasks require a details payload."));
        }

        var parameters = new JsonObject();
        if (!string.IsNullOrWhiteSpace(type))
        {
            parameters["type"] = type;
        }

        if (!string.IsNullOrWhiteSpace(subtype))
        {
            parameters["subtype"] = subtype;
        }

        if (dto.Details is not null)
        {
            parameters["details"] = dto.Details.DeepClone();
        }

        return new TaskCompileOutput
        {
            NormalizedType = TaskModuleTypes.SingleStep,
            Params = parameters,
            Issues = issues,
        };
    }

    public static TaskCompileOutput CompileTask(
        UnifiedTaskItem task,
        UnifiedProfile profile,
        UnifiedConfig config,
        bool strict)
    {
        var normalized = NormalizeTaskType(task.Type);

        return normalized switch
        {
            "StartUp" => CompileStartUpFromTask(task, profile, config, strict),
            "Fight" => CompileFightFromTask(task, profile, config, strict),
            "Mall" => CompileMallFromTask(task, profile, config, strict),
            "Recruit" => CompileRecruitFromTask(task, profile, config, strict),
            "Roguelike" => CompileRoguelikeFromTask(task, profile, config, strict),
            "Reclamation" => CompileReclamationFromTask(task, profile, config, strict),
            "UserDataUpdate" => CompileUserDataUpdateFromTask(task, strict),
            "SingleStep" => CompileSingleStepFromTask(task, strict),
            "Custom" => CompileCustomFromTask(task, profile, config, strict),
            _ => new TaskCompileOutput
            {
                NormalizedType = normalized,
                Params = task.Params ?? new JsonObject(),
                Issues = [],
            },
        };
    }

    public static TaskCompileOutput NormalizeTaskForPersistence(
        UnifiedTaskItem task,
        UnifiedProfile profile,
        UnifiedConfig config,
        bool strict)
    {
        var normalized = NormalizeTaskType(task.Type);
        if (string.Equals(normalized, TaskModuleTypes.Mall, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeMallForPersistence(task);
        }

        return CompileTask(task, profile, config, strict);
    }

    public static JsonObject BuildCoreParams(string taskType, JsonObject parameters)
    {
        var runtimeParams = parameters.DeepClone() as JsonObject ?? new JsonObject();
        if (!string.Equals(NormalizeTaskType(taskType), TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase))
        {
            return runtimeParams;
        }

        if (runtimeParams["stage"] is JsonValue stageValue
            && stageValue.TryGetValue(out string? stage))
        {
            runtimeParams["stage"] = FightStageSelection.ToCoreStage(stage);
        }

        return runtimeParams;
    }

    private static TaskCompileOutput CompileStartUpFromTask(
        UnifiedTaskItem task,
        UnifiedProfile profile,
        UnifiedConfig config,
        bool strict)
    {
        var (dto, readIssues) = ReadStartUp(task, profile, config, strict);
        var compiled = CompileStartUp(dto, profile, config);
        var allIssues = readIssues.Concat(compiled.Issues).ToList();
        return new TaskCompileOutput
        {
            NormalizedType = compiled.NormalizedType,
            Params = compiled.Params,
            Issues = allIssues,
        };
    }

    private static TaskCompileOutput CompileFightFromTask(
        UnifiedTaskItem task,
        UnifiedProfile profile,
        UnifiedConfig config,
        bool strict)
    {
        var (dto, readIssues) = ReadFight(task, strict);
        var compiled = CompileFight(dto, profile, config);
        var allIssues = readIssues.Concat(compiled.Issues).ToList();
        return new TaskCompileOutput
        {
            NormalizedType = compiled.NormalizedType,
            Params = compiled.Params,
            Issues = allIssues,
        };
    }

    private static TaskCompileOutput CompileUserDataUpdateFromTask(
        UnifiedTaskItem task,
        bool strict)
    {
        var (dto, readIssues) = ReadUserDataUpdate(task, strict);
        var compiled = CompileUserDataUpdate(dto);
        return new TaskCompileOutput
        {
            NormalizedType = compiled.NormalizedType,
            Params = compiled.Params,
            Issues = readIssues.Concat(compiled.Issues).ToList(),
        };
    }

    private static TaskCompileOutput CompileSingleStepFromTask(
        UnifiedTaskItem task,
        bool strict)
    {
        var (dto, readIssues) = ReadSingleStep(task, strict);
        var compiled = CompileSingleStep(dto);
        return new TaskCompileOutput
        {
            NormalizedType = compiled.NormalizedType,
            Params = compiled.Params,
            Issues = readIssues.Concat(compiled.Issues).ToList(),
        };
    }

    private static TaskCompileOutput CompileMallFromTask(
        UnifiedTaskItem task,
        UnifiedProfile profile,
        UnifiedConfig config,
        bool strict)
    {
        _ = strict;
        var issues = new List<TaskValidationIssue>();
        var nowUtc = DateTime.UtcNow;
        var parameters = task.Params ?? TaskModuleParameterDefaults.CreateMallDefaults("zh-cn");
        var model = MallParams.FromJson(parameters);
        var clientType = ResolveStringSetting(profile, config, "ClientType", "Start.ClientType") ?? "Official";
        var defaultLastTime = MallDailyResetHelper.GetPreviousYjDateString(nowUtc, clientType);
        var creditFightLastTime = ReadString(
            parameters,
            UiMallCreditFightLastTime,
            false,
            issues,
            "mall.credit_fight_last_time",
            defaultLastTime);
        var visitFriendsLastTime = ReadString(
            parameters,
            UiMallVisitFriendsLastTime,
            false,
            issues,
            "mall.visit_friends_last_time",
            defaultLastTime);

        var creditFight = model.CreditFight;
        if (model.CreditFightOnceADay)
        {
            creditFight = creditFight && MallDailyResetHelper.IsTaskAvailableToday(creditFightLastTime, clientType, nowUtc);
        }

        var visitFriends = model.VisitFriends;
        if (model.VisitFriendsOnceADay)
        {
            visitFriends = visitFriends && MallDailyResetHelper.IsTaskAvailableToday(visitFriendsLastTime, clientType, nowUtc);
        }

        var buyFirst = model.BuyFirst
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        var blacklist = model.Blacklist
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Union(ResolveMallBlacklistDefaults(clientType), StringComparer.Ordinal)
            .ToList();

        return new TaskCompileOutput
        {
            NormalizedType = "Mall",
            Params = new JsonObject
            {
                ["credit_fight"] = creditFight,
                ["credit_fight_once_a_day"] = model.CreditFightOnceADay,
                ["formation_index"] = Math.Clamp(model.FormationIndex, 0, 4),
                ["visit_friends"] = visitFriends,
                ["visit_friends_once_a_day"] = model.VisitFriendsOnceADay,
                ["shopping"] = model.Shopping,
                ["buy_first"] = ToJsonArray(buyFirst),
                ["blacklist"] = ToJsonArray(blacklist),
                ["force_shopping_if_credit_full"] = model.ForceShoppingIfCreditFull,
                ["only_buy_discount"] = model.OnlyBuyDiscount,
                ["reserve_max_credit"] = model.ReserveMaxCredit,
                [UiMallCreditFightLastTime] = string.IsNullOrWhiteSpace(creditFightLastTime) ? defaultLastTime : creditFightLastTime,
                [UiMallVisitFriendsLastTime] = string.IsNullOrWhiteSpace(visitFriendsLastTime) ? defaultLastTime : visitFriendsLastTime,
            },
            Issues = issues,
        };
    }

    private static TaskCompileOutput NormalizeMallForPersistence(UnifiedTaskItem task)
    {
        var model = MallParams.FromJson(task.Params);
        model.BuyFirst = NormalizeMallStoredList(model.BuyFirst);
        model.Blacklist = NormalizeMallStoredList(model.Blacklist);

        return new TaskCompileOutput
        {
            NormalizedType = "Mall",
            Params = model.ToJson(),
            Issues = [],
        };
    }

    private static List<string> NormalizeMallStoredList(IEnumerable<string?> values)
    {
        return values
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TaskCompileOutput CompileRecruitFromTask(
        UnifiedTaskItem task,
        UnifiedProfile profile,
        UnifiedConfig config,
        bool strict)
    {
        var (dto, readIssues) = ReadRecruit(task, strict);
        var compiled = CompileRecruit(dto, profile, config);
        var allIssues = readIssues.Concat(compiled.Issues).ToList();
        return new TaskCompileOutput
        {
            NormalizedType = compiled.NormalizedType,
            Params = compiled.Params,
            Issues = allIssues,
        };
    }

    private static TaskCompileOutput CompileRoguelikeFromTask(
        UnifiedTaskItem task,
        UnifiedProfile profile,
        UnifiedConfig config,
        bool strict)
    {
        var (dto, readIssues) = ReadRoguelike(task, strict);
        var compiled = CompileRoguelike(dto, profile, config);
        var allIssues = readIssues.Concat(compiled.Issues).ToList();
        return new TaskCompileOutput
        {
            NormalizedType = compiled.NormalizedType,
            Params = compiled.Params,
            Issues = allIssues,
        };
    }

    private static TaskCompileOutput CompileReclamationFromTask(
        UnifiedTaskItem task,
        UnifiedProfile profile,
        UnifiedConfig config,
        bool strict)
    {
        var (dto, readIssues) = ReadReclamation(task, strict);
        var compiled = CompileReclamation(dto, profile, config);
        var allIssues = readIssues.Concat(compiled.Issues).ToList();
        return new TaskCompileOutput
        {
            NormalizedType = compiled.NormalizedType,
            Params = compiled.Params,
            Issues = allIssues,
        };
    }

    private static TaskCompileOutput CompileCustomFromTask(
        UnifiedTaskItem task,
        UnifiedProfile profile,
        UnifiedConfig config,
        bool strict)
    {
        var (dto, readIssues) = ReadCustom(task, strict);
        var compiled = CompileCustom(dto, profile, config);
        var allIssues = readIssues.Concat(compiled.Issues).ToList();
        return new TaskCompileOutput
        {
            NormalizedType = compiled.NormalizedType,
            Params = compiled.Params,
            Issues = allIssues,
        };
    }

    public static void ApplyStartUpSharedProfileValues(UnifiedProfile profile, StartUpTaskParamsDto dto)
    {
        profile.Values["ConnectConfig"] = JsonValue.Create(dto.ConnectConfig);
        profile.Values["ConnectAddress"] = JsonValue.Create(dto.ConnectAddress);
        profile.Values["AdbPath"] = JsonValue.Create(dto.AdbPath);
        if (dto.MacUseBundledAdb.HasValue)
        {
            profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(dto.MacUseBundledAdb.Value);
        }

        profile.Values["TouchMode"] = JsonValue.Create(dto.TouchMode);
        profile.Values["AutoDetect"] = JsonValue.Create(dto.AutoDetectConnection);
        profile.Values["AttachWindowScreencapMethod"] = JsonValue.Create(dto.AttachWindowScreencapMethod);
        profile.Values["AttachWindowMouseMethod"] = JsonValue.Create(dto.AttachWindowMouseMethod);
        profile.Values["AttachWindowKeyboardMethod"] = JsonValue.Create(dto.AttachWindowKeyboardMethod);
        profile.Values["ClientType"] = JsonValue.Create(dto.ClientType);
        profile.Values["StartGame"] = JsonValue.Create(dto.StartGameEnabled);
    }

    private static void ValidateRecruitTime(int value, string field, ICollection<TaskValidationIssue> issues)
    {
        if (value < 60 || value > 540 || value % 10 != 0)
        {
            issues.Add(new TaskValidationIssue(
                "RecruitTimeOutOfRange",
                field,
                "Recruit time must be between 60 and 540 minutes and aligned to 10-minute steps."));
        }
    }

    private static int ClampRecruitTime(int value)
    {
        var clamped = Math.Clamp(value, 60, 540);
        return (clamped / 10) * 10;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.Ordinal))
        {
            array.Add(value);
        }

        return array;
    }

    private static List<string> ParseDelimitedList(IEnumerable<string> values)
    {
        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsRoguelikeProfessionalSquad(string squad)
    {
        return RoguelikeProfessionalSquads.Contains(squad);
    }

    private static bool ContainsStructuredToken(string value)
    {
        return value.IndexOfAny(['[', ']', '{', '}', ':', '"', '\r', '\n']) >= 0
               || value.Any(c => char.IsControl(c) && c != '\t');
    }

    private static string NormalizeUserDataUpdateTriggerInterval(string? value)
    {
        return (value ?? string.Empty).Trim() switch
        {
            var trigger when string.Equals(trigger, UserDataUpdateTaskParamsDto.TriggerDaily, StringComparison.OrdinalIgnoreCase)
                => UserDataUpdateTaskParamsDto.TriggerDaily,
            var trigger when string.Equals(trigger, UserDataUpdateTaskParamsDto.TriggerWeekly, StringComparison.OrdinalIgnoreCase)
                => UserDataUpdateTaskParamsDto.TriggerWeekly,
            _ => UserDataUpdateTaskParamsDto.TriggerEveryTime,
        };
    }

    private static IReadOnlyList<string> ResolveMallBlacklistDefaults(string clientType)
    {
        var normalized = clientType switch
        {
            "YoStarEN" => "EN",
            "YoStarJP" => "JP",
            "YoStarKR" => "KR",
            "txwy" => "Txwy",
            _ => clientType,
        };

        return MallBlacklistByClientType.TryGetValue(normalized, out var values)
            ? values
            : MallBlacklistByClientType["Official"];
    }

    private static JsonObject CompileCollectibleStartList(RoguelikeCollectibleStartListDto? dto)
    {
        dto ??= new RoguelikeCollectibleStartListDto();
        return new JsonObject
        {
            ["hot_water"] = dto.HotWater,
            ["shield"] = dto.Shield,
            ["ingot"] = dto.Ingot,
            ["hope"] = dto.Hope,
            ["random"] = dto.Random,
            ["key"] = dto.Key,
            ["dice"] = dto.Dice,
            ["ideas"] = dto.Ideas,
            ["ticket"] = dto.Ticket,
        };
    }

    private static RoguelikeCollectibleStartListDto ReadCollectibleStartList(
        JsonNode? node,
        ICollection<TaskValidationIssue> issues,
        string field)
    {
        if (node is null)
        {
            return new RoguelikeCollectibleStartListDto();
        }

        if (node is not JsonObject value)
        {
            issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", field, "Task field has incompatible type."));
            return new RoguelikeCollectibleStartListDto();
        }

        return new RoguelikeCollectibleStartListDto
        {
            HotWater = ReadBool(value, "hot_water", false, issues, $"{field}.hot_water", false),
            Shield = ReadBool(value, "shield", false, issues, $"{field}.shield", false),
            Ingot = ReadBool(value, "ingot", false, issues, $"{field}.ingot", false),
            Hope = ReadBool(value, "hope", false, issues, $"{field}.hope", false),
            Random = ReadBool(value, "random", false, issues, $"{field}.random", false),
            Key = ReadBool(value, "key", false, issues, $"{field}.key", false),
            Dice = ReadBool(value, "dice", false, issues, $"{field}.dice", false),
            Ideas = ReadBool(value, "ideas", false, issues, $"{field}.ideas", false),
            Ticket = ReadBool(value, "ticket", false, issues, $"{field}.ticket", false),
        };
    }

    private static List<int> ReadIntArray(JsonObject obj, string key)
    {
        if (obj[key] is not JsonArray array)
        {
            return [];
        }

        var result = new List<int>();
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out int parsed))
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    private static List<string> ReadStringArray(JsonObject obj, string key)
    {
        if (obj[key] is not JsonArray array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? parsed) && !string.IsNullOrWhiteSpace(parsed))
            {
                result.Add(parsed.Trim());
            }
        }

        return result;
    }

    private static List<string> ReadStringArrayCompat(
        JsonObject obj,
        string key,
        bool strict,
        ICollection<TaskValidationIssue> issues,
        string field)
    {
        if (!obj.TryGetPropertyValue(key, out var node))
        {
            if (strict)
            {
                issues.Add(new TaskValidationIssue("TaskFieldMissing", field, $"Required task field `{key}` is missing."));
            }

            return [];
        }

        if (node is JsonArray array)
        {
            var result = new List<string>();
            var hasInvalidEntry = false;
            foreach (var item in array)
            {
                if (item is JsonValue jsonValue
                    && jsonValue.TryGetValue(out string? parsed)
                    && !string.IsNullOrWhiteSpace(parsed))
                {
                    result.Add(parsed.Trim());
                    continue;
                }

                if (item is not null)
                {
                    hasInvalidEntry = true;
                }
            }

            if (hasInvalidEntry)
            {
                issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", field, "Task field has incompatible type."));
            }

            return result
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        if (node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
        {
            return text
                .Split(new[] { '\r', '\n', ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", field, "Task field has incompatible type."));
        return [];
    }

    private static string ReadString(
        JsonObject obj,
        string key,
        bool strict,
        ICollection<TaskValidationIssue> issues,
        string field,
        string fallback)
    {
        if (obj.TryGetPropertyValue(key, out var value))
        {
            if (value is JsonValue jsonValue
                && jsonValue.TryGetValue(out string? text)
                && text is not null)
            {
                return text;
            }

            issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", field, $"Task field `{key}` has incompatible type."));
            return fallback;
        }

        if (strict)
        {
            issues.Add(new TaskValidationIssue("TaskFieldMissing", field, $"Required task field `{key}` is missing."));
        }

        return fallback;
    }

    private static int ReadInt(
        JsonObject obj,
        string key,
        bool strict,
        ICollection<TaskValidationIssue> issues,
        string field,
        int fallback)
    {
        if (obj.TryGetPropertyValue(key, out var value))
        {
            if (value is not JsonValue jsonValue)
            {
                issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", field, $"Task field `{key}` has incompatible type."));
                return fallback;
            }

            if (jsonValue.TryGetValue(out int parsed))
            {
                return parsed;
            }

            if (jsonValue.TryGetValue(out long parsedLong))
            {
                return Convert.ToInt32(parsedLong);
            }

            if (jsonValue.TryGetValue(out string? text) && int.TryParse(text, out var parsedText))
            {
                return parsedText;
            }

            issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", field, $"Task field `{key}` has incompatible type."));
            return fallback;
        }

        if (strict)
        {
            issues.Add(new TaskValidationIssue("TaskFieldMissing", field, $"Required task field `{key}` is missing."));
        }

        return fallback;
    }

    private static int ReadIntWithAliases(
        JsonObject obj,
        IReadOnlyList<string> keys,
        bool strict,
        ICollection<TaskValidationIssue> issues,
        string field,
        int fallback)
    {
        var foundAlias = false;
        foreach (var key in keys)
        {
            if (!obj.TryGetPropertyValue(key, out var value)
                || value is null)
            {
                continue;
            }

            foundAlias = true;
            if (value is not JsonValue jsonValue)
            {
                issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", field, $"Task field `{key}` has incompatible type."));
                return fallback;
            }

            if (jsonValue.TryGetValue(out int parsed))
            {
                return parsed;
            }

            if (jsonValue.TryGetValue(out long parsedLong))
            {
                return Convert.ToInt32(parsedLong);
            }

            if (jsonValue.TryGetValue(out string? text) && int.TryParse(text, out var parsedText))
            {
                return parsedText;
            }

            issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", field, $"Task field `{key}` has incompatible type."));
            return fallback;
        }

        if (!foundAlias && strict)
        {
            issues.Add(new TaskValidationIssue("TaskFieldMissing", field, $"Required task field `{keys[0]}` is missing."));
        }

        return fallback;
    }

    private static bool ReadBool(
        JsonObject obj,
        string key,
        bool strict,
        ICollection<TaskValidationIssue> issues,
        string field,
        bool fallback)
    {
        if (obj.TryGetPropertyValue(key, out var value))
        {
            if (value is not JsonValue jsonValue)
            {
                issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", field, $"Task field `{key}` has incompatible type."));
                return fallback;
            }

            if (jsonValue.TryGetValue(out bool parsed))
            {
                return parsed;
            }

            if (jsonValue.TryGetValue(out int parsedInt))
            {
                return parsedInt != 0;
            }

            if (jsonValue.TryGetValue(out string? text) && bool.TryParse(text, out var parsedText))
            {
                return parsedText;
            }

            issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", field, $"Task field `{key}` has incompatible type."));
            return fallback;
        }

        if (strict)
        {
            issues.Add(new TaskValidationIssue("TaskFieldMissing", field, $"Required task field `{key}` is missing."));
        }

        return fallback;
    }

    private static bool? ReadNullableBool(
        JsonObject obj,
        string key,
        bool? fallback,
        ICollection<TaskValidationIssue> issues,
        string field)
    {
        if (!obj.TryGetPropertyValue(key, out var value))
        {
            return fallback;
        }

        if (value is null)
        {
            return null;
        }

        if (value is not JsonValue jsonValue)
        {
            issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", field, $"Task field `{key}` has incompatible type."));
            return fallback;
        }

        if (jsonValue.TryGetValue(out bool parsed))
        {
            return parsed;
        }

        if (jsonValue.TryGetValue(out int parsedInt))
        {
            return parsedInt != 0;
        }

        if (jsonValue.TryGetValue(out string? text))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (bool.TryParse(text, out var parsedText))
            {
                return parsedText;
            }
        }

        issues.Add(new TaskValidationIssue("TaskFieldTypeInvalid", field, $"Task field `{key}` has incompatible type."));
        return fallback;
    }

    private static string? ResolveStringSetting(UnifiedProfile profile, UnifiedConfig config, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (profile.Values.TryGetValue(key, out var profileValue)
                && profileValue is JsonValue value
                && value.TryGetValue(out string? text)
                && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            if (config.GlobalValues.TryGetValue(key, out var globalValue)
                && globalValue is JsonValue global
                && global.TryGetValue(out string? globalText)
                && !string.IsNullOrWhiteSpace(globalText))
            {
                return globalText;
            }

            if (config.GlobalValues.TryGetValue($"GUI.{key}", out var guiValue)
                && guiValue is JsonValue gui
                && gui.TryGetValue(out string? guiText)
                && !string.IsNullOrWhiteSpace(guiText))
            {
                return guiText;
            }
        }

        return null;
    }

    private static string ResolveYituliuId(UnifiedProfile profile, UnifiedConfig config)
    {
        return ResolveStringSetting(profile, config, "YituliuId")
            ?? ResolveStringSetting(profile, config, "PenguinId")
            ?? string.Empty;
    }

    private static bool ResolveBooleanSetting(UnifiedProfile profile, UnifiedConfig config, bool fallback = false, params string[] keys)
    {
        if (TryResolveBooleanSetting(profile, config, out var value, keys))
        {
            return value;
        }

        return fallback;
    }

    private static bool ResolveBooleanSetting(UnifiedProfile profile, UnifiedConfig config, string key)
        => ResolveBooleanSetting(profile, config, false, key);

    private static bool TryResolveBooleanSetting(UnifiedProfile profile, UnifiedConfig config, out bool value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (profile.Values.TryGetValue(key, out var profileValue) && TryReadBooleanNode(profileValue, out value))
            {
                return true;
            }

            if (config.GlobalValues.TryGetValue(key, out var globalValue) && TryReadBooleanNode(globalValue, out value))
            {
                return true;
            }

            if (config.GlobalValues.TryGetValue($"GUI.{key}", out var guiValue) && TryReadBooleanNode(guiValue, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool ToBoolean(JsonNode? node, bool fallback)
    {
        if (TryReadBooleanNode(node, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static bool TryReadBooleanNode(JsonNode? node, out bool value)
    {
        if (node is not JsonValue jsonValue)
        {
            value = default;
            return false;
        }

        if (jsonValue.TryGetValue(out bool parsedBool))
        {
            value = parsedBool;
            return true;
        }

        if (jsonValue.TryGetValue(out int parsedInt))
        {
            value = parsedInt != 0;
            return true;
        }

        if (jsonValue.TryGetValue(out string? text) && bool.TryParse(text, out var parsedText))
        {
            value = parsedText;
            return true;
        }

        value = default;
        return false;
    }

    private static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        if (!obj.TryGetPropertyValue(key, out var node))
        {
            return fallback;
        }

        return ToBoolean(node, fallback);
    }
}
