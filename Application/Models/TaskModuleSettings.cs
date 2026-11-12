using System.Text.Json;
using System.Text.Json.Nodes;

namespace MAAUnified.Application.Models;

public static class TaskModuleTypes
{
    public const string StartUp = "StartUp";
    public const string Fight = "Fight";
    public const string Recruit = "Recruit";
    public const string Infrast = "Infrast";
    public const string Mall = "Mall";
    public const string Award = "Award";
    public const string Roguelike = "Roguelike";
    public const string Reclamation = "Reclamation";
    public const string UserDataUpdate = "UserDataUpdate";
    public const string SingleStep = "SingleStep";
    public const string Custom = "Custom";
    public const string PostAction = "PostAction";
    public const string SwitchTheme = "SwitchTheme";
    public const string OperProgress = "OperProgress";

    public static string Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "Unknown";
        }

        var value = type.Trim();
        var lastDot = value.LastIndexOf('.');
        if (lastDot >= 0)
        {
            value = value[(lastDot + 1)..];
        }

        if (value.EndsWith("Task", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return value;
    }
}

public static class TaskModuleParameterDefaults
{
    public static JsonObject Create(string taskType, string language = "zh-cn")
    {
        var normalized = TaskModuleTypes.Normalize(taskType);
        return normalized switch
        {
            TaskModuleTypes.Infrast => CreateInfrastDefaults(),
            TaskModuleTypes.Mall => CreateMallDefaults(language),
            TaskModuleTypes.Award => CreateAwardDefaults(),
            TaskModuleTypes.UserDataUpdate => CreateUserDataUpdateDefaults(),
            TaskModuleTypes.SwitchTheme => CreateSwitchThemeDefaults(),
            TaskModuleTypes.OperProgress => CreateOperProgressDefaults(),
            _ => new JsonObject(),
        };
    }

    public static JsonObject CreateInfrastDefaults()
    {
        return new JsonObject
        {
            ["mode"] = 0,
            ["facility"] = new JsonArray("Mfg", "Trade", "Control", "Power", "Reception", "Office", "Dorm", "Processing", "Training"),
            ["_ui_facility_order"] = new JsonArray("Mfg", "Trade", "Control", "Power", "Reception", "Office", "Dorm", "Processing", "Training"),
            ["drones"] = "Money",
            ["continue_training"] = false,
            ["threshold"] = 0.3,
            ["dorm_notstationed_enabled"] = true,
            ["dorm_trust_enabled"] = true,
            ["replenish"] = true,
            ["reception_message_board"] = true,
            ["reception_clue_exchange"] = true,
            ["reception_send_clue"] = true,
            ["filename"] = string.Empty,
            ["plan_index"] = -1,
            ["fiammetta_targets"] = new JsonArray("清流", "可露希尔", "但书"),
            ["fiammetta_recovery_enabled"] = false,
            ["use_pinus_sylvestris"] = false,
            ["use_perception_information"] = false,
            ["use_worldly_plight"] = false,
            ["use_abyssal_hunter"] = false,
            ["_ui_use_weekly_schedule"] = false,
            ["_ui_weekly_schedule_sunday"] = true,
            ["_ui_weekly_schedule_monday"] = true,
            ["_ui_weekly_schedule_tuesday"] = true,
            ["_ui_weekly_schedule_wednesday"] = true,
            ["_ui_weekly_schedule_thursday"] = true,
            ["_ui_weekly_schedule_friday"] = true,
            ["_ui_weekly_schedule_saturday"] = true,
        };
    }

    public static JsonObject CreateMallDefaults(string language)
    {
        var (firstItem, blacklistItems) = (language ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "en-us" => ("Recruitment Permit", new[] { "Carbon", "Furniture Part", "Expedited Plan" }),
            "ja-jp" => ("求人票", new[] { "炭素材", "家具", "緊急招集票" }),
            "ko-kr" => ("모집 허가증", new[] { "카본", "가구 부품", "즉시 완료 허가증" }),
            "zh-tw" => ("招聘許可", new[] { "碳", "傢俱", "加急許可" }),
            _ => ("招聘许可", new[] { "碳", "家具", "加急许可" }),
        };

        var first = new JsonArray(firstItem);
        var blacklist = new JsonArray(blacklistItems.Select(item => JsonValue.Create(item)).ToArray<JsonNode?>());

        return new JsonObject
        {
            ["credit_fight"] = false,
            ["credit_fight_once_a_day"] = true,
            ["formation_index"] = 0,
            ["visit_friends"] = true,
            ["visit_friends_once_a_day"] = false,
            ["shopping"] = true,
            ["buy_first"] = first,
            ["blacklist"] = blacklist,
            ["force_shopping_if_credit_full"] = false,
            ["only_buy_discount"] = false,
            ["reserve_max_credit"] = false,
        };
    }

