using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;

namespace MAAUnified.Application.Configuration;

internal sealed class LegacyTaskSchemaConversionOptions
{
    public static readonly LegacyTaskSchemaConversionOptions GuiJsonFlat = new(false, false, false);
    public static readonly LegacyTaskSchemaConversionOptions GuiNewJson = new(true, true, true);

    private LegacyTaskSchemaConversionOptions(
        bool missingRoguelikeIterationFlagsDefault,
        bool missingInfrastDormTrustDefault,
        bool missingInfrastRoomListDefault)
    {
        MissingRoguelikeIterationFlagsDefault = missingRoguelikeIterationFlagsDefault;
        MissingInfrastDormTrustDefault = missingInfrastDormTrustDefault;
        MissingInfrastRoomListDefault = missingInfrastRoomListDefault;
    }

    public bool MissingRoguelikeIterationFlagsDefault { get; }

    public bool MissingInfrastDormTrustDefault { get; }

    public bool MissingInfrastRoomListDefault { get; }
}

internal static class LegacyTaskSchemaConverter
{
    private static readonly Dictionary<string, string> _legacyTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["StartUpTask"] = "StartUp",
        ["CloseDownTask"] = "CloseDown",
        ["FightTask"] = "Fight",
        ["RecruitTask"] = "Recruit",
        ["InfrastTask"] = "Infrast",
        ["MallTask"] = "Mall",
        ["AwardTask"] = "Award",
        ["RoguelikeTask"] = "Roguelike",
        ["ReclamationTask"] = "Reclamation",
        ["UserDataUpdateTask"] = "UserDataUpdate",
        ["SingleStepTask"] = "SingleStep",
        ["CustomTask"] = "Custom",
        // Accept already migrated type names.
        ["StartUp"] = "StartUp",
        ["CloseDown"] = "CloseDown",
        ["Fight"] = "Fight",
        ["Recruit"] = "Recruit",
        ["Infrast"] = "Infrast",
        ["Mall"] = "Mall",
        ["Award"] = "Award",
        ["Roguelike"] = "Roguelike",
        ["Reclamation"] = "Reclamation",
        ["UserDataUpdate"] = "UserDataUpdate",
        ["SingleStep"] = "SingleStep",
        ["Custom"] = "Custom",
    };

    public static bool TryConvertLegacyTask(
        JsonObject legacyTask,
        UnifiedProfile profile,
        UnifiedConfig config,
        out UnifiedTaskItem taskItem,
        out string? error,
        LegacyTaskSchemaConversionOptions? options = null,
        string? uiLanguage = null)
    {
        options ??= LegacyTaskSchemaConversionOptions.GuiJsonFlat;

        var rawLegacyType = GetString(legacyTask, "$type")
            ?? GetString(legacyTask, "Type")
            ?? "Unknown";
        var legacyType = NormalizeTypeName(rawLegacyType);

        var name = GetString(legacyTask, "Name")
            ?? GetString(legacyTask, "name")
            ?? legacyType;
        var isEnabled = GetBool(legacyTask, "IsEnable", true);

        if (!_legacyTypeMap.TryGetValue(legacyType, out var coreType))
        {
            taskItem = new UnifiedTaskItem
            {
                Type = legacyType,
                Name = name,
                IsEnabled = false,
                Params = [],
                LegacyRawTask = (JsonObject?)legacyTask.DeepClone(),
            };
            error = $"Unsupported legacy task type `{rawLegacyType}`.";
            return false;
        }

        JsonObject @params;
        try
        {
            @params = coreType switch
            {
                "StartUp" => ConvertStartUp(legacyTask, profile, config),
                "CloseDown" => ConvertCloseDown(profile, config),
                "Fight" => ConvertFight(legacyTask, profile, config),
                "Recruit" => ConvertRecruit(legacyTask, profile, config),
                "Infrast" => ConvertInfrast(legacyTask, options),
                "Mall" => ConvertMall(legacyTask, ResolveUiLanguage(config, uiLanguage)),
                "Award" => ConvertAward(legacyTask),
                "Roguelike" => ConvertRoguelike(legacyTask, options),
                "Reclamation" => ConvertReclamation(legacyTask, profile, config),
                "UserDataUpdate" => ConvertUserDataUpdate(legacyTask),
                "SingleStep" => ConvertSingleStep(legacyTask),
                "Custom" => ConvertCustom(legacyTask),
                _ => [],
            };
        }
        catch (Exception ex)
        {
            taskItem = new UnifiedTaskItem
            {
                Type = coreType,
                Name = name,
                IsEnabled = false,
                Params = [],
                LegacyRawTask = (JsonObject?)legacyTask.DeepClone(),
            };
            error = $"Failed to convert legacy task `{name}` ({rawLegacyType}): {ex.Message}";
            return false;
        }

        taskItem = new UnifiedTaskItem
        {
            Type = coreType,
            Name = name,
            IsEnabled = isEnabled,
            Params = @params,
            LegacyRawTask = null,
        };
        error = null;
        return true;
    }

    public static bool TryUpgradeTaskToSchemaV2(
        UnifiedTaskItem task,
        UnifiedProfile profile,
        UnifiedConfig config,
        out UnifiedTaskItem upgradedTask,
        out string? error)
    {
        var normalizedType = NormalizeTypeName(task.Type);

        if (task.LegacyRawTask is null && !_legacyTypeMap.ContainsKey(normalizedType))
        {
            upgradedTask = CloneTask(task);
            error = null;
            return true;
        }

        if (task.LegacyRawTask is null && _legacyTypeMap.TryGetValue(normalizedType, out var alreadyCoreType))
        {
            upgradedTask = CloneTask(task);
            upgradedTask.Type = alreadyCoreType;
            upgradedTask.LegacyRawTask = null;
            error = null;
            return true;
        }

        var legacy = task.LegacyRawTask?.DeepClone() as JsonObject;
        if (legacy is null)
        {
            upgradedTask = CloneTask(task);
            upgradedTask.IsEnabled = false;
            upgradedTask.Params = [];
            error = $"Task `{task.Name}` cannot be migrated because no legacy payload was found.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(GetString(legacy, "$type")))
        {
            legacy["$type"] = task.Type;
        }

        if (string.IsNullOrWhiteSpace(GetString(legacy, "Name")))
        {
            legacy["Name"] = task.Name;
        }

        legacy["IsEnable"] = task.IsEnabled;

        var converted = TryConvertLegacyTask(legacy, profile, config, out upgradedTask, out error);
        if (!converted)
        {
            upgradedTask.IsEnabled = false;
        }

        return converted;
    }

    private static UnifiedTaskItem CloneTask(UnifiedTaskItem task)
    {
        return new UnifiedTaskItem
        {
            Type = task.Type,
            Name = task.Name,
            IsEnabled = task.IsEnabled,
            Params = (JsonObject)(task.Params.DeepClone() ?? new JsonObject()),
            LegacyRawTask = (JsonObject?)task.LegacyRawTask?.DeepClone(),
        };
    }

    private static JsonObject ConvertStartUp(JsonObject task, UnifiedProfile profile, UnifiedConfig config)
    {
        var clientType = ResolveClientType(profile, config);
        var accountName = GetString(task, "AccountName") ?? string.Empty;
        var startGameEnabled = ResolveBooleanSetting(profile, config, true, "StartGame", "Start.StartGame");
        return new JsonObject
        {
            ["client_type"] = clientType,
            ["start_game_enabled"] = startGameEnabled,
            ["account_name"] = accountName,
        };
    }

    private static JsonObject ConvertCloseDown(UnifiedProfile profile, UnifiedConfig config)
    {
        return new JsonObject
        {
            ["client_type"] = ResolveClientType(profile, config),
        };
    }

    private static JsonObject ConvertFight(JsonObject task, UnifiedProfile profile, UnifiedConfig config)
    {
        var stagePlan = ResolveFightStagePlan(task);
        var stage = FightStageSelection.NormalizeStoredValue(ResolveFightStage(task));
        var useMedicine = GetNullableBool(task, "UseMedicine") ?? false;
        var allowUseStoneSave = GetBool(task, "UseStoneAllowSave", false);
        var useStone = allowUseStoneSave && (GetNullableBool(task, "UseStone") ?? false);
        var useExpiringMedicine = GetBool(task, "UseExpiringMedicine", false);
        var expiringMedicine = GetInt(task, "MedicineExpireDays", 2);
        var useExpireMedicineForActivity = GetBool(task, "UseExpireMedicineForActivity", false);
        var enableTimesLimit = GetNullableBool(task, "EnableTimesLimit") ?? false;
        var enableTargetDrop = GetNullableBool(task, "EnableTargetDrop") ?? false;
        var dropId = GetString(task, "DropId") ?? string.Empty;
        var dropCount = GetInt(task, "DropCount", 5);
        var isInventoryTarget = GetBool(task, "IsInventoryTarget", false);
        var annihilationStage = GetString(task, "AnnihilationStage") ?? "Annihilation";

        // WPF ConfigConverter keeps the stored annihilation stage verbatim (a
        // hand-edited blank stays blank) and only preserves the custom flag when
        // the stored stage differs from the default "Annihilation".
        var useCustomAnnihilation = GetBool(task, "UseCustomAnnihilation", false)
            && !string.Equals(annihilationStage, "Annihilation", StringComparison.Ordinal);
        var useAlternateStage = GetBool(task, "UseOptionalStage", false);
        var hideUnavailableStage = GetBool(task, "HideUnavailableStage", true);
        var stageResetMode = ResolveFightStageResetMode(task);

        // Match CompileFight normalization rules so legacy import produces consistent UI metadata.
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

        if (string.Equals(stage, "Annihilation", StringComparison.OrdinalIgnoreCase)
            && useCustomAnnihilation
            && !string.IsNullOrWhiteSpace(annihilationStage))
        {
            stage = annihilationStage;
        }

        var result = new JsonObject
        {
            ["stage"] = stage,
            ["medicine"] = useMedicine ? GetInt(task, "MedicineCount", 999) : 0,
            ["expiring_medicine"] = useExpiringMedicine ? 9999 : 0,
            ["medicine_expire_days"] = useExpiringMedicine ? Math.Max(1, expiringMedicine) : 0,
            ["stone"] = useStone ? GetInt(task, "StoneCount", 0) : 0,
            ["times"] = enableTimesLimit ? GetInt(task, "TimesLimit", 5) : int.MaxValue,
            ["series"] = LegacyConfigValueMappings.NormalizeFightSeries(task["Series"]),
            ["DrGrandet"] = GetBool(task, "IsDrGrandet", false),
            ["report_to_penguin"] = ResolveBooleanSetting(profile, config, true, "Penguin.EnablePenguin", "EnablePenguin"),
            ["report_to_yituliu"] = ResolveBooleanSetting(profile, config, true, "Yituliu.EnableYituliu", "EnableYituliu"),
            ["penguin_id"] = ResolveStringSetting(profile, config, "Penguin.Id", "PenguinId"),
            ["yituliu_id"] = ResolveStringSetting(profile, config, "Penguin.Id", "YituliuId", "PenguinId"),
            ["server"] = ResolveServerType(profile, config),
            ["client_type"] = ResolveClientType(profile, config),
            ["_ui_stage_plan"] = ToJsonArray(stagePlan),
            ["_ui_is_stage_manually"] = GetBool(task, "IsStageManually", false),
            ["_ui_use_expiring_medicine"] = useExpiringMedicine,
            ["_ui_medicine_expire_days"] = Math.Max(1, expiringMedicine),
            ["_ui_use_medicine"] = CreateLegacyNullableBoolUiValue(task, "UseMedicine"),
            ["_ui_use_stone"] = useStone,
            ["_ui_enable_times_limit"] = CreateLegacyNullableBoolUiValue(task, "EnableTimesLimit"),
            ["_ui_enable_target_drop"] = CreateLegacyNullableBoolUiValue(task, "EnableTargetDrop"),
            ["_ui_drop_id"] = dropId.Trim(),
            ["_ui_drop_count"] = Math.Max(1, dropCount),
            ["_ui_is_inventory_target"] = isInventoryTarget,
            ["_ui_use_expire_medicine_for_activity"] = useExpireMedicineForActivity,
            ["_ui_use_alternate_stage"] = useAlternateStage,
            ["_ui_hide_unavailable_stage"] = hideUnavailableStage,
            ["_ui_stage_reset_mode"] = stageResetMode,
            ["_ui_use_custom_annihilation"] = useCustomAnnihilation,
            ["_ui_annihilation_stage"] = string.IsNullOrWhiteSpace(annihilationStage) ? annihilationStage : annihilationStage.Trim(),
            ["_ui_hide_series"] = GetBool(task, "HideSeries", false),
            ["_ui_allow_use_stone_save"] = allowUseStoneSave,
            ["_ui_use_weekly_schedule"] = GetBool(task, "UseWeeklySchedule", false),
            ["_ui_weekly_schedule_sunday"] = ResolveLegacyWeeklySchedule(task, DayOfWeek.Sunday),
            ["_ui_weekly_schedule_monday"] = ResolveLegacyWeeklySchedule(task, DayOfWeek.Monday),
            ["_ui_weekly_schedule_tuesday"] = ResolveLegacyWeeklySchedule(task, DayOfWeek.Tuesday),
            ["_ui_weekly_schedule_wednesday"] = ResolveLegacyWeeklySchedule(task, DayOfWeek.Wednesday),
            ["_ui_weekly_schedule_thursday"] = ResolveLegacyWeeklySchedule(task, DayOfWeek.Thursday),
            ["_ui_weekly_schedule_friday"] = ResolveLegacyWeeklySchedule(task, DayOfWeek.Friday),
            ["_ui_weekly_schedule_saturday"] = ResolveLegacyWeeklySchedule(task, DayOfWeek.Saturday),
        };

        if (enableTargetDrop)
        {
            if (!string.IsNullOrWhiteSpace(dropId))
            {
                var drops = new JsonObject
                {
                    [dropId] = Math.Max(1, dropCount),
                };
                result["drops"] = drops;
            }
        }

        return result;
    }

    private static JsonObject ConvertRecruit(JsonObject task, UnifiedProfile profile, UnifiedConfig config)
    {
        var maxTimes = GetInt(task, "MaxTimes", 4);
        var level1NotChoose = GetBool(task, "Level1NotChoose", true);
        var level3Choose = GetBool(task, "Level3Choose", true);
        var level4Choose = GetBool(task, "Level4Choose", true);
        var level5Choose = GetBool(task, "Level5Choose", false);
        // WPF task queue has no selectable six-star recruit option; legacy values are ignored.
        var level6Choose = false;
        var useExpedited = GetBool(task, "UseExpedited", false);

        var selectList = new JsonArray();
        var confirmList = new JsonArray();

        if (level1NotChoose)
        {
            confirmList.Add(1);
        }

        if (level3Choose)
        {
            confirmList.Add(3);
        }

        if (level4Choose)
        {
            selectList.Add(4);
            confirmList.Add(4);
        }

        if (level5Choose)
        {
            selectList.Add(5);
            confirmList.Add(5);
        }

        if (level6Choose)
        {
            selectList.Add(6);
            confirmList.Add(6);
        }

        var firstTags = ToJsonArray(ReadStringArray(task, "Level3PreferTags"));

        var result = new JsonObject
        {
            ["refresh"] = GetBool(task, "RefreshLevel3", true),
            ["force_refresh"] = GetBool(task, "ForceRefresh", true),
            ["select"] = selectList,
            ["confirm"] = confirmList,
            ["times"] = maxTimes,
            ["set_time"] = true,
            ["expedite"] = useExpedited,
            ["skip_robot"] = level1NotChoose,
            ["extra_tags_mode"] = GetInt(task, "ExtraTagMode", 0),
            ["first_tags"] = firstTags,
            ["recruitment_time"] = new JsonObject
            {
                ["3"] = GetInt(task, "Level3Time", 540),
                ["4"] = GetInt(task, "Level4Time", 540),
                ["5"] = GetInt(task, "Level5Time", 540),
            },
            ["report_to_penguin"] = ResolveBooleanSetting(profile, config, true, "Penguin.EnablePenguin", "EnablePenguin"),
            ["report_to_yituliu"] = ResolveBooleanSetting(profile, config, true, "Yituliu.EnableYituliu", "EnableYituliu"),
            ["penguin_id"] = ResolveStringSetting(profile, config, "Penguin.Id", "PenguinId"),
            ["yituliu_id"] = ResolveStringSetting(profile, config, "Penguin.Id", "YituliuId", "PenguinId"),
            ["server"] = ResolveServerType(profile, config),
        };

        if (useExpedited)
        {
            result["expedite_times"] = maxTimes;
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
            if (item is JsonValue value
                && value.TryGetValue(out string? tagText)
                && !string.IsNullOrWhiteSpace(tagText))
            {
                result.Add(tagText.Trim());
            }
        }

        return result;
    }

    private static JsonObject ConvertInfrast(JsonObject task, LegacyTaskSchemaConversionOptions options)
    {
        var facilities = new JsonArray();
        var facilityOrder = new JsonArray();
        if (task["RoomList"] is JsonArray roomList)
        {
            foreach (var roomNode in roomList)
            {
                if (roomNode is not JsonObject room)
                {
                    continue;
                }

                var roomName = GetString(room, "Room");
                if (!string.IsNullOrWhiteSpace(roomName))
                {
                    facilityOrder.Add(roomName);
                    if (!GetBool(room, "IsEnabled", true))
                    {
                        continue;
                    }

                    facilities.Add(roomName);
                }
            }
        }
        else if (options.MissingInfrastRoomListDefault)
        {
            foreach (var roomName in new[] { "Mfg", "Trade", "Control", "Power", "Reception", "Office", "Dorm", "Processing", "Training" })
            {
                facilities.Add(roomName);
                facilityOrder.Add(roomName);
            }
        }

        var mode = LegacyConfigValueMappings.NormalizeInfrastMode(task["Mode"]);
        var result = new JsonObject
        {
            ["drones"] = GetString(task, "UsesOfDrones") ?? "Money",
            ["continue_training"] = GetBool(task, "ContinueTraining", false),
            ["threshold"] = GetInt(task, "DormThreshold", 30) / 100.0,
            ["dorm_notstationed_enabled"] = GetBool(task, "DormFilterNotStationed", true),
            ["dorm_trust_enabled"] = GetBool(task, "DormTrustEnabled", options.MissingInfrastDormTrustDefault),
            ["replenish"] = GetBool(task, "OriginiumShardAutoReplenishment", true),
            ["reception_message_board"] = GetBool(task, "ReceptionMessageBoard", true),
            ["reception_clue_exchange"] = GetBool(task, "ReceptionClueExchange", true),
            ["reception_send_clue"] = GetBool(task, "SendClue", true),
            ["mode"] = mode,
        };

        // Frozen WPF serializes Filename/PlanSelect unconditionally and the flat migration maps
        // CustomInfrastFile/CustomInfrastPlanSelect regardless of mode, so persist the saved Custom
        // selection for every mode. Non-Custom runtime Core params still strip them in BuildCoreParams.
        result["filename"] = GetString(task, "Filename") ?? string.Empty;
        result["plan_index"] = Math.Max(-1, GetInt(task, "PlanSelect", -1));

        if (task["RoomList"] is JsonArray || options.MissingInfrastRoomListDefault)
        {
            result["facility"] = facilities;
            result["_ui_facility_order"] = facilityOrder;
        }

        return result;
    }

    private static JsonObject ConvertMall(JsonObject task, string language)
    {
        var defaults = TaskModuleParameterDefaults.CreateMallDefaults(language);
        var buyFirst = task.ContainsKey("FirstList")
            ? ToJsonArray(SplitNonEmpty(GetString(task, "FirstList"), ';'))
            : defaults["buy_first"]?.DeepClone() as JsonArray ?? [];
        var blacklist = task.ContainsKey("BlackList")
            ? ToJsonArray(SplitNonEmpty(GetString(task, "BlackList"), ';'))
            : defaults["blacklist"]?.DeepClone() as JsonArray ?? [];

        return new JsonObject
        {
            ["credit_fight"] = GetBool(task, "CreditFight", false),
            ["credit_fight_once_a_day"] = GetBool(task, "CreditFightOnceADay", true),
            ["_ui_mall_credit_fight_last_time"] = GetString(task, "CreditFightLastTime") ?? string.Empty,
            ["formation_index"] = GetInt(task, "CreditFightFormation", 0),
            ["visit_friends"] = GetBool(task, "VisitFriends", true),
            ["visit_friends_once_a_day"] = GetBool(task, "VisitFriendsOnceADay", false),
            ["_ui_mall_visit_friends_last_time"] = GetString(task, "VisitFriendsLastTime") ?? string.Empty,
            ["shopping"] = GetBool(task, "Shopping", true),
            ["buy_first"] = buyFirst,
            ["blacklist"] = blacklist,
            ["force_shopping_if_credit_full"] = GetBool(task, "ShoppingIgnoreBlackListWhenFull", false),
            ["only_buy_discount"] = GetBool(task, "OnlyBuyDiscount", false),
            ["reserve_max_credit"] = GetBool(task, "ReserveMaxCredit", false),
        };
    }

    private static string ResolveUiLanguage(UnifiedConfig config, string? explicitLanguage)
    {
        if (!string.IsNullOrWhiteSpace(explicitLanguage))
        {
            return explicitLanguage.Trim();
        }

        if (config.GlobalValues.TryGetValue("GUI.Localization", out var node)
            && node is JsonValue value
            && value.TryGetValue(out string? language)
            && !string.IsNullOrWhiteSpace(language))
        {
            return language.Trim();
        }

        return "zh-cn";
    }

    private static JsonObject ConvertAward(JsonObject task)
    {
        return new JsonObject
        {
            ["award"] = GetBool(task, "Award", true),
            ["mail"] = GetBool(task, "Mail", false),
            ["recruit"] = GetBool(task, "FreeGacha", false),
            ["orundum"] = GetBool(task, "Orundum", false),
            ["mining"] = GetBool(task, "Mining", false),
            ["specialaccess"] = GetBool(task, "SpecialAccess", false),
        };
    }

    private static JsonObject ConvertSingleStep(JsonObject task)
    {
        var result = new JsonObject();
        CopyFirstPresent(task, result, "type", "type", "StepType");
        CopyFirstPresent(task, result, "subtype", "subtype", "Subtype", "StepSubtype");
        CopyFirstPresent(task, result, "details", "details", "Details", "StepDetails");
        return result;
    }

    private static JsonObject ConvertRoguelike(JsonObject task, LegacyTaskSchemaConversionOptions options)
    {
        var mode = LegacyConfigValueMappings.NormalizeRoguelikeMode(task["Mode"]);
        var theme = ResolveRoguelikeTheme(task["Theme"]);
        // Frozen WPF repairs a persisted collapse-paradigm mode back to the first mode
        // whenever the stored theme is not Sami, so imported configs do the same.
        var importedCollapseParadigms = mode == 5;
        if (importedCollapseParadigms && !string.Equals(theme, "Sami", StringComparison.OrdinalIgnoreCase))
        {
            mode = 0;
        }

        // Frozen WPF likewise repairs FindPlaytime back to the first mode while keeping
        // the stored theme when the theme is not JieGarden.
        var importedFindPlaytime = mode == 20001;
        if (importedFindPlaytime && !string.Equals(theme, "JieGarden", StringComparison.OrdinalIgnoreCase))
        {
            mode = 0;
        }
        var collectibleAwardsMask = task["CollectibleStartAwards"] is null
            ? (1 << 0) | (1 << 3) | (1 << 7)
            : LegacyConfigValueMappings.NormalizeRoguelikeCollectibleAwardsMask(task["CollectibleStartAwards"]);
        var missingIterationFlagsDefault = options.MissingRoguelikeIterationFlagsDefault;

        // Frozen WPF stores every RoguelikeTask field per task unconditionally and
        // gates only at serialization, so import the raw values and leave run-form
        // gating to TaskParamCompiler.CompileRoguelike (theme/mode/investment gates).
        var result = new JsonObject
        {
            ["mode"] = mode,
            ["theme"] = theme,
            ["difficulty"] = GetInt(task, "Difficulty", int.MaxValue),
            ["starts_count"] = GetInt(task, "StartCount", 999999),
            ["investment_enabled"] = GetBool(task, "Investment", true),
            ["use_support"] = GetBool(task, "UseSupport", false),
            ["use_nonfriend_support"] = GetBool(task, "UseSupportNonFriend", false),
            ["refresh_trader_with_dice"] = GetBool(task, "RefreshTraderWithDice", false),
            ["investment_with_more_score"] = GetBool(task, "InvestWithMoreScore", false),
            ["investments_count"] = GetInt(task, "InvestCount", 999),
            ["stop_when_investment_full"] = GetBool(task, "StopWhenDepositFull", false),
            ["stop_at_final_boss"] = GetBool(task, "StopAtFinalBoss", false),
            ["stop_at_max_level"] = GetBool(task, "StopWhenLevelMax", false),
            ["collectible_mode_shopping"] = GetBool(task, "CollectibleShopping", false),
            ["collectible_mode_squad"] = GetString(task, "SquadCollectible") ?? string.Empty,
            ["start_with_elite_two"] = GetBool(task, "StartWithEliteTwo", false),
            ["only_start_with_elite_two"] = GetBool(task, "StartWithEliteTwoOnly", false),
            ["collectible_mode_start_list"] = BuildCollectibleStartList(collectibleAwardsMask),
            ["monthly_squad_auto_iterate"] = GetBool(task, "MonthlySquadAutoIterate", missingIterationFlagsDefault),
            ["monthly_squad_check_comms"] = GetBool(task, "MonthlySquadCheckComms", missingIterationFlagsDefault),
            ["deep_exploration_auto_iterate"] = GetBool(task, "DeepExplorationAutoIterate", missingIterationFlagsDefault),
            ["find_playTime_target"] = LegacyConfigValueMappings.NormalizeFindPlaytimeTarget(task["FindPlaytimeTarget"]),
        };

        var squad = GetString(task, "Squad");
        if (!string.IsNullOrWhiteSpace(squad))
        {
            result["squad"] = squad;
        }

        var roles = GetString(task, "Roles");
        if (!string.IsNullOrWhiteSpace(roles))
        {
            result["roles"] = roles;
        }

        var coreChar = GetString(task, "CoreChar");
        if (!string.IsNullOrWhiteSpace(coreChar))
        {
            result["core_char"] = coreChar;
        }

        // Frozen WPF persists each foldartal/seed toggle independently of its
        // content, so import both even when the toggle is off.
        result["first_floor_foldartal_enabled"] = GetBool(task, "SamiFirstFloorFoldartal", false);
        var firstFloorFoldartal = GetString(task, "SamiFirstFloorFoldartals");
        if (!string.IsNullOrWhiteSpace(firstFloorFoldartal))
        {
            result["first_floor_foldartal"] = firstFloorFoldartal;
        }

        result["start_foldartal_list_enabled"] = GetBool(task, "SamiNewSquad2StartingFoldartal", false);
        var foldartalList = SplitWpfSemicolonList(GetString(task, "SamiNewSquad2StartingFoldartals"), trimEntries: false, preserveEmptyEntries: false)
            .Take(3)
            .ToList();
        if (foldartalList.Count > 0)
        {
            result["start_foldartal_list"] = ToJsonArray(foldartalList);
        }

        if (importedCollapseParadigms)
        {
            // Frozen WPF omits the key entirely when the paradigm text is empty and keeps
            // the stored text in config even when the mode itself was repaired away.
            var paradigms = SplitWpfSemicolonList(GetString(task, "ExpectedCollapsalParadigms"), trimEntries: false, preserveEmptyEntries: false)
                .ToList();
            if (paradigms.Count > 0)
            {
                result["expected_collapsal_paradigms"] = ToJsonArray(paradigms);
            }
        }

        result["start_with_seed_enabled"] = GetBool(task, "StartWithSeed", false);
        var seed = GetString(task, "Seed");
        if (!string.IsNullOrWhiteSpace(seed))
        {
            result["start_with_seed"] = seed;
        }

        return result;
    }

    private static JsonObject ConvertReclamation(JsonObject task, UnifiedProfile profile, UnifiedConfig config)
    {
        var mode = LegacyConfigValueMappings.NormalizeReclamationMode(task["Mode"]);
        var theme = ResolveReclamationTheme(task["Theme"]);
        if (task["Theme"] is null && mode is 16 or 32 or 48)
        {
            theme = "RelaunchAnchor";
        }

        var toolToCraft = GetString(task, "ToolToCraft");
        if (string.IsNullOrEmpty(toolToCraft))
        {
            toolToCraft = ReclamationTaskParamsDto.ResolveDefaultToolToCraft(ResolveClientType(profile, config));
        }

        return new JsonObject
        {
            ["theme"] = theme,
            ["mode"] = mode,
            ["increment_mode"] = LegacyConfigValueMappings.NormalizeReclamationIncrementMode(task["IncrementMode"]),
            ["num_craft_batches"] = GetInt(task, "MaxCraftCountPerRound", 16),
            ["tools_to_craft"] = ToJsonArray(SplitWpfSemicolonList(toolToCraft, trimEntries: true, preserveEmptyEntries: true)),
            ["clear_store"] = GetBool(task, "ClearStore", true),
        };
    }

    private static JsonObject ConvertCustom(JsonObject task)
    {
        return new JsonObject
        {
            ["task_names"] = ToJsonArray(SplitCustomTaskNames(GetString(task, "CustomTaskName"))),
        };
    }

    private static JsonObject ConvertUserDataUpdate(JsonObject task)
    {
        return new JsonObject
        {
            ["update_oper_box"] = GetBool(task, "UpdateOperBox", true),
            ["update_depot"] = GetBool(task, "UpdateDepot", true),
            ["trigger_interval"] = NormalizeUserDataUpdateTriggerInterval(task["TriggerInterval"]),
        };
    }

    private static string NormalizeUserDataUpdateTriggerInterval(JsonNode? value)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out int number))
            {
                return number switch
                {
                    1 => UserDataUpdateTaskParamsDto.TriggerDaily,
                    2 => UserDataUpdateTaskParamsDto.TriggerWeekly,
                    _ => UserDataUpdateTaskParamsDto.TriggerEveryTime,
                };
            }

            if (jsonValue.TryGetValue(out long number64))
            {
                return number64 switch
                {
                    1 => UserDataUpdateTaskParamsDto.TriggerDaily,
                    2 => UserDataUpdateTaskParamsDto.TriggerWeekly,
                    _ => UserDataUpdateTaskParamsDto.TriggerEveryTime,
                };
            }

            if (jsonValue.TryGetValue(out string? text))
            {
                var trimmed = text?.Trim() ?? string.Empty;
                if (int.TryParse(trimmed, out var parsed))
                {
                    return NormalizeUserDataUpdateTriggerInterval(JsonValue.Create(parsed));
                }

                if (string.Equals(trimmed, UserDataUpdateTaskParamsDto.TriggerDaily, StringComparison.OrdinalIgnoreCase))
                {
                    return UserDataUpdateTaskParamsDto.TriggerDaily;
                }

                if (string.Equals(trimmed, UserDataUpdateTaskParamsDto.TriggerWeekly, StringComparison.OrdinalIgnoreCase))
                {
                    return UserDataUpdateTaskParamsDto.TriggerWeekly;
                }
            }
        }

        return UserDataUpdateTaskParamsDto.TriggerEveryTime;
    }

    private static JsonObject BuildCollectibleStartList(int mask)
    {
        return new JsonObject
        {
            ["hot_water"] = HasFlag(mask, 1 << 0),
            ["shield"] = HasFlag(mask, 1 << 1),
            ["ingot"] = HasFlag(mask, 1 << 2),
            ["hope"] = HasFlag(mask, 1 << 3),
            ["random"] = HasFlag(mask, 1 << 4),
            ["key"] = HasFlag(mask, 1 << 5),
            ["dice"] = HasFlag(mask, 1 << 6),
            ["ideas"] = HasFlag(mask, 1 << 7),
            ["ticket"] = HasFlag(mask, 1 << 8),
        };
    }

    private static bool HasFlag(int value, int flag) => (value & flag) == flag;

    private static List<string> ResolveFightStagePlan(JsonObject task)
    {
        if (task["StagePlan"] is JsonArray stagePlan)
        {
            return FightStageSelection.NormalizeStagePlan(
                stagePlan.Select(stageNode => stageNode?.GetValue<string?>()));
        }

        if (task["StagePlan"] is JsonValue stagePlanValue
            && stagePlanValue.TryGetValue(out string? stagePlanText)
            && !string.IsNullOrWhiteSpace(stagePlanText))
        {
            return FightStageSelection.NormalizeStagePlan(
                LegacyConfigValueMappings.SplitLegacyList(stagePlanText));
        }

        return [FightStageSelection.CurrentOrLast];
    }

    private static string ResolveFightStage(JsonObject task)
    {
        foreach (var stage in ResolveFightStagePlan(task))
        {
            if (!FightStageSelection.IsCurrentOrLast(stage))
            {
                return stage;
            }
        }

        return FightStageSelection.CurrentOrLast;
    }

    private static string ResolveFightStageResetMode(JsonObject task)
    {
        return LegacyConfigValueMappings.NormalizeFightStageResetMode(task["StageResetMode"]);
    }

    private static bool ResolveLegacyWeeklySchedule(JsonObject task, DayOfWeek dayOfWeek)
    {
        if (task["WeeklySchedule"] is not JsonObject weeklySchedule)
        {
            return true;
        }

        foreach (var key in ResolveLegacyWeeklyScheduleKeys(dayOfWeek))
        {
            if (weeklySchedule.TryGetPropertyValue(key, out var value) && value is not null)
            {
                if (value is JsonValue jsonValue)
                {
                    if (jsonValue.TryGetValue(out bool flag))
                    {
                        return flag;
                    }

                    if (jsonValue.TryGetValue(out int number))
                    {
                        return number != 0;
                    }

                    if (jsonValue.TryGetValue(out string? text) && bool.TryParse(text, out flag))
                    {
                        return flag;
                    }
                }
            }
        }

        return true;
    }

    private static IEnumerable<string> ResolveLegacyWeeklyScheduleKeys(DayOfWeek dayOfWeek)
    {
        yield return dayOfWeek.ToString();
        yield return ((int)dayOfWeek).ToString();
    }

    private static string ResolveRoguelikeTheme(JsonNode? themeNode)
    {
        return LegacyConfigValueMappings.NormalizeRoguelikeTheme(themeNode);
    }

    private static string ResolveReclamationTheme(JsonNode? themeNode)
    {
        return LegacyConfigValueMappings.NormalizeReclamationTheme(themeNode);
    }

    private static string ResolveClientType(UnifiedProfile profile, UnifiedConfig config)
    {
        return LegacyConfigValueNormalizer.NormalizeClientType(ResolveStringSetting(profile, config, "Start.ClientType", "ClientType")
            ?? ResolveStringSetting(profile, config, "GameSettings.ClientType")
            ?? "Official");
    }

    private static string ResolveServerType(UnifiedProfile profile, UnifiedConfig config)
    {
        return ResolveStringSetting(profile, config, "ServerType")
            ?? ResolveStringSetting(profile, config, "Server")
            ?? ResolveServerTypeFromClientType(ResolveClientType(profile, config));
    }

    private static string ResolveServerTypeFromClientType(string clientType)
    {
        return clientType.Trim() switch
        {
            var value when value.Equals("YoStarEN", StringComparison.OrdinalIgnoreCase) => "US",
            var value when value.Equals("YoStarJP", StringComparison.OrdinalIgnoreCase) => "JP",
            var value when value.Equals("YoStarKR", StringComparison.OrdinalIgnoreCase) => "KR",
            var value when value.Equals("txwy", StringComparison.OrdinalIgnoreCase) => "ZH_TW",
            _ => "CN",
        };
    }

    private static string? ResolveStringSetting(UnifiedProfile profile, UnifiedConfig config, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (profile.Values.TryGetValue(key, out var profileValue))
            {
                if (profileValue is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            if (config.GlobalValues.TryGetValue(key, out var globalValue))
            {
                if (globalValue is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            if (config.GlobalValues.TryGetValue($"GUI.{key}", out var guiGlobal))
            {
                if (guiGlobal is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static bool ResolveBooleanSetting(UnifiedProfile profile, UnifiedConfig config, bool fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (profile.Values.TryGetValue(key, out var profileValue))
            {
                return ToBoolean(profileValue, false);
            }

            if (config.GlobalValues.TryGetValue(key, out var globalValue))
            {
                return ToBoolean(globalValue, false);
            }

            if (config.GlobalValues.TryGetValue($"GUI.{key}", out var guiGlobal))
            {
                return ToBoolean(guiGlobal, false);
            }
        }

        return fallback;
    }

    private static bool ResolveBooleanSetting(UnifiedProfile profile, UnifiedConfig config, params string[] keys)
    {
        return ResolveBooleanSetting(profile, config, false, keys);
    }

    private static bool ToBoolean(JsonNode? node, bool fallback)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool b))
            {
                return b;
            }

            if (value.TryGetValue(out string? s) && bool.TryParse(s, out var parsed))
            {
                return parsed;
            }

            if (value.TryGetValue(out int i))
            {
                return i != 0;
            }
        }

        return fallback;
    }

    private static string NormalizeTypeName(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "Unknown";
        }

        var withoutAssembly = type.Split(',')[0].Trim();
        var lastDot = withoutAssembly.LastIndexOf('.');
        return lastDot >= 0 ? withoutAssembly[(lastDot + 1)..] : withoutAssembly;
    }

    private static string? GetString(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            JsonValue v when v.TryGetValue(out string? text) => text,
            JsonValue v => v.ToJsonString(),
            _ => value.ToJsonString(),
        };
    }

    private static bool GetBool(JsonObject obj, string key, bool fallback)
    {
        if (!obj.TryGetPropertyValue(key, out var value) || value is null)
        {
            return fallback;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out bool b))
            {
                return b;
            }

            if (jsonValue.TryGetValue(out string? text) && bool.TryParse(text, out var parsed))
            {
                return parsed;
            }

            if (jsonValue.TryGetValue(out int i))
            {
                return i != 0;
            }
        }

        return fallback;
    }

    private static bool? GetNullableBool(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var value))
        {
            return null;
        }

        if (value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out bool b))
            {
                return b;
            }

            if (jsonValue.TryGetValue(out string? text))
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                if (bool.TryParse(text, out var parsed))
                {
                    return parsed;
                }
            }

            if (jsonValue.TryGetValue(out int i))
            {
                return i != 0;
            }
        }

        return null;
    }

    private static JsonNode? CreateLegacyNullableBoolUiValue(JsonObject obj, string key)
    {
        return obj.ContainsKey(key)
            ? JsonValue.Create(GetNullableBool(obj, key))
            : JsonValue.Create(false);
    }

    private static int GetInt(JsonObject obj, string key, int fallback)
    {
        if (!obj.TryGetPropertyValue(key, out var value) || value is null)
        {
            return fallback;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out int number))
            {
                return number;
            }

            if (jsonValue.TryGetValue(out long number64))
            {
                return (int)Math.Clamp(number64, int.MinValue, int.MaxValue);
            }

            if (jsonValue.TryGetValue(out string? text) && int.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static IEnumerable<string> SplitNonEmpty(string? input, char separator)
    {
        _ = separator;
        return LegacyConfigValueMappings.SplitLegacyList(input);
    }

    private static IEnumerable<string> SplitWpfSemicolonList(
        string? input,
        bool trimEntries,
        bool preserveEmptyEntries)
    {
        var normalized = (input ?? string.Empty).Replace('；', ';').Trim();
        return normalized
            .Split(';')
            .Select(item => trimEntries ? item.Trim() : item)
            .Where(item => preserveEmptyEntries || !string.IsNullOrEmpty(item));
    }

    private static IEnumerable<string> SplitCustomTaskNames(string? input)
    {
        return (input ?? string.Empty)
            .Replace('，', ',')
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim());
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static void CopyIfPresent(JsonObject source, JsonObject target, string key)
    {
        if (source.TryGetPropertyValue(key, out var value) && value is not null)
        {
            target[key] = value.DeepClone();
        }
    }

    private static void CopyFirstPresent(JsonObject source, JsonObject target, string targetKey, params string[] sourceKeys)
    {
        foreach (var sourceKey in sourceKeys)
        {
            if (!source.TryGetPropertyValue(sourceKey, out var value) || value is null)
            {
                continue;
            }

            target[targetKey] = value.DeepClone();
            return;
        }
    }
}
