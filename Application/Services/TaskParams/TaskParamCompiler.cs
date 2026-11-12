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
    private const string UiUseExpiringMedicine = "_ui_use_expiring_medicine";
    private const string UiMedicineExpireDays = "_ui_medicine_expire_days";
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
    private const string RecruitRobotTag = "支援机械";
    private const int FixedRecruitNineHourMinutes = 540;
    private const int DefaultLevel3RecruitmentPermitReserve = 8;
    private const string UiInfrastFacilityOrder = "_ui_facility_order";
    private const string SkipAppendIssueCode = "TaskCompileSkipAppend";
    private static readonly Regex ExpiredActivityStageCodeRegex = new("^[A-Za-z]{2}-\\d{1,2}$", RegexOptions.Compiled);
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

    // Frozen WPF parity: Mall serialization unconditionally unions this
    // client-type operator blacklist into the user's blacklist
    // (src/MaaWpfGui/ViewModels/UserControl/TaskQueue/MallSettingsUserControlModel.cs:239
    // at WPF commit 23ddd271b40a3d0c18de8a6ae8305357cc032e19). Keys are the
    // canonical client types (WPF ClientType constants).
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> MallClientTypeBlacklist =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Official"] = ["讯使", "嘉维尔", "坚雷"],
            ["Bilibili"] = ["讯使", "嘉维尔", "坚雷"],
            ["YoStarEN"] = ["Courier", "Gavial", "Dur-nar"],
            ["YoStarJP"] = ["クーリエ", "ガヴィル", "ジュナー"],
            ["YoStarKR"] = ["쿠리어", "가비알", "듀나"],
            ["txwy"] = ["訊使", "嘉維爾", "堅雷"],
        };

    // Frozen WPF parity: Recruit `server` is derived from the game client type
    // (SettingsViewModel._serverMapping,
    // src/MaaWpfGui/ViewModels/UI/SettingsViewModel.cs:959-973 at WPF commit
    // 23ddd271b40a3d0c18de8a6ae8305357cc032e19). Keys are the canonical client
    // types (WPF ClientType constants).
    private static readonly IReadOnlyDictionary<string, string> RecruitClientTypeServer =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Official"] = "CN",
            ["Bilibili"] = "CN",
            ["YoStarEN"] = "US",
            ["YoStarJP"] = "JP",
            ["YoStarKR"] = "KR",
            ["txwy"] = "ZH_TW",
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
            ConnectAddress = ResolveStringSetting(profile, config, "ConnectAddress", "Connect.Address") ?? string.Empty,
            AdbPath = ResolveStringSetting(profile, config, "AdbPath", "Connect.AdbPath") ?? string.Empty,
            MacUseBundledAdb = MacBundledAdbPolicy.ReadUseBundledAdb(profile),
            TouchMode = ResolveStringSetting(profile, config, "TouchMode", "Connect.TouchMode") ?? "minitouch",
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
        var configuredMedicineExpireDays = ReadInt(parameters, UiMedicineExpireDays, false, issues, "fight.configured_medicine_expire_days", 0);
        var legacyExpiringMedicine = ReadInt(parameters, "expiring_medicine", false, issues, "fight.expiring_medicine", 0);
        var useExpireMedicineForActivity = ReadBool(parameters, UiUseExpireMedicineForActivity, false);
        var useExpiringMedicine = ReadBool(
            parameters,
            UiUseExpiringMedicine,
            medicineExpireDays > 0 || legacyExpiringMedicine > 0);
        var resolvedMedicineExpireDays = configuredMedicineExpireDays > 0
            ? configuredMedicineExpireDays
            : medicineExpireDays > 0
                ? medicineExpireDays
                : legacyExpiringMedicine is > 0 and < 9999
                    ? legacyExpiringMedicine
                    : 2;
        var times = ReadInt(parameters, "times", strict, issues, "fight.times", int.MaxValue);
        var series = ReadInt(parameters, "series", strict, issues, "fight.series", 0);

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
        var hideUnavailableStage = ReadBool(parameters, UiHideUnavailableStage, true);
        var stageResetModeFallback = hideUnavailableStage ? "Current" : "Ignore";
        var stageResetMode = ReadString(parameters, UiStageResetMode, false, issues, "fight.stage_reset_mode", stageResetModeFallback);
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
            UseExpiringMedicine = useExpiringMedicine,
            ExpiringMedicine = useExpiringMedicine ? 9999 : 0,
            MedicineExpireDays = resolvedMedicineExpireDays,
            UseExpireMedicineForActivity = useExpireMedicineForActivity,
            EnableTargetDrop = ReadNullableBool(parameters, UiEnableTargetDrop, !string.IsNullOrWhiteSpace(dropId), issues, "fight.enable_target_drop"),
            DropId = dropId,
            DropCount = Math.Max(1, dropCount),
            IsInventoryTarget = ReadBool(parameters, UiIsInventoryTarget, false),
            UseCustomAnnihilation = useCustomAnnihilation,
            AnnihilationStage = annihilationStage,
            UseAlternateStage = ReadBool(parameters, UiUseAlternateStage, false),
            HideUnavailableStage = hideUnavailableStage,
            StageResetMode = stageResetMode,
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
        DateTime? nowUtc = null,
        StageActivityState? stageActivityState = null)
    {
        var issues = new List<TaskValidationIssue>();

        var useAlternateStage = dto.UseAlternateStage;
        var hideUnavailableStage = dto.HideUnavailableStage;
        var stageResetModeFallback = hideUnavailableStage ? "Current" : "Ignore";
        var stageResetMode = string.IsNullOrWhiteSpace(dto.StageResetMode) ? stageResetModeFallback : dto.StageResetMode;
        if (!string.Equals(stageResetMode, "Current", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stageResetMode, "Ignore", StringComparison.OrdinalIgnoreCase))
        {
            stageResetMode = stageResetModeFallback;
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

        var stageSelectionTime = nowUtc ?? DateTime.UtcNow;
        var selectedStage = ResolveFightExecutionStage(
            stagePlan,
            dto.IsStageManually,
            stageResetMode,
            ResolveStringSetting(profile, config, "Start.ClientType", "ClientType") ?? "Official",
            stageSelectionTime,
            stageActivityState);
        var stage = selectedStage;

        if (dto.Series is < -1 or > 6)
        {
            issues.Add(new TaskValidationIssue("FightSeriesOutOfRange", "fight.series", "Fight series must be between -1 and 6."));
        }

        if (dto.Times < 0)
        {
            issues.Add(new TaskValidationIssue("FightTimesOutOfRange", "fight.times", "Fight times must be greater than or equal to zero."));
        }

        if (enableTimesLimit != false && dto.Series > 0 && dto.Times > 0 && dto.Times % dto.Series != 0)
        {
            issues.Add(new TaskValidationIssue(
                "FightTimesMayNotExhausted",
                "fight.times",
                "Fight times may not be fully exhausted under current series.",
                Blocking: false));
        }

        // WPF assigns the configured custom annihilation stage unconditionally
        // once the resolved stage is exactly "Annihilation", so a blank override
        // reaches Core as an empty stage.
        if (string.Equals(stage, "Annihilation", StringComparison.Ordinal)
            && dto.UseCustomAnnihilation)
        {
            stage = dto.AnnihilationStage.Trim();
        }

        var configuredMedicineExpireDays = dto.UseExpiringMedicine ? Math.Max(1, dto.MedicineExpireDays) : 0;
        var activityExpireDays = ResolveActivityExpireMedicineDays(dto, profile, config, nowUtc);
        var effectiveMedicineExpireDays = Math.Max(configuredMedicineExpireDays, activityExpireDays);
        var expiringMedicine = effectiveMedicineExpireDays > 0 ? 9999 : 0;

        var parameters = new JsonObject
        {
            ["stage"] = stage,
            ["medicine"] = useMedicine != false ? Math.Max(0, dto.Medicine) : 0,
            ["expiring_medicine"] = expiringMedicine,
            ["medicine_expire_days"] = effectiveMedicineExpireDays,
            ["stone"] = useStone != false ? Math.Max(0, dto.Stone) : 0,
            ["times"] = enableTimesLimit != false ? Math.Max(0, dto.Times) : int.MaxValue,
            ["series"] = dto.Series,
            ["DrGrandet"] = dto.IsDrGrandet,
            ["report_to_penguin"] = ResolveBooleanSetting(profile, config, true, "Penguin.EnablePenguin", "EnablePenguin"),
            ["report_to_yituliu"] = ResolveBooleanSetting(profile, config, true, "Yituliu.EnableYituliu", "EnableYituliu"),
            ["penguin_id"] = ResolveStringSetting(profile, config, "Penguin.Id", "PenguinId") ?? string.Empty,
            ["yituliu_id"] = ResolveYituliuId(profile, config),
            ["server"] = ResolveFightServerType(profile, config),
            ["client_type"] = ResolveStringSetting(profile, config, "Start.ClientType", "ClientType") ?? "Official",
            [UiStagePlan] = ToJsonArray(stagePlan),
            [UiIsStageManually] = dto.IsStageManually,
            [UiUseExpiringMedicine] = dto.UseExpiringMedicine,
            [UiMedicineExpireDays] = Math.Max(1, dto.MedicineExpireDays),
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
        var clientType = ResolveStringSetting(profile, config, "Start.ClientType", "ClientType") ?? "Official";
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
        string stageResetMode,
        string clientType,
        DateTime utcNow,
        StageActivityState? stageActivityState = null)
    {
        var normalizedPlan = FightStageSelection.NormalizeStagePlan(stagePlan);
        var normalizedClientType = NormalizeFightClientType(clientType);
        var currentDay = MallDailyResetHelper.GetYjDate(utcNow, normalizedClientType).DayOfWeek;

        // WPF preserves manual values during resource refresh, but still checks whether they are open before appending.
        if (isStageManually)
        {
            stageResetMode = "Ignore";
        }

        // Tests inject a hermetic state; production reads the machine's cached/web stage state.
        var stageState = stageActivityState
            ?? new StageManagerFeatureService().GetStageActivityState(normalizedClientType);

        foreach (var candidate in normalizedPlan)
        {
            if (FightStageSelection.IsCurrentOrLast(candidate))
            {
                return FightStageSelection.CurrentOrLast;
            }

            if (string.Equals(candidate, "Annihilation", StringComparison.OrdinalIgnoreCase))
            {
                return isStageManually ? candidate : "Annihilation";
            }

            var stage = stageState.Find(candidate);
            if (stage is null)
            {
                if (string.Equals(stageResetMode, "Current", StringComparison.OrdinalIgnoreCase))
                {
                    return FightStageSelection.CurrentOrLast;
                }

                // Keep WPF's fallback: only two-letter activity codes are treated as expired.
                if (!ExpiredActivityStageCodeRegex.IsMatch(candidate))
                {
                    return candidate;
                }

                continue;
            }

            // WPF resets Current only when the stage is no longer in GetStageList().
            // A weekly closed stage and a future activity remain in that list and must be skipped instead.
            var isInStageList = !stage.IsHidden && stage.IsOpenOrWillOpen(utcNow);
            if (!isInStageList
                && string.Equals(stageResetMode, "Current", StringComparison.OrdinalIgnoreCase))
            {
                return FightStageSelection.CurrentOrLast;
            }

            if (stage.IsOpen(utcNow, currentDay))
            {
                return candidate;
            }

        }

        return normalizedPlan[0];
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

    public static (RecruitTaskParamsDto Dto, IReadOnlyList<TaskValidationIssue> Issues) ReadRecruit(
        UnifiedTaskItem task,
        bool strict)
    {
        var issues = new List<TaskValidationIssue>();
        var parameters = task.Params ?? new JsonObject();

        var confirm = ReadIntArray(parameters, "confirm");
        var select = ReadIntArray(parameters, "select");

        var recruitmentTime = parameters["recruitment_time"] as JsonObject ?? new JsonObject();

        // WPF dev-v2 stores PreserveTagEnabled/PreserveTagList; the flattened Core
        // form keeps the list plus an explicit _ui_preserve_tags_enabled marker so
        // an empty enabled list stays distinguishable from a disabled one.
        // v6.3.7 configs only carry skip_robot (and confirm [1]); those are read as
        // the legacy equivalent of preserving 支援机械, but an explicit dev-v2
        // marker always wins over the legacy keys.
        var legacySkipRobot = ReadBool(parameters, "skip_robot", false, issues, "recruit.skip_robot", true);
        var hasPreserveMarker = parameters.TryGetPropertyValue(UiRecruitPreserveTagsEnabled, out var preserveMarkerNode);
        var hasPreserveTagsKey = parameters.ContainsKey("preserve_tags");
        List<string> preserveTags = hasPreserveTagsKey ? ReadStringArray(parameters, "preserve_tags") : [RecruitRobotTag];
        bool preserveEnabled;
        if (hasPreserveMarker)
        {
            preserveEnabled = TryReadBooleanNode(preserveMarkerNode, out var preserveMarker) && preserveMarker;
        }
        else if (hasPreserveTagsKey)
        {
            preserveEnabled = preserveTags.Count > 0;
        }
        else
        {
            preserveEnabled = legacySkipRobot || confirm.Contains(1);
        }

        var permitReserve = ReadInt(parameters, "level3_recruitment_permit_reserve", false, issues, "recruit.level3_recruitment_permit_reserve", 0);

        var dto = new RecruitTaskParamsDto
        {
            Refresh = ReadBool(parameters, "refresh", strict, issues, "recruit.refresh", true),
            ForceRefresh = ReadBool(parameters, "force_refresh", strict, issues, "recruit.force_refresh", true),
            Times = ReadInt(parameters, "times", strict, issues, "recruit.times", 4),
            SetTime = ReadBool(parameters, "set_time", strict, issues, "recruit.set_time", true),
            UseExpedited = ReadBool(parameters, "expedite", false),
            SkipRobot = preserveEnabled,
            PreserveTagEnabled = preserveEnabled,
            PreserveTags = preserveTags,
            Level3RecruitmentPermitReserveEnabled = permitReserve > 0,
            Level3RecruitmentPermitReserve = permitReserve > 0
                ? Math.Clamp(permitReserve, 1, 999)
                : DefaultLevel3RecruitmentPermitReserve,
            ExtraTagsMode = ReadInt(parameters, "extra_tags_mode", false, issues, "recruit.extra_tags_mode", 0),
            FirstTags = ReadStringArray(parameters, "first_tags"),
            ChooseLevel3 = confirm.Contains(3),
            ChooseLevel4 = confirm.Contains(4),
            ChooseLevel5 = confirm.Contains(5),
            ChooseLevel6 = confirm.Contains(6),
            Level3Time = ReadInt(recruitmentTime, "3", strict, issues, "recruit.time.3", FixedRecruitNineHourMinutes),
            Level4Time = ReadInt(recruitmentTime, "4", strict, issues, "recruit.time.4", FixedRecruitNineHourMinutes),
            // 5-star recruit time is fixed at 9:00 in WPF dev-v2 (AsstRecruitTask
            // ChooseLevel5Time has no setter); stored legacy values are ignored.
            Level5Time = FixedRecruitNineHourMinutes,
        };

        if (select.Count == 0 && !confirm.Contains(4) && !confirm.Contains(5))
        {
            dto.ChooseLevel4 = false;
            dto.ChooseLevel5 = false;
        }

        return (dto, issues);
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

        var refresh = dto.Refresh;
        var forceRefresh = dto.ForceRefresh;
        var select = new JsonArray();
        var confirm = new JsonArray();

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

        // 6-star auto-confirm is config-only in WPF dev-v2 (RecruitTask.Level6Choose);
        // the recruit panel keeps the control disabled. It participates in both
        // select and confirm, and its time is fixed at 9:00.
        if (dto.ChooseLevel6)
        {
            select.Add(6);
            confirm.Add(6);
        }

        var preserveTags = NormalizeRecruitTags(dto.PreserveTags);
        var permitReserve = dto.Level3RecruitmentPermitReserveEnabled
            ? Math.Clamp(dto.Level3RecruitmentPermitReserve, 1, 999)
            : 0;

        var parameters = new JsonObject
        {
            ["refresh"] = refresh,
            ["force_refresh"] = forceRefresh,
            ["select"] = select,
            ["confirm"] = confirm,
            ["times"] = Math.Max(0, dto.Times),
            ["set_time"] = dto.SetTime,
            ["expedite"] = dto.UseExpedited != false,
            // WPF dev-v2 no longer emits skip_robot: preserved tags replace it. The
            // enable flag is kept alongside the list so an empty enabled list stays
            // distinguishable from a disabled one; BuildCoreParams strips the marker
            // and blanks preserve_tags when disabled.
            [UiRecruitPreserveTagsEnabled] = dto.PreserveTagEnabled,
            ["preserve_tags"] = ToJsonArray(preserveTags),
            ["level3_recruitment_permit_reserve"] = permitReserve,
            ["extra_tags_mode"] = dto.ExtraTagsMode,
            ["first_tags"] = ToJsonArray(dto.FirstTags),
            ["recruitment_time"] = new JsonObject
            {
                ["3"] = ClampRecruitTime(dto.Level3Time),
                ["4"] = ClampRecruitTime(dto.Level4Time),
                ["5"] = FixedRecruitNineHourMinutes,
                ["6"] = FixedRecruitNineHourMinutes,
            },
            // Frozen WPF defaults both reporting switches to true when the key is absent
            // (GameSettingsUserControlModel reads GetValue(..., bool.TrueString)); an explicit
            // stored false still wins over this fallback. Both key forms are live producers:
            // the settings page writes the dotted legacy keys (ConfigurationKeys.EnablePenguin
            // etc.) while the gui.json importers normalize them to the bare aliases
            // (LegacyConfigValueMappings.ProfileKeyAliases). The dotted form is checked first
            // because the settings page is the live writer: a stale import alias must not
            // shadow a later explicit settings-page toggle.
            ["report_to_penguin"] = ResolveBooleanSetting(profile, config, true, "Penguin.EnablePenguin", "EnablePenguin"),
            ["report_to_yituliu"] = ResolveBooleanSetting(profile, config, true, "Yituliu.EnableYituliu", "EnableYituliu"),
            ["penguin_id"] = ResolveStringSetting(profile, config, "Penguin.Id", "PenguinId") ?? string.Empty,
            ["yituliu_id"] = ResolveYituliuId(profile, config),
            ["server"] = ResolveRecruitServerType(profile, config),
        };

        if (dto.UseExpedited != false)
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

    public static (RoguelikeTaskParamsDto Dto, IReadOnlyList<TaskValidationIssue> Issues) ReadRoguelike(
        UnifiedTaskItem task,
        bool strict)
    {
        var issues = new List<TaskValidationIssue>();
        var parameters = task.Params ?? new JsonObject();

        var mode = ReadInt(parameters, "mode", strict, issues, "roguelike.mode", 0);

        // WPF dev-v2 replaces the single core_char/top-level use_support pair with
        // core_char_list[{name,use_support}] (RoguelikeTask.StartingOpers). Reads
        // still accept the old single pair as the first position so pre-dev-v2
        // avalonia.json keeps working.
        var hasCoreCharList = parameters.ContainsKey("core_char_list");
        List<RoguelikeStartingOperDto> coreCharList = hasCoreCharList
            ? ReadRoguelikeStartingOpers(parameters, issues)
            : [];
        if (!hasCoreCharList)
        {
            var legacyCoreChar = ReadString(parameters, "core_char", false, issues, "roguelike.core_char", string.Empty);
            var legacyUseSupport = ReadBool(parameters, "use_support", false, issues, "roguelike.use_support", false);
            if (!string.IsNullOrWhiteSpace(legacyCoreChar) || legacyUseSupport)
            {
                coreCharList =
                [
                    new RoguelikeStartingOperDto
                    {
                        Name = legacyCoreChar.Trim(),
                        UseSupport = legacyUseSupport,
                    },
                ];
            }
        }

        var firstFloorFoldartal = ReadString(parameters, "first_floor_foldartal", false, issues, "roguelike.first_floor_foldartal", string.Empty);
        var startFoldartalList = ReadWpfSemicolonListCompat(parameters, "start_foldartal_list", issues, "roguelike.start_foldartal_list", trimEntries: false);
        var startWithSeed = ReadString(parameters, "start_with_seed", false, issues, "roguelike.start_with_seed", string.Empty);

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
            CoreChar = coreCharList.Count > 0 ? coreCharList[0].Name : string.Empty,
            UseSupport = coreCharList.Count > 0 && coreCharList[0].UseSupport,
            CoreCharList = coreCharList,
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
            FirstFloorFoldartalEnabled = ReadBool(parameters, "first_floor_foldartal_enabled", false, issues, "roguelike.first_floor_foldartal_enabled", !string.IsNullOrWhiteSpace(firstFloorFoldartal)),
            FirstFloorFoldartal = firstFloorFoldartal,
            StartFoldartalListEnabled = ReadBool(parameters, "start_foldartal_list_enabled", false, issues, "roguelike.start_foldartal_list_enabled", startFoldartalList.Count > 0),
            StartFoldartalList = startFoldartalList,
            ExpectedCollapsalParadigms = ReadWpfSemicolonListCompat(parameters, "expected_collapsal_paradigms", issues, "roguelike.expected_collapsal_paradigms", trimEntries: false),
            StartWithSeedEnabled = ReadBool(parameters, "start_with_seed_enabled", false, issues, "roguelike.start_with_seed_enabled", !string.IsNullOrWhiteSpace(startWithSeed)),
            StartWithSeed = startWithSeed,
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

        if (mode == 5 && !string.Equals(theme, "Sami", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new TaskValidationIssue("RoguelikeCollapseModeThemeMismatch", "roguelike.mode", "Collapse paradigm mode requires the Sami theme, fallback to Exp mode.", Blocking: false));
            mode = 0;
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
            // Frozen WPF keeps the stored theme and clamps the mode back to the first
            // list entry (Exp) when FindPlaytime is unavailable for that theme.
            issues.Add(new TaskValidationIssue("RoguelikeFindPlaytimeThemeMismatch", "roguelike.mode", "FindPlaytime mode requires the JieGarden theme, fallback to Exp mode.", Blocking: false));
            mode = 0;
        }

        var findPlayTimeTarget = dto.FindPlayTimeTarget;
        if (mode == 20001 && (findPlayTimeTarget is < 1 or > 3))
        {
            issues.Add(new TaskValidationIssue("RoguelikeFindPlaytimeTargetOutOfRange", "roguelike.find_playTime_target", "FindPlaytime target must be between 1 and 3.", Blocking: false));
            findPlayTimeTarget = 1;
        }

        var startingOpers = ResolveRoguelikeStartingOpers(dto);
        var startWithEliteTwo = dto.StartWithEliteTwo;
        var onlyStartWithEliteTwo = dto.OnlyStartWithEliteTwo;
        var squad = (dto.Squad ?? string.Empty).Trim();
        // Frozen WPF skips collectible reward serialization whenever the stored
        // only-elite-two intent holds for a non-Phantom professional squad, even on
        // themes where the elite-two flags themselves are gated off at serialization
        // (Sarkaz/JieGarden), so the skip is keyed on the raw stored flags.
        var skipCollectibleStartRewards = dto.StartWithEliteTwo
            && dto.OnlyStartWithEliteTwo
            && mode == 4
            && !string.Equals(theme, "Phantom", StringComparison.OrdinalIgnoreCase)
            && IsRoguelikeProfessionalSquad(squad);
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

        var firstOperUsesSupport = startingOpers.Count > 0 && startingOpers[0].UseSupport;
        if (firstOperUsesSupport && startWithEliteTwo)
        {
            issues.Add(new TaskValidationIssue("RoguelikeEliteTwoSupportConflict", "roguelike.use_support", "UseSupport conflicts with StartWithEliteTwo under current strategy.", Blocking: false));
            startWithEliteTwo = false;
            onlyStartWithEliteTwo = false;
        }

        var startFoldartalList = NormalizeWpfSemicolonList(dto.StartFoldartalList, trimEntries: false, preserveEmptyEntries: false);
        if (startFoldartalList.Count > 3)
        {
            issues.Add(new TaskValidationIssue("RoguelikeStartFoldartalListTrimmed", "roguelike.start_foldartal_list", "Start foldartal list exceeds max size and will be trimmed to 3.", Blocking: false));
            startFoldartalList = startFoldartalList.Take(3).ToList();
        }

        var expectedCollapsalParadigms = NormalizeWpfSemicolonList(dto.ExpectedCollapsalParadigms, trimEntries: false, preserveEmptyEntries: false);

        var startWithSeed = (dto.StartWithSeed ?? string.Empty).Trim();
        if (dto.StartWithSeedEnabled && !string.IsNullOrWhiteSpace(startWithSeed) && !RoguelikeSeedRegex.IsMatch(startWithSeed))
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

        if (startingOpers.Count > 0)
        {
            parameters["core_char_list"] = CompileRoguelikeCoreCharList(startingOpers);
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
            parameters["collectible_mode_start_list"] = skipCollectibleStartRewards
                ? new JsonObject()
                : CompileCollectibleStartList(dto.CollectibleModeStartList, theme);
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

        // Frozen WPF serializes foldartal fields only for the Sami collectible-mode
        // combinations; Core reads them under the same restrictions.
        if (dto.FirstFloorFoldartalEnabled
            && mode == 4
            && string.Equals(theme, "Sami", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(dto.FirstFloorFoldartal))
        {
            parameters["first_floor_foldartal"] = dto.FirstFloorFoldartal.Trim();
        }

        if (dto.StartFoldartalListEnabled
            && mode == 4
            && string.Equals(theme, "Sami", StringComparison.OrdinalIgnoreCase)
            && string.Equals(squad, "生活至上分队", StringComparison.Ordinal)
            && startFoldartalList.Count > 0)
        {
            parameters["start_foldartal_list"] = ToJsonArrayPreservingEntries(startFoldartalList);
        }

        // Frozen WPF omits the key for empty paradigm text; Core treats a present but
        // empty array as an explicit empty expectation set and would never stop.
        if (mode == 5 && expectedCollapsalParadigms.Count > 0)
        {
            parameters["expected_collapsal_paradigms"] = ToJsonArrayPreservingEntries(expectedCollapsalParadigms);
        }

        if (dto.StartWithSeedEnabled && !string.IsNullOrWhiteSpace(startWithSeed))
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

    private static List<RoguelikeStartingOperDto> ReadRoguelikeStartingOpers(
        JsonObject parameters,
        ICollection<TaskValidationIssue> issues)
    {
        var result = new List<RoguelikeStartingOperDto>();
        if (parameters["core_char_list"] is not JsonArray array)
        {
            if (parameters.ContainsKey("core_char_list"))
            {
                issues.Add(new TaskValidationIssue(
                    "TaskFieldTypeInvalid",
                    "roguelike.core_char_list",
                    "Task field `core_char_list` has incompatible type."));
            }

            return result;
        }

        foreach (var item in array)
        {
            if (item is not JsonObject oper)
            {
                issues.Add(new TaskValidationIssue(
                    "TaskFieldTypeInvalid",
                    "roguelike.core_char_list",
                    "Task field `core_char_list` contains an entry with incompatible type."));
                continue;
            }

            var name = oper["name"] is JsonValue nameNode && nameNode.TryGetValue(out string? parsedName)
                ? parsedName.Trim()
                : string.Empty;
            var useSupport = oper["use_support"] is JsonValue supportNode
                && TryReadBooleanNode(supportNode, out var parsedSupport)
                && parsedSupport;
            result.Add(new RoguelikeStartingOperDto
            {
                Name = name,
                UseSupport = useSupport,
            });
        }

        if (result.Count > 3)
        {
            issues.Add(new TaskValidationIssue(
                "RoguelikeStartingOpersTrimmed",
                "roguelike.core_char_list",
                "Starting oper list exceeds max size and will be trimmed to 3.",
                Blocking: false));
            result = result.Take(3).ToList();
        }

        return result;
    }

    private static List<RoguelikeStartingOperDto> ResolveRoguelikeStartingOpers(
        RoguelikeTaskParamsDto dto,
        bool keepEmptyEntries = false)
    {
        IEnumerable<RoguelikeStartingOperDto> source = dto.CoreCharList;
        if (!source.Any() && (!string.IsNullOrWhiteSpace(dto.CoreChar) || dto.UseSupport))
        {
            source =
            [
                new RoguelikeStartingOperDto
                {
                    Name = dto.CoreChar,
                    UseSupport = dto.UseSupport,
                },
            ];
        }

        var opers = source
            .Take(3)
            .Select(oper => new RoguelikeStartingOperDto
            {
                Name = (oper.Name ?? string.Empty).Trim(),
                UseSupport = oper.UseSupport,
            });
        if (!keepEmptyEntries)
        {
            opers = opers.Where(oper => oper.Name.Length > 0);
        }

        return opers.ToList();
    }

    private static JsonArray CompileRoguelikeCoreCharList(IEnumerable<RoguelikeStartingOperDto> opers)
    {
        var list = new JsonArray();
        foreach (var oper in opers)
        {
            list.Add(new JsonObject
            {
                ["name"] = oper.Name,
                ["use_support"] = oper.UseSupport,
            });
        }

        return list;
    }

    public static TaskCompileOutput CompileRoguelikeForPersistence(
        UnifiedTaskItem task,
        UnifiedProfile profile,
        UnifiedConfig config,
        bool strict)
    {
        var (dto, readIssues) = ReadRoguelike(task, strict);
        var compiled = CompileRoguelike(dto, profile, config);
        var persistedParams = BuildRoguelikePersistenceParams(dto, compiled.Params);
        var allIssues = readIssues.Concat(compiled.Issues).ToList();
        return new TaskCompileOutput
        {
            NormalizedType = compiled.NormalizedType,
            Params = persistedParams,
            Issues = allIssues,
        };
    }

    public static JsonObject BuildRoguelikePersistenceParams(
        RoguelikeTaskParamsDto dto,
        JsonObject compiledParams)
    {
        // Frozen WPF persists every RoguelikeTask field per task and gates only at
        // serialization, so the stored form keeps hidden mode-, investment-, and
        // theme-gated user intent. Core-bound params are always recompiled from the
        // stored form through CompileRoguelike, so preserved keys never leak into a
        // run.
        var parameters = compiledParams.DeepClone() as JsonObject ?? new JsonObject();
        var theme = parameters["theme"]?.GetValue<string>() ?? string.Empty;

        parameters["refresh_trader_with_dice"] = dto.RefreshTraderWithDice;
        parameters["investment_with_more_score"] = dto.InvestmentWithMoreScore;
        parameters["investments_count"] = Math.Max(0, dto.InvestmentsCount);
        parameters["stop_when_investment_full"] = dto.StopWhenInvestmentFull;
        parameters["stop_at_final_boss"] = dto.StopAtFinalBoss;
        parameters["stop_at_max_level"] = dto.StopAtMaxLevel;
        parameters["collectible_mode_shopping"] = dto.CollectibleModeShopping;
        parameters["collectible_mode_squad"] = dto.CollectibleModeSquad.Trim();
        parameters["start_with_elite_two"] = dto.StartWithEliteTwo;
        parameters["only_start_with_elite_two"] = dto.OnlyStartWithEliteTwo;
        parameters["collectible_mode_start_list"] = CompileCollectibleStartList(dto.CollectibleModeStartList, theme);
        parameters["monthly_squad_auto_iterate"] = dto.MonthlySquadAutoIterate;
        parameters["monthly_squad_check_comms"] = dto.MonthlySquadCheckComms;
        parameters["deep_exploration_auto_iterate"] = dto.DeepExplorationAutoIterate;
        parameters["find_playTime_target"] = dto.FindPlayTimeTarget;
        parameters["first_floor_foldartal_enabled"] = dto.FirstFloorFoldartalEnabled;
        parameters["start_foldartal_list_enabled"] = dto.StartFoldartalListEnabled;
        parameters["start_with_seed_enabled"] = dto.StartWithSeedEnabled;

        // Persist the full starting-oper list (including intentionally empty later
        // positions) so a dev-v2 StartingOpers import survives a single-value UI save.
        parameters.Remove("core_char");
        parameters.Remove("use_support");
        var persistedStartingOpers = ResolveRoguelikeStartingOpers(dto, keepEmptyEntries: true);
        // Drop trailing placeholders so a fresh task does not persist a single empty
        // position, while interior/trailing filled slots keep their configured order.
        while (persistedStartingOpers.Count > 0
            && persistedStartingOpers[^1].Name.Length == 0
            && !persistedStartingOpers[^1].UseSupport)
        {
            persistedStartingOpers.RemoveAt(persistedStartingOpers.Count - 1);
        }

        if (persistedStartingOpers.Count > 0)
        {
            parameters["core_char_list"] = CompileRoguelikeCoreCharList(persistedStartingOpers);
        }
        else
        {
            parameters.Remove("core_char_list");
        }

        var firstFloorFoldartal = dto.FirstFloorFoldartal.Trim();
        if (firstFloorFoldartal.Length > 0)
        {
            parameters["first_floor_foldartal"] = firstFloorFoldartal;
        }
        else
        {
            parameters.Remove("first_floor_foldartal");
        }

        var startFoldartalList = NormalizeWpfSemicolonList(dto.StartFoldartalList, trimEntries: false, preserveEmptyEntries: false);
        if (startFoldartalList.Count > 3)
        {
            startFoldartalList = startFoldartalList.Take(3).ToList();
        }

        if (startFoldartalList.Count > 0)
        {
            parameters["start_foldartal_list"] = ToJsonArrayPreservingEntries(startFoldartalList);
        }
        else
        {
            parameters.Remove("start_foldartal_list");
        }

        var expectedCollapsalParadigms = NormalizeWpfSemicolonList(dto.ExpectedCollapsalParadigms, trimEntries: false, preserveEmptyEntries: false);
        if (expectedCollapsalParadigms.Count > 0)
        {
            parameters["expected_collapsal_paradigms"] = ToJsonArrayPreservingEntries(expectedCollapsalParadigms);
        }
        else
        {
            parameters.Remove("expected_collapsal_paradigms");
        }

        // Frozen WPF keeps the stored seed when the toggle is off; the compiled
        // form already gates emission on the toggle, so re-overlay the raw value.
        var persistedSeed = (dto.StartWithSeed ?? string.Empty).Trim();
        if (persistedSeed.Length > 0)
        {
            parameters["start_with_seed"] = persistedSeed;
        }
        else
        {
            parameters.Remove("start_with_seed");
        }

        return parameters;
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
            ToolsToCraft = ReadWpfSemicolonListCompat(parameters, "tools_to_craft", issues, "reclamation.tools_to_craft", trimEntries: true),
            ClearStore = ReadBool(parameters, "clear_store", strict, issues, "reclamation.clear_store", true),
        };

        return (dto, issues);
    }

    public static TaskCompileOutput CompileReclamation(
        ReclamationTaskParamsDto dto,
        UnifiedProfile profile,
        UnifiedConfig config)
    {
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
        if (numCraftBatches < 0)
        {
            issues.Add(new TaskValidationIssue("ReclamationNumCraftBatchesOutOfRange", "reclamation.num_craft_batches", "NumCraftBatches must be greater than or equal to zero.", Blocking: false));
            numCraftBatches = 0;
        }

        var toolsToCraft = NormalizeWpfSemicolonList(dto.ToolsToCraft, trimEntries: true, preserveEmptyEntries: true);
        if (toolsToCraft.Count == 0)
        {
            var clientType = ResolveStringSetting(profile, config, "ClientType", "Start.ClientType");
            toolsToCraft = [ReclamationTaskParamsDto.ResolveDefaultToolToCraft(clientType)];
        }

        var isRelaunchAnchor = string.Equals(theme, "RelaunchAnchor", StringComparison.OrdinalIgnoreCase);
        if (isRelaunchAnchor && toolsToCraft.Count > 0)
        {
            issues.Add(new TaskValidationIssue("ReclamationToolsIgnoredInNoArchive", "reclamation.tools_to_craft", "ToolsToCraft is ignored by RelaunchAnchor modes and will be cleared.", Blocking: false));
            toolsToCraft = [];
        }

        var clearStore = dto.ClearStore;
        if (isRelaunchAnchor && clearStore)
        {
            issues.Add(new TaskValidationIssue(
                "ReclamationClearStoreIgnoredInArchive",
                "reclamation.clear_store",
                "ClearStore is ignored by RelaunchAnchor modes and will be disabled.",
                Blocking: false));
            clearStore = false;
        }

        var parameters = new JsonObject
        {
            ["theme"] = theme,
            ["mode"] = mode,
            ["increment_mode"] = incrementMode,
            ["num_craft_batches"] = numCraftBatches,
            ["tools_to_craft"] = ToJsonArrayPreservingEntries(toolsToCraft),
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
        // WPF splits only the editor's comma-separated input, then passes each
        // trimmed name to MaaCore without interpreting it as a task type.
        var taskNames = dto.TaskNames.Select(taskName => taskName.Trim()).ToList();
        if (taskNames.Count == 0)
        {
            issues.Add(new TaskValidationIssue("CustomTaskNamesEmpty", "custom.task_names", "Custom task names list is empty.", Blocking: false));
        }

        var taskNamesJson = new JsonArray();
        foreach (var taskName in taskNames)
        {
            taskNamesJson.Add(taskName);
        }

        var parameters = new JsonObject
        {
            ["task_names"] = taskNamesJson,
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

        // WPF parity: a bare SingleStep marker carries no parameter keys at all
        // (WPF SingleStepTask has no settings), so it must not raise strict-mode
        // missing-field issues; CompileSingleStep turns it into a skippable marker.
        var isBareMarker = !parameters.ContainsKey("type")
            && !parameters.ContainsKey("subtype")
            && !parameters.ContainsKey("details");
        if (isBareMarker)
        {
            return (new SingleStepTaskParamsDto(), issues);
        }

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

        // WPF parity: a bare SingleStep marker carries no parameters at all (WPF
        // SingleStepTask has no settings view model), so WPF skips it and keeps
        // appending the remaining queue instead of failing the whole run.
        if (type.Length == 0 && subtype.Length == 0 && dto.Details is null)
        {
            issues.Add(new TaskValidationIssue(
                SkipAppendIssueCode,
                "single_step.type",
                "SingleStep marker has no runtime parameters; skipping append to match WPF marker behavior.",
                Blocking: false));

            return new TaskCompileOutput
            {
                NormalizedType = TaskModuleTypes.SingleStep,
                Params = new JsonObject(),
                Issues = issues,
            };
        }

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
            "Infrast" => CompileInfrastFromTask(task),
            "Mall" => CompileMallFromTask(task, profile, config, strict),
            "Recruit" => CompileRecruitFromTask(task, profile, config, strict),
            "Award" => CompileAwardFromTask(task),
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

        if (string.Equals(normalized, TaskModuleTypes.Roguelike, StringComparison.OrdinalIgnoreCase))
        {
            return CompileRoguelikeForPersistence(task, profile, config, strict);
        }

        if (string.Equals(normalized, TaskModuleTypes.StartUp, StringComparison.OrdinalIgnoreCase))
        {
            var startUpCompiled = CompileStartUpFromTask(task, profile, config, strict);
            PreserveStoredStartUpAccountName(task, startUpCompiled.Params);
            return startUpCompiled;
        }

        if (string.Equals(normalized, TaskModuleTypes.Infrast, StringComparison.OrdinalIgnoreCase))
        {
            return new TaskCompileOutput
            {
                NormalizedType = normalized,
                Params = NormalizeInfrastParams(task.Params),
                Issues = [],
            };
        }

        return CompileTask(task, profile, config, strict);
    }

    // WPF parity (StartUpSettingsUserControlModel): the stored StartUp account name is
    // never cleared because of the client type; only the serialized Core payload blanks
    // it for non-Official/Bilibili clients. Persistence write-back sites call this helper
    // to restore the previously stored value onto the compiled parameters. The caller
    // must invoke it before replacing task.Params.
    public static void PreserveStoredStartUpAccountName(UnifiedTaskItem task, JsonObject compiledParams)
    {
        if (task.Params?["account_name"] is JsonValue storedAccountNameNode
            && storedAccountNameNode.TryGetValue(out string? storedAccountName))
        {
            compiledParams["account_name"] = storedAccountName;
        }
        else
        {
            compiledParams.Remove("account_name");
        }
    }

    public static JsonObject BuildCoreParams(string taskType, JsonObject parameters)
    {
        var isInfrast = string.Equals(
            NormalizeTaskType(taskType),
            TaskModuleTypes.Infrast,
            StringComparison.OrdinalIgnoreCase);
        var runtimeParams = isInfrast
            ? NormalizeInfrastParams(parameters)
            : parameters.DeepClone() as JsonObject ?? new JsonObject();
        var normalizedType = NormalizeTaskType(taskType);
        if (string.Equals(normalizedType, TaskModuleTypes.Recruit, StringComparison.OrdinalIgnoreCase))
        {
            // The preserve-tag enable flag is MAAUnified UI state; Core only takes
            // preserve_tags. WPF dev-v2 serializes an empty array when the toggle is
            // off, so mirror that instead of leaking the marker key.
            if (runtimeParams.ContainsKey(UiRecruitPreserveTagsEnabled))
            {
                if (!ReadBooleanNode(runtimeParams, UiRecruitPreserveTagsEnabled))
                {
                    runtimeParams["preserve_tags"] = new JsonArray();
                }

                runtimeParams.Remove(UiRecruitPreserveTagsEnabled);
            }

            if (!ReadBooleanNode(runtimeParams, "report_to_penguin"))
            {
                runtimeParams.Remove("penguin_id");
            }

            if (!ReadBooleanNode(runtimeParams, "report_to_yituliu"))
            {
                runtimeParams.Remove("yituliu_id");
            }
        }
        else if (string.Equals(normalizedType, TaskModuleTypes.Mall, StringComparison.OrdinalIgnoreCase))
        {
            runtimeParams.Remove("credit_fight_once_a_day");
            runtimeParams.Remove("visit_friends_once_a_day");
            runtimeParams.Remove(UiMallCreditFightLastTime);
            runtimeParams.Remove(UiMallVisitFriendsLastTime);
        }
        else if (string.Equals(normalizedType, TaskModuleTypes.Award, StringComparison.OrdinalIgnoreCase))
        {
            var allowed = new[] { "award", "mail", "recruit", "orundum", "mining", "specialaccess" };
            foreach (var key in runtimeParams.Select(pair => pair.Key).Where(key => !allowed.Contains(key, StringComparer.Ordinal)).ToList())
            {
                runtimeParams.Remove(key);
            }
        }

        if (isInfrast)
        {
            runtimeParams.Remove(UiInfrastFacilityOrder);
            if ((runtimeParams["mode"]?.GetValue<int?>() ?? 0) != 10000)
            {
                runtimeParams.Remove("filename");
                runtimeParams.Remove("plan_index");
            }

            return runtimeParams;
        }


        if (!string.Equals(normalizedType, TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase))
        {
            return runtimeParams;
        }

        if (runtimeParams["stage"] is JsonValue stageValue
            && stageValue.TryGetValue(out string? stage))
        {
            runtimeParams["stage"] = FightStageSelection.ToCoreStage(stage);
        }

        foreach (var key in runtimeParams.Select(static pair => pair.Key).ToArray())
        {
            if (key.StartsWith("_ui_", StringComparison.Ordinal))
            {
                runtimeParams.Remove(key);
            }
        }

        if (runtimeParams["report_to_penguin"]?.GetValue<bool>() != true
            || string.IsNullOrWhiteSpace(runtimeParams["penguin_id"]?.GetValue<string>()))
        {
            runtimeParams.Remove("penguin_id");
        }

        if (runtimeParams["report_to_yituliu"]?.GetValue<bool>() != true
            || string.IsNullOrWhiteSpace(runtimeParams["yituliu_id"]?.GetValue<string>()))
        {
            runtimeParams.Remove("yituliu_id");
        }

        return runtimeParams;
    }

    private static string ResolveFightServerType(UnifiedProfile profile, UnifiedConfig config)
    {
        var explicitServer = ResolveStringSetting(profile, config, "ServerType", "Server");
        if (!string.IsNullOrWhiteSpace(explicitServer))
        {
            return explicitServer.Trim();
        }

        var clientType = ResolveStringSetting(profile, config, "Start.ClientType", "ClientType") ?? "Official";
        return clientType.Trim() switch
        {
            var value when value.Equals("YoStarEN", StringComparison.OrdinalIgnoreCase) => "US",
            var value when value.Equals("YoStarJP", StringComparison.OrdinalIgnoreCase) => "JP",
            var value when value.Equals("YoStarKR", StringComparison.OrdinalIgnoreCase) => "KR",
            var value when value.Equals("txwy", StringComparison.OrdinalIgnoreCase) => "ZH_TW",
            _ => "CN",
        };
    }

    private static bool ReadBooleanNode(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var node)
            && node is JsonValue value
            && value.TryGetValue(out bool result)
            && result;
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

    private static TaskCompileOutput CompileInfrastFromTask(UnifiedTaskItem task)
    {
        var parameters = NormalizeInfrastParams(task.Params);
        var issues = new List<TaskValidationIssue>();
        var mode = parameters["mode"]?.GetValue<int?>() ?? 0;
        if (mode == 10000)
        {
            var filename = parameters["filename"]?.GetValue<string>() ?? string.Empty;
            try
            {
                var plans = InfrastPlanParser.ParseFile(filename);
                var selected = parameters["plan_index"]?.GetValue<int?>() ?? -1;
                if (plans.Count > 0 && selected >= plans.Count)
                {
                    // Frozen WPF InfrastTask.OnDeserialized resets PlanSelect >= Count to 0 at load;
                    // clamp here so a stale persisted index can never block the append.
                    issues.Add(new TaskValidationIssue(
                        "InfrastCustomPlanIndexClamped",
                        "infrast.plan_index",
                        $"Custom infrast plan index {selected} is out of range for {plans.Count} plan(s); reset to 0.",
                        Blocking: false));
                    selected = 0;
                }

                parameters["plan_index"] = InfrastPlanParser.ResolvePlanIndex(
                    plans,
                    selected,
                    TimeOnly.FromDateTime(DateTime.Now));
            }
            catch (Exception ex)
            {
                issues.Add(new TaskValidationIssue(
                    "InfrastCustomConfigInvalid",
                    "infrast.custom_config",
                    ex.Message));
            }
        }

        return new TaskCompileOutput
        {
            NormalizedType = TaskModuleTypes.Infrast,
            Params = parameters,
            Issues = issues,
        };
    }

    private static JsonObject NormalizeInfrastParams(JsonObject? parameters)
    {
        var normalized = parameters?.DeepClone() as JsonObject ?? new JsonObject();
        var canonical = InfrastParams.FromJson(parameters).ToJson();
        foreach (var property in canonical)
        {
            normalized[property.Key] = property.Value?.DeepClone();
        }

        return normalized;
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

        var creditFight = model.CreditFight && !HasCurrentOrLastFightStage(profile, config, nowUtc);
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
        // Frozen WPF parity: Mall serialization unconditionally unions the
        // client-type operator blacklist into the user's list
        // (MallSettingsUserControlModel.cs:239). The extra names follow the
        // game client type, not the UI language, and the union uses the same
        // case-sensitive ordinal comparison as WPF's Enumerable.Union. The
        // persisted profile list itself is never mutated here.
        var blacklist = model.Blacklist
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Union(ResolveMallClientTypeBlacklist(clientType), StringComparer.Ordinal)
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
        // Frozen WPF parity: stored Mall lists are kept verbatim (duplicates and
        // case variants included); only trimming and empty-entry removal are
        // applied. Core matches goods names case-sensitively, so collapsing case
        // variants could drop the variant that would actually match.
        return values
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
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

    private static TaskCompileOutput CompileAwardFromTask(UnifiedTaskItem task)
    {
        return new TaskCompileOutput
        {
            NormalizedType = TaskModuleTypes.Award,
            Params = AwardParams.FromJson(task.Params).ToJson(),
            Issues = [],
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

    private static List<string> NormalizeRecruitTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return [];
        }

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
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

    private static JsonArray ToJsonArrayPreservingEntries(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
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

    private static List<string> NormalizeWpfSemicolonList(
        IEnumerable<string> values,
        bool trimEntries,
        bool preserveEmptyEntries)
    {
        var result = new List<string>();
        foreach (var value in values)
        {
            var normalized = trimEntries ? (value ?? string.Empty).Trim() : value ?? string.Empty;
            if (!preserveEmptyEntries && string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            result.Add(normalized);
        }

        return result;
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

    private static IReadOnlyList<string> ResolveMallClientTypeBlacklist(string clientType)
    {
        // WPF's settings layer coerces unknown client types to Official, so an
        // unrecognized value here falls back the same way.
        return MallClientTypeBlacklist.TryGetValue(clientType, out var values)
            ? values
            : MallClientTypeBlacklist["Official"];
    }

    private static string ResolveRecruitServerType(UnifiedProfile profile, UnifiedConfig config)
    {
        // Frozen WPF parity: Recruit serialization always sets
        // ServerType = Instances.SettingsViewModel.ServerType
        // (RecruitSettingsUserControlModel.cs:285 at WPF commit
        // 23ddd271b40a3d0c18de8a6ae8305357cc032e19) and WPF has no ServerType
        // config key, so the value is purely derived from the client type.
        // Unknown client types fall back to Official (WPF settings coercion),
        // which maps to "CN".
        var clientType = ResolveStringSetting(profile, config, "ClientType", "Start.ClientType") ?? "Official";
        return RecruitClientTypeServer.TryGetValue(clientType, out var server)
            ? server
            : RecruitClientTypeServer["Official"];
    }

    /// <summary>
    /// Frozen WPF parity guard for Mall credit_fight
    /// (src/MaaWpfGui/ViewModels/UserControl/TaskQueue/MallSettingsUserControlModel.cs
    /// ISerialize at WPF commit 23ddd271b40a3d0c18de8a6ae8305357cc032e19): for every
    /// enabled Fight task, resolve the full persisted stage plan on every
    /// schedule-enabled day of the week via
    /// GetStageForDayOfWeek = StagePlan.FirstOrDefault(IsStageOpen) ?? StagePlan[0],
    /// where the current/last sentinel (WPF "") always counts as open
    /// (StageManager.GetStageInfo("") falls back to a permanent StageInfo). The
    /// guard fires when the resolved stage is current/last on ANY enabled day.
    /// </summary>
    public static bool HasCurrentOrLastFightStage(UnifiedProfile profile, UnifiedConfig config, DateTime utcNow)
    {
        var clientType = NormalizeFightClientType(ResolveStringSetting(profile, config, "ClientType", "Start.ClientType") ?? "Official");
        StageActivityState? stageState = null;
        foreach (var task in profile.TaskQueue)
        {
            if (!TaskQueueEnabledState.IsEffectivelyEnabled(task, config)
                || !string.Equals(TaskModuleTypes.Normalize(task.Type), TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parameters = task.Params ?? new JsonObject();
            var fallbackStage = FightStageSelection.NormalizeStoredValue(
                parameters.TryGetPropertyValue("stage", out var stageNode)
                && stageNode is JsonValue stageValue
                && stageValue.TryGetValue(out string? stageText)
                    ? stageText
                    : FightStageSelection.CurrentOrLast);
            var plan = ReadFightStagePlan(parameters, strict: false, issues: new List<TaskValidationIssue>(), fallbackStage);
            if (!ReadBool(parameters, UiUseAlternateStage, false) && plan.Count > 1)
            {
                // WPF FightTask.OnDeserialized truncates the stored plan to its
                // first entry while the alternate-stage switch is off.
                plan = [plan[0]];
            }

            var enabledDays = ResolveFightWeeklyEnabledDays(parameters);
            if (enabledDays.Count == 0)
            {
                continue;
            }

            stageState ??= new StageManagerFeatureService().GetStageActivityState(clientType);
            foreach (var day in enabledDays)
            {
                var resolved = plan.FirstOrDefault(candidate => IsFightStageOpenForMallCreditFightGuard(stageState, candidate, utcNow, day))
                    ?? plan[0];
                if (FightStageSelection.IsCurrentOrLast(resolved))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<DayOfWeek> ResolveFightWeeklyEnabledDays(JsonObject parameters)
    {
        // WPF: all seven days while the weekly schedule is off, otherwise only the
        // enabled days. Missing flags default to true, matching the WPF FightTask
        // WeeklySchedule defaults and ReadFight.
        if (!ReadBool(parameters, UiUseWeeklySchedule, false))
        {
            return Enum.GetValues<DayOfWeek>();
        }

        var days = new List<DayOfWeek>(7);
        if (ReadBool(parameters, UiWeeklyScheduleSunday, true))
        {
            days.Add(DayOfWeek.Sunday);
        }

        if (ReadBool(parameters, UiWeeklyScheduleMonday, true))
        {
            days.Add(DayOfWeek.Monday);
        }

        if (ReadBool(parameters, UiWeeklyScheduleTuesday, true))
        {
            days.Add(DayOfWeek.Tuesday);
        }

        if (ReadBool(parameters, UiWeeklyScheduleWednesday, true))
        {
            days.Add(DayOfWeek.Wednesday);
        }

        if (ReadBool(parameters, UiWeeklyScheduleThursday, true))
        {
            days.Add(DayOfWeek.Thursday);
        }

        if (ReadBool(parameters, UiWeeklyScheduleFriday, true))
        {
            days.Add(DayOfWeek.Friday);
        }

        if (ReadBool(parameters, UiWeeklyScheduleSaturday, true))
        {
            days.Add(DayOfWeek.Saturday);
        }

        return days;
    }

    private static bool IsFightStageOpenForMallCreditFightGuard(StageActivityState stageState, string candidate, DateTime utcNow, DayOfWeek day)
    {
        // WPF StageManager.GetStageInfo("") falls back to a permanent StageInfo,
        // so the current/last sentinel is always "open".
        if (FightStageSelection.IsCurrentOrLast(candidate))
        {
            return true;
        }

        var stage = stageState.Find(candidate);
        if (stage is not null)
        {
            return stage.IsOpen(utcNow, day);
        }

        // WPF treats unknown two-letter activity codes (e.g. "UR-5") as expired
        // activities (closed) and every other unknown name as a permanent stage
        // (always open).
        return !ExpiredActivityStageCodeRegex.IsMatch(candidate);
    }

    private static JsonObject CompileCollectibleStartList(RoguelikeCollectibleStartListDto? dto, string theme)
    {
        dto ??= new RoguelikeCollectibleStartListDto();
        return new JsonObject
        {
            ["hot_water"] = dto.HotWater,
            ["shield"] = dto.Shield,
            ["ingot"] = dto.Ingot,
            ["hope"] = !string.Equals(theme, "JieGarden", StringComparison.OrdinalIgnoreCase) && dto.Hope,
            ["random"] = dto.Random,
            ["key"] = string.Equals(theme, "Mizuki", StringComparison.OrdinalIgnoreCase) && dto.Key,
            ["dice"] = string.Equals(theme, "Mizuki", StringComparison.OrdinalIgnoreCase) && dto.Dice,
            ["ideas"] = string.Equals(theme, "Sarkaz", StringComparison.OrdinalIgnoreCase) && dto.Ideas,
            ["ticket"] = string.Equals(theme, "JieGarden", StringComparison.OrdinalIgnoreCase) && dto.Ticket,
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

    private static List<string> ReadWpfSemicolonListCompat(
        JsonObject obj,
        string key,
        ICollection<TaskValidationIssue> issues,
        string field,
        bool trimEntries)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return [];
        }

        if (node is JsonArray array)
        {
            var result = new List<string>();
            var hasInvalidEntry = false;
            foreach (var item in array)
            {
                if (item is JsonValue jsonValue && jsonValue.TryGetValue(out string? parsed))
                {
                    result.Add(trimEntries ? (parsed ?? string.Empty).Trim() : parsed ?? string.Empty);
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

            return result;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            var normalized = (text ?? string.Empty).Replace('；', ';').Trim();
            if (normalized.Length == 0)
            {
                return [];
            }

            return normalized
                .Split(';')
                .Select(item => trimEntries ? item.Trim() : item)
                .Where(item => trimEntries || !string.IsNullOrEmpty(item))
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
        // Frozen WPF has no separate Yituliu ID setting: Recruit ISerialize passes
        // GameSettings.PenguinId for both penguin_id and yituliu_id, so a legacy
        // YituliuId value is not consulted here.
        return ResolveStringSetting(profile, config, "Penguin.Id", "PenguinId")
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