    public static JsonObject CreateAwardDefaults()
    {
        return new JsonObject
        {
            ["award"] = true,
            ["mail"] = false,
            ["recruit"] = false,
            ["orundum"] = false,
            ["mining"] = false,
            ["specialaccess"] = false,
            ["signinevent"] = false,
        };
    }

    public static JsonObject CreateSwitchThemeDefaults()
    {
        return new JsonObject
        {
            ["themes"] = new JsonArray(),
        };
    }

    public static JsonObject CreateOperProgressDefaults()
    {
        return new JsonObject
        {
            ["plans"] = new JsonArray(),
        };
    }

    public static JsonObject CreateUserDataUpdateDefaults()
    {
        return new JsonObject
        {
            ["update_oper_box"] = true,
            ["update_depot"] = true,
            ["trigger_interval"] = "EveryTime",
        };
    }
}

public sealed class InfrastParams
{
    public int Mode { get; set; }

    public List<string> Facility { get; set; } = [];

    public List<string> FacilityOrder { get; set; } = [];

    public string Drones { get; set; } = "Money";

    public bool ContinueTraining { get; set; }

    public double Threshold { get; set; } = 0.3;

    public bool DormNotStationedEnabled { get; set; } = true;

    public bool DormTrustEnabled { get; set; } = true;

    public bool Replenish { get; set; } = true;

    public bool ReceptionMessageBoard { get; set; } = true;

    public bool ReceptionClueExchange { get; set; } = true;

    public bool ReceptionSendClue { get; set; } = true;

    public string Filename { get; set; } = string.Empty;

    public int PlanIndex { get; set; } = -1;

    // WPF dev-v2 (c281b7aaca / 59963a90cb / 97d5e8ac38): the Fiammetta recovery
    // target list keeps the first three distinct non-blank names, and the four
    // cross-facility team switches are plain booleans.
    public List<string> FiammettaTargets { get; set; } = DefaultFiammettaTargets.ToList();

    public bool FiammettaRecoveryEnabled { get; set; }

    public bool UsePinusSylvestris { get; set; }

    public bool UsePerceptionInformation { get; set; }

    public bool UseWorldlyPlight { get; set; }

    public bool UseAbyssalHunter { get; set; }

    // WPF dev-v2 (3e570d1c23): weekly schedule gates the whole Infrast task; the
    // day flags mirror the Fight task's _ui_weekly_schedule_* storage.
    public bool UseWeeklySchedule { get; set; }

    public bool WeeklyScheduleSunday { get; set; } = true;

    public bool WeeklyScheduleMonday { get; set; } = true;

    public bool WeeklyScheduleTuesday { get; set; } = true;

    public bool WeeklyScheduleWednesday { get; set; } = true;

    public bool WeeklyScheduleThursday { get; set; } = true;

    public bool WeeklyScheduleFriday { get; set; } = true;

    public bool WeeklyScheduleSaturday { get; set; } = true;

    public static IReadOnlyList<string> DefaultFiammettaTargets { get; } = ["清流", "可露希尔", "但书"];

    public static InfrastParams FromJson(JsonObject? obj)
    {
        obj ??= TaskModuleParameterDefaults.CreateInfrastDefaults();
        var result = new InfrastParams
        {
            Mode = ReadInt(obj, "mode", 0),
            Drones = ReadString(obj, "drones", "Money"),
            ContinueTraining = ReadBool(obj, "continue_training", false),
            Threshold = ReadDouble(obj, "threshold", 0.3),
            DormNotStationedEnabled = ReadBool(obj, "dorm_notstationed_enabled", true),
            DormTrustEnabled = ReadBool(obj, "dorm_trust_enabled", true),
            Replenish = ReadBool(obj, "replenish", true),
            ReceptionMessageBoard = ReadBool(obj, "reception_message_board", true),
            ReceptionClueExchange = ReadBool(obj, "reception_clue_exchange", true),
            ReceptionSendClue = ReadBool(obj, "reception_send_clue", true),
            Filename = ReadString(obj, "filename", string.Empty),
            PlanIndex = ReadInt(obj, "plan_index", -1),
            FiammettaRecoveryEnabled = ReadBool(obj, "fiammetta_recovery_enabled", false),
            UsePinusSylvestris = ReadBool(obj, "use_pinus_sylvestris", false),
            UsePerceptionInformation = ReadBool(obj, "use_perception_information", false),
            UseWorldlyPlight = ReadBool(obj, "use_worldly_plight", false),
            UseAbyssalHunter = ReadBool(obj, "use_abyssal_hunter", false),
            UseWeeklySchedule = ReadBool(obj, "_ui_use_weekly_schedule", false),
            WeeklyScheduleSunday = ReadBool(obj, "_ui_weekly_schedule_sunday", true),
            WeeklyScheduleMonday = ReadBool(obj, "_ui_weekly_schedule_monday", true),
            WeeklyScheduleTuesday = ReadBool(obj, "_ui_weekly_schedule_tuesday", true),
            WeeklyScheduleWednesday = ReadBool(obj, "_ui_weekly_schedule_wednesday", true),
            WeeklyScheduleThursday = ReadBool(obj, "_ui_weekly_schedule_thursday", true),
            WeeklyScheduleFriday = ReadBool(obj, "_ui_weekly_schedule_friday", true),
            WeeklyScheduleSaturday = ReadBool(obj, "_ui_weekly_schedule_saturday", true),
        };

        if (TaskParamJsonReader.FindNode(obj, "fiammetta_targets") is JsonArray targetsArray)
        {
            // Replace (not append to) the WPF defaults: an explicit list wins.
            result.FiammettaTargets.Clear();
            foreach (var node in targetsArray)
            {
                var value = node?.GetValue<string?>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.FiammettaTargets.Add(value.Trim());
                }
            }

            result.FiammettaTargets = NormalizeFiammettaTargets(result.FiammettaTargets);
        }

        if (obj["facility"] is JsonArray facilityArray)
        {
            foreach (var node in facilityArray)
            {
                var value = node?.GetValue<string?>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Facility.Add(value.Trim());
                }
            }
        }
        else
        {
            result.Facility.AddRange(new[]
            {
                "Mfg", "Trade", "Control", "Power", "Reception", "Office", "Dorm", "Processing", "Training",
            });
        }

        if (obj["_ui_facility_order"] is JsonArray orderArray)
        {
            foreach (var node in orderArray)
            {
                var value = node?.GetValue<string?>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.FacilityOrder.Add(value.Trim());
                }
            }
        }

        if (result.FacilityOrder.Count == 0)
        {
            result.FacilityOrder.AddRange(result.Facility);
        }

        return result;
    }

    public JsonObject ToJson()
    {
        var facility = new JsonArray();
        foreach (var name in Facility.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            facility.Add(name.Trim());
        }

        var facilityOrder = new JsonArray();
        foreach (var name in FacilityOrder.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            facilityOrder.Add(name.Trim());
        }

        return new JsonObject
        {
            ["mode"] = Mode,
            ["facility"] = facility,
            ["_ui_facility_order"] = facilityOrder,
            ["drones"] = Drones,
            ["continue_training"] = ContinueTraining,
            ["threshold"] = Math.Clamp(Threshold, 0, 1),
            ["dorm_notstationed_enabled"] = DormNotStationedEnabled,
            ["dorm_trust_enabled"] = DormTrustEnabled,
            ["replenish"] = Replenish,
            ["reception_message_board"] = ReceptionMessageBoard,
            ["reception_clue_exchange"] = ReceptionClueExchange,
            ["reception_send_clue"] = ReceptionSendClue,
            ["filename"] = Filename,
            ["plan_index"] = PlanIndex,
            ["fiammetta_targets"] = TaskParamJsonReader.ToJsonArray(NormalizeFiammettaTargets(FiammettaTargets)),
            ["fiammetta_recovery_enabled"] = FiammettaRecoveryEnabled,
            ["use_pinus_sylvestris"] = UsePinusSylvestris,
            ["use_perception_information"] = UsePerceptionInformation,
            ["use_worldly_plight"] = UseWorldlyPlight,
            ["use_abyssal_hunter"] = UseAbyssalHunter,
            ["_ui_use_weekly_schedule"] = UseWeeklySchedule,
            ["_ui_weekly_schedule_sunday"] = WeeklyScheduleSunday,
            ["_ui_weekly_schedule_monday"] = WeeklyScheduleMonday,
            ["_ui_weekly_schedule_tuesday"] = WeeklyScheduleTuesday,
            ["_ui_weekly_schedule_wednesday"] = WeeklyScheduleWednesday,
            ["_ui_weekly_schedule_thursday"] = WeeklyScheduleThursday,
            ["_ui_weekly_schedule_friday"] = WeeklyScheduleFriday,
            ["_ui_weekly_schedule_saturday"] = WeeklyScheduleSaturday,
        };
    }

    /// <summary>
    /// WPF AsstInfrastTask serialization: trim, drop blanks, de-duplicate and keep
    /// at most three Fiammetta recovery targets.
    /// </summary>
    public static List<string> NormalizeFiammettaTargets(IEnumerable<string?> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();
    }

    private static bool ReadBool(JsonObject obj, string key, bool fallback)
        => TryGetNode(obj, key, out var node) && node is not null && TryReadBool(node, out var value) ? value : fallback;

    private static int ReadInt(JsonObject obj, string key, int fallback)
        => TryGetNode(obj, key, out var node) && node is not null && TryReadInt(node, out var value) ? value : fallback;

    private static double ReadDouble(JsonObject obj, string key, double fallback)
        => TryGetNode(obj, key, out var node) && node is not null && TryReadDouble(node, out var value) ? value : fallback;

    private static string ReadString(JsonObject obj, string key, string fallback)
        => TryGetNode(obj, key, out var node) && node is not null && TryReadString(node, out var value) ? value : fallback;

    private static bool TryGetNode(JsonObject obj, string key, out JsonNode? value)
        => obj.TryGetPropertyValue(key, out value) && value is not null;

    private static bool TryReadBool(JsonNode node, out bool value)
    {
        value = false;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out bool b))
        {
            value = b;
            return true;
        }

        if (jsonValue.TryGetValue(out string? s) && bool.TryParse(s, out var parsed))
        {
            value = parsed;
            return true;
        }

        if (jsonValue.TryGetValue(out int i))
        {
            value = i != 0;
            return true;
        }

        return false;
    }

    private static bool TryReadInt(JsonNode node, out int value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out int i))
        {
            value = i;
            return true;
        }

        if (jsonValue.TryGetValue(out string? s) && int.TryParse(s, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryReadDouble(JsonNode node, out double value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out double d))
        {
            value = d;
            return true;
        }

        if (jsonValue.TryGetValue(out int i))
        {
            value = i;
            return true;
        }

        if (jsonValue.TryGetValue(out string? s) && double.TryParse(s, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryReadString(JsonNode node, out string value)
    {
        value = string.Empty;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out string? s))
        {
            value = s ?? string.Empty;
            return true;
        }

        try
        {
            value = node.ToJsonString();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class MallParams
{
    private const string UiMallCreditFightLastTime = "_ui_mall_credit_fight_last_time";
    private const string UiMallVisitFriendsLastTime = "_ui_mall_visit_friends_last_time";

    public bool CreditFight { get; set; }

    public bool CreditFightOnceADay { get; set; } = true;

    public int FormationIndex { get; set; }

    public bool VisitFriends { get; set; } = true;

    public bool VisitFriendsOnceADay { get; set; }

    public bool Shopping { get; set; } = true;

    public List<string> BuyFirst { get; set; } = [];

    public List<string> Blacklist { get; set; } = [];

    public bool ForceShoppingIfCreditFull { get; set; }

    public bool OnlyBuyDiscount { get; set; }

    public bool ReserveMaxCredit { get; set; }

    public string CreditFightLastTime { get; set; } = string.Empty;

    public string VisitFriendsLastTime { get; set; } = string.Empty;

    public static MallParams FromJson(JsonObject? obj)
    {
        obj ??= TaskModuleParameterDefaults.CreateMallDefaults("zh-cn");
        var result = new MallParams
        {
            CreditFight = ReadBool(obj, "credit_fight", false),
            CreditFightOnceADay = ReadBool(obj, "credit_fight_once_a_day", true),
            FormationIndex = ReadInt(obj, "formation_index", 0),
            VisitFriends = ReadBool(obj, "visit_friends", true),
            VisitFriendsOnceADay = ReadBool(obj, "visit_friends_once_a_day", false),
            Shopping = ReadBool(obj, "shopping", true),
            ForceShoppingIfCreditFull = ReadBool(obj, "force_shopping_if_credit_full", false),
            OnlyBuyDiscount = ReadBool(obj, "only_buy_discount", false),
            ReserveMaxCredit = ReadBool(obj, "reserve_max_credit", false),
            CreditFightLastTime = ReadString(obj, UiMallCreditFightLastTime, string.Empty),
            VisitFriendsLastTime = ReadString(obj, UiMallVisitFriendsLastTime, string.Empty),
        };

        result.BuyFirst.AddRange(ReadStringList(obj, "buy_first"));
        result.Blacklist.AddRange(ReadStringList(obj, "blacklist"));
        return result;
    }

    public JsonObject ToJson()
    {
        var result = new JsonObject
        {
            ["credit_fight"] = CreditFight,
            ["credit_fight_once_a_day"] = CreditFightOnceADay,
            ["formation_index"] = Math.Clamp(FormationIndex, 0, 4),
            ["visit_friends"] = VisitFriends,
            ["visit_friends_once_a_day"] = VisitFriendsOnceADay,
            ["shopping"] = Shopping,
            ["buy_first"] = ToJsonArray(BuyFirst),
            ["blacklist"] = ToJsonArray(Blacklist),
            ["force_shopping_if_credit_full"] = ForceShoppingIfCreditFull,
            ["only_buy_discount"] = OnlyBuyDiscount,
            ["reserve_max_credit"] = ReserveMaxCredit,
        };

        if (!string.IsNullOrWhiteSpace(CreditFightLastTime))
        {
            result[UiMallCreditFightLastTime] = CreditFightLastTime.Trim();
        }

        if (!string.IsNullOrWhiteSpace(VisitFriendsLastTime))
        {
            result[UiMallVisitFriendsLastTime] = VisitFriendsLastTime.Trim();
        }

        return result;
    }

    private static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return fallback;
        }

        return value.TryGetValue(out bool boolean) ? boolean
            : value.TryGetValue(out int number) ? number != 0
            : value.TryGetValue(out string? text) && bool.TryParse(text, out var parsed) ? parsed
            : fallback;
    }

    private static int ReadInt(JsonObject obj, string key, int fallback)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue(out int parsedInt))
        {
            return parsedInt;
        }

        if (value.TryGetValue(out string? parsedText) && int.TryParse(parsedText, out parsedInt))
        {
            return parsedInt;
        }

        return fallback;
    }

    private static string ReadString(JsonObject obj, string key, string fallback)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue(out string? parsedText) && !string.IsNullOrWhiteSpace(parsedText))
        {
            return parsedText.Trim();
        }

        return fallback;
    }

    private static List<string> ReadStringList(JsonObject obj, string key)
    {
        var result = new List<string>();
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonArray array)
        {
            return result;
        }

        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            {
                result.Add(text.Trim());
            }
        }

        return result;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            result.Add(value.Trim());
        }

        return result;
    }
}

public sealed class AwardParams
{
    public bool Award { get; set; } = true;

    public bool Mail { get; set; }

    public bool Recruit { get; set; }

    public bool Orundum { get; set; }

    public bool Mining { get; set; }

    public bool SpecialAccess { get; set; }

    public bool SignInEvent { get; set; }

    public static AwardParams FromJson(JsonObject? obj)
    {
        obj ??= TaskModuleParameterDefaults.CreateAwardDefaults();
        return new AwardParams
        {
            Award = ReadBool(obj, "award", true),
            Mail = ReadBool(obj, "mail", false),
            Recruit = ReadBool(obj, "recruit", false),
            Orundum = ReadBool(obj, "orundum", false),
            Mining = ReadBool(obj, "mining", false),
            SpecialAccess = ReadBool(obj, "specialaccess", false),
            SignInEvent = ReadBool(obj, "signinevent", false),
        };
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["award"] = Award,
            ["mail"] = Mail,
            ["recruit"] = Recruit,
            ["orundum"] = Orundum,
            ["mining"] = Mining,
            ["specialaccess"] = SpecialAccess,
            ["signinevent"] = SignInEvent,
        };
    }

    private static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return fallback;
        }

        return value.TryGetValue(out bool boolean) ? boolean
            : value.TryGetValue(out int number) ? number != 0
            : value.TryGetValue(out string? text) && bool.TryParse(text, out var parsed) ? parsed
            : fallback;
    }
}

// WPF dev-v2 (9c86aae937): AsstSwitchThemeTask serializes the candidate theme
// name list; Core picks one at random per run and skips the task when the list
// is empty.
public sealed class SwitchThemeParams
{
    public List<string> Themes { get; set; } = [];

    public static SwitchThemeParams FromJson(JsonObject? obj)
    {
        obj ??= TaskModuleParameterDefaults.CreateSwitchThemeDefaults();
        var result = new SwitchThemeParams();
        result.Themes.AddRange(TaskParamJsonReader.ReadStringList(obj, "themes"));
        return result;
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["themes"] = TaskParamJsonReader.ToJsonArray(TaskParamJsonReader.NormalizeNames(Themes)),
        };
    }
}

/// <summary>
/// Core <c>battle::Role</c> names usable as an OperProgress plan target, in the
/// in-game class order. <c>Drone</c>/<c>Trap</c> are excluded because Core
/// rejects non-operator progression targets.
/// </summary>
public static class OperProgressRoles
{
    public const string Unknown = "Unknown";

    public static IReadOnlyList<string> Selectable { get; } =
    [
        "Pioneer",
        "Warrior",
        "Tank",
        "Sniper",
        "Caster",
        "Medic",
        "Support",
        "Special",
    ];

    public static string Normalize(string? role)
    {
        var trimmed = role?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Unknown;
        }

        if (string.Equals(trimmed, Unknown, StringComparison.OrdinalIgnoreCase))
        {
            return Unknown;
        }

        foreach (var candidate in Selectable)
        {
            if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return Unknown;
    }
}

/// <summary>
/// WPF dev-v2 (a412e6a294): one ordered operator development plan. Zero targets
/// are omitted from the serialized payload because Core treats a present value
/// as a hard requirement.
/// </summary>
public sealed class OperProgressPlan
{
    public string Role { get; set; } = OperProgressRoles.Unknown;

    public string Name { get; set; } = string.Empty;

    public int Elite { get; set; }

    public int SkillLevel { get; set; }

    public int[] SkillMastery { get; set; } = new int[3];

    public static OperProgressPlan FromJson(JsonObject plan)
    {
        var result = new OperProgressPlan
        {
            Role = OperProgressRoles.Normalize(TaskParamJsonReader.ReadString(plan, "role")),
            Name = TaskParamJsonReader.ReadString(plan, "name").Trim(),
            Elite = TaskParamJsonReader.ReadInt(plan, "elite"),
            SkillLevel = TaskParamJsonReader.ReadInt(plan, "skill_level"),
        };

        if (TaskParamJsonReader.FindNode(plan, "skill_mastery") is JsonArray masteryArray)
        {
            for (var index = 0; index < result.SkillMastery.Length && index < masteryArray.Count; index++)
            {
                if (masteryArray[index] is JsonValue value && value.TryGetValue(out int level))
                {
                    result.SkillMastery[index] = level;
                }
            }
        }

        return result;
    }

    public JsonObject ToJson()
    {
        var plan = new JsonObject
        {
            ["role"] = OperProgressRoles.Normalize(Role),
            ["name"] = Name.Trim(),
        };

        if (Elite is >= 1 and <= 2)
        {
            plan["elite"] = Elite;
        }

        if (SkillLevel is >= 2 and <= 7)
        {
            plan["skill_level"] = SkillLevel;
        }

        var mastery = SkillMastery.Select(level => Math.Clamp(level, 0, 3)).ToArray();
        if (mastery.Any(level => level > 0))
        {
            plan["skill_mastery"] = TaskParamJsonReader.ToJsonIntArray(mastery);
        }

        return plan;
    }

    public OperProgressPlan Clone()
    {
        return new OperProgressPlan
        {
            Role = Role,
            Name = Name,
            Elite = Elite,
            SkillLevel = SkillLevel,
            SkillMastery = [.. SkillMastery],
        };
    }
}

public sealed class OperProgressParams
{
    public List<OperProgressPlan> Plans { get; set; } = [];

    public static OperProgressParams FromJson(JsonObject? obj)
    {
        obj ??= TaskModuleParameterDefaults.CreateOperProgressDefaults();
        var result = new OperProgressParams();
        if (TaskParamJsonReader.FindNode(obj, "plans") is not JsonArray plans)
        {
            return result;
        }

        foreach (var node in plans)
        {
            if (node is JsonObject plan)
            {
                result.Plans.Add(OperProgressPlan.FromJson(plan));
            }
        }

        return result;
    }

    public JsonObject ToJson()
    {
        var plans = new JsonArray();
        foreach (var plan in Plans)
        {
            plans.Add(plan.ToJson());
        }

        return new JsonObject
        {
            ["plans"] = plans,
        };
    }
}

public sealed class PostActionConfig
{
    public bool Once { get; set; }

    public bool ExitArknights { get; set; }

    public bool BackToAndroidHome { get; set; }

    public bool ExitEmulator { get; set; }

    public bool ExitSelf { get; set; }

    public bool IfNoOtherMaa { get; set; }

    public bool Hibernate { get; set; }

    public bool Shutdown { get; set; }

    public bool Sleep { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether every post action is skipped when the run had a
    /// failed main-queue task. WPF cda1c3b07f (<c>PostActions.SkipOnError = 1 &lt;&lt; 8</c>).
    /// </summary>
    public bool SkipOnError { get; set; }
    /// Gets or sets a value indicating whether MAA locks the workstation after completion.
    /// WPF parity: <c>PostActions.LockScreen = 1 &lt;&lt; 9</c> (d58cb0825b). Windows only; the stored
    /// value is preserved on other platforms and normalized away at runtime.
    /// </summary>
    public bool LockScreen { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether MAA closes the PC client after completion.
    /// WPF parity: <c>PostActions.ExitPCClient = 1 &lt;&lt; 10</c> (3b4f06dd86). Only meaningful for the
    /// PC (window binding) connection; the raw value survives connection switches.
    /// </summary>
    public bool ExitPCClient { get; set; }

    public PostActionCommandConfig Commands { get; set; } = PostActionCommandConfig.Default;

    public static PostActionConfig Default => new();

    public static PostActionConfig FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return Default;
        }

        return new PostActionConfig
        {
            Once = ReadBool(obj, false, "once", "Once"),
            ExitArknights = ReadBool(obj, false, "exit_arknights", "ExitArknights"),
            BackToAndroidHome = ReadBool(obj, false, "back_to_android_home", "BackToAndroidHome"),
            ExitEmulator = ReadBool(obj, false, "exit_emulator", "ExitEmulator"),
            ExitSelf = ReadBool(obj, false, "exit_self", "ExitSelf"),
            IfNoOtherMaa = ReadBool(obj, false, "if_no_other_maa", "IfNoOtherMaa"),
            Hibernate = ReadBool(obj, false, "hibernate", "Hibernate"),
            Shutdown = ReadBool(obj, false, "shutdown", "Shutdown"),
            Sleep = ReadBool(obj, false, "sleep", "Sleep"),
            SkipOnError = ReadBool(obj, false, "skip_on_error", "SkipOnError"),
            LockScreen = ReadBool(obj, false, "lock_screen", "LockScreen"),
            ExitPCClient = ReadBool(obj, false, "exit_pc_client", "ExitPCClient"),
            Commands = PostActionCommandConfig.FromJson(FindNode(obj, "commands", "Commands")),
        };
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["once"] = Once,
            ["exit_arknights"] = ExitArknights,
            ["back_to_android_home"] = BackToAndroidHome,
            ["exit_emulator"] = ExitEmulator,
            ["exit_self"] = ExitSelf,
            ["if_no_other_maa"] = IfNoOtherMaa,
            ["hibernate"] = Hibernate,
            ["shutdown"] = Shutdown,
            ["sleep"] = Sleep,
            ["skip_on_error"] = SkipOnError,
            ["lock_screen"] = LockScreen,
            ["exit_pc_client"] = ExitPCClient,
            ["commands"] = Commands.ToJson(),
        };
    }

    public bool HasAnyAction()
    {
        return ExitArknights || BackToAndroidHome || ExitEmulator || ExitSelf
            || LockScreen || ExitPCClient
            || Hibernate || Shutdown || Sleep;
    }

    /// <summary>
    /// Gets a value indicating whether the post actions must be skipped for a run with failed
    /// tasks. WPF cda1c3b07f gates the skip on both the checkbox and the run outcome.
    /// </summary>
    /// <param name="hasTaskErrors">True when the run recorded at least one failed task.</param>
    /// <returns>True when the post actions must be skipped.</returns>
    public bool ShouldSkipOnError(bool hasTaskErrors) => SkipOnError && hasTaskErrors;

    public PostActionConfig Clone()
    {
        return new PostActionConfig
        {
            Once = Once,
            ExitArknights = ExitArknights,
            BackToAndroidHome = BackToAndroidHome,
            ExitEmulator = ExitEmulator,
            ExitSelf = ExitSelf,
            IfNoOtherMaa = IfNoOtherMaa,
            Hibernate = Hibernate,
            Shutdown = Shutdown,
            Sleep = Sleep,
            SkipOnError = SkipOnError,
            LockScreen = LockScreen,
            ExitPCClient = ExitPCClient,
            Commands = Commands.Clone(),
        };
    }

    private static bool ReadBool(JsonObject obj, bool fallback, params string[] keys)
        => FindNode(obj, keys) is JsonValue v && (
            v.TryGetValue(out bool b) ? b :
            v.TryGetValue(out int i) ? i != 0 :
            v.TryGetValue(out string? s) && bool.TryParse(s, out var parsed) ? parsed : fallback);

    private static JsonNode? FindNode(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node))
            {
                return node;
            }
        }

        foreach (var (existingKey, value) in obj)
        {
            if (string.IsNullOrWhiteSpace(existingKey))
            {
                continue;
            }

            foreach (var key in keys)
            {
                if (string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }

        return null;
    }
}

public sealed class PostActionCommandConfig
{
    public string ExitArknights { get; set; } = string.Empty;

    public string BackToAndroidHome { get; set; } = string.Empty;

    public string ExitEmulator { get; set; } = string.Empty;

    public string ExitSelf { get; set; } = string.Empty;

    public static PostActionCommandConfig Default => new();

    public static PostActionCommandConfig FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return Default;
        }

        return new PostActionCommandConfig
        {
            ExitArknights = ReadString(obj, "exit_arknights", "ExitArknights"),
            BackToAndroidHome = ReadString(obj, "back_to_android_home", "BackToAndroidHome"),
            ExitEmulator = ReadString(obj, "exit_emulator", "ExitEmulator"),
            ExitSelf = ReadString(obj, "exit_self", "ExitSelf"),
        };
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["exit_arknights"] = ExitArknights,
            ["back_to_android_home"] = BackToAndroidHome,
            ["exit_emulator"] = ExitEmulator,
            ["exit_self"] = ExitSelf,
        };
    }

    public PostActionCommandConfig Clone()
    {
        return new PostActionCommandConfig
        {
            ExitArknights = ExitArknights,
            BackToAndroidHome = BackToAndroidHome,
            ExitEmulator = ExitEmulator,
            ExitSelf = ExitSelf,
        };
    }

    private static string ReadString(JsonObject obj, params string[] keys)
    {
        if (FindNode(obj, keys) is not JsonValue value)
        {
            return string.Empty;
        }

        return value.TryGetValue(out string? text)
            ? text ?? string.Empty
            : string.Empty;
    }

    private static JsonNode? FindNode(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node))
            {
                return node;
            }
        }

        foreach (var (existingKey, value) in obj)
        {
            if (string.IsNullOrWhiteSpace(existingKey))
            {
                continue;
            }

            foreach (var key in keys)
            {
                if (string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }

        return null;
    }
}

public sealed record PostActionPreview(
    bool HasBlockingError,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> UnsupportedActions);

public sealed record PostActionExecutionContext(
    string TriggerMessage,
    bool WasSuccessfulTaskChain,
    string RunId = "-",
    int? TaskIndex = null);

public sealed record PostActionCapabilitySnapshot(
    bool ExitArknightsSupported,
    bool BackToAndroidHomeSupported,
    bool ExitEmulatorSupported,
    bool ExitSelfSupported,
    bool HibernateSupported,
    bool ShutdownSupported,
    bool SleepSupported);

public sealed record PostActionExecutionPlan(
    IReadOnlyList<string> PlannedActions,
    IReadOnlyList<string> SkippedActions,
    bool SkippedSystemActionsForOtherMaa);

public sealed record TaskRuntimeStatusSnapshot(
    string RunId,
    int? TaskIndex,
    string Module,
    string Action,
    string Status,
    string Message,
    DateTimeOffset Timestamp);

internal static class TaskParamJsonReader
{
    public static JsonNode? FindNode(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var node) && node is not null)
        {
            return node;
        }

        foreach (var (existingKey, value) in obj)
        {
            if (value is not null && string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    public static string ReadString(JsonObject obj, string key)
    {
        return FindNode(obj, key) is JsonValue value && value.TryGetValue(out string? text)
            ? text ?? string.Empty
            : string.Empty;
    }

    public static int ReadInt(JsonObject obj, string key)
    {
        return FindNode(obj, key) is JsonValue value
            ? value.TryGetValue(out int number) ? number
                : value.TryGetValue(out string? text) && int.TryParse(text, out var parsed) ? parsed
                : 0
            : 0;
    }

    public static bool ReadBool(JsonObject obj, string key)
    {
        return ReadBool(obj, key, false);
    }

    public static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        return FindNode(obj, key) is JsonValue value
            ? value.TryGetValue(out bool flag) ? flag
                : value.TryGetValue(out int number) ? number != 0
                : value.TryGetValue(out string? text) && bool.TryParse(text, out var parsed) ? parsed
                : fallback
            : fallback;
    }

    public static List<string> ReadStringList(JsonObject obj, string key)
    {
        var result = new List<string>();
        if (FindNode(obj, key) is not JsonArray array)
        {
            return result;
        }

        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            {
                result.Add(text.Trim());
            }
        }

        return result;
    }

    public static List<string> NormalizeNames(IEnumerable<string?> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Where(value => value.Length > 0)
            .ToList();
    }

    public static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    public static JsonArray ToJsonIntArray(IEnumerable<int> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
