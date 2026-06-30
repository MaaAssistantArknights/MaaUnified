using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;

namespace MAAUnified.Application.Configuration;

public sealed class GuiJsonConfigImporter : IConfigImporter
{
    private const string FileName = "gui.json";

    public string Name => "gui.json";

    public bool CanImport(LegacyConfigSnapshot snapshot) => snapshot.GuiExists;

    public async Task ImportAsync(
        LegacyConfigSnapshot snapshot,
        UnifiedConfig target,
        ImportReport report,
        bool fillMissingOnly,
        CancellationToken cancellationToken = default)
    {
        if (!snapshot.GuiExists)
        {
            AppendUnique(report.MissingFiles, FileName);
            report.DefaultFallbackCount += 1;
            report.Warnings.Add("gui.json not found, skipped");
            return;
        }

        try
        {
            await using var stream = File.OpenRead(snapshot.GuiPath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            if (root.TryGetProperty("Current", out var currentProp) && currentProp.ValueKind == JsonValueKind.String)
            {
                if (fillMissingOnly)
                {
                    if (string.Equals(target.CurrentProfile, "Default", StringComparison.OrdinalIgnoreCase))
                    {
                        target.CurrentProfile = currentProp.GetString() ?? target.CurrentProfile;
                        report.MappedFieldCount += 1;
                    }
                }
                else
                {
                    target.CurrentProfile = currentProp.GetString() ?? target.CurrentProfile;
                    report.MappedFieldCount += 1;
                }
            }

            if (root.TryGetProperty("Configurations", out var configsProp) && configsProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var configProp in configsProp.EnumerateObject())
                {
                    if (!target.Profiles.TryGetValue(configProp.Name, out var profile))
                    {
                        profile = new UnifiedProfile();
                        target.Profiles[configProp.Name] = profile;
                    }

                    if (configProp.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (var valueProp in configProp.Value.EnumerateObject())
                    {
                        var normalizedKey = NormalizeProfileKey(valueProp.Name);
                        JsonImportMergeHelper.MergeProfileValue(
                            profile,
                            configProp.Name,
                            normalizedKey,
                            JsonImportMergeHelper.ToJsonNode(valueProp.Value),
                            fillMissingOnly,
                            report);
                    }

                    MigrateFlatTaskQueue(configProp.Name, configProp.Value, profile, target, fillMissingOnly, report);
                }
            }

            if (root.TryGetProperty("Global", out var globalProp) && globalProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var valueProp in globalProp.EnumerateObject())
                {
                    JsonImportMergeHelper.MergeGlobalValue(
                        target,
                        valueProp.Name,
                        JsonImportMergeHelper.ToJsonNode(valueProp.Value),
                        fillMissingOnly,
                        report);
                }
            }

            report.ImportedGui = true;
            AppendUnique(report.ImportedFiles, FileName);
        }
        catch (Exception ex)
        {
            AppendUnique(report.DamagedFiles, FileName);
            report.Errors.Add($"Failed to import gui.json: {ex.Message}");
        }
    }

    private static string NormalizeProfileKey(string key)
    {
        return LegacyConfigValueMappings.NormalizeProfileKey(key);
    }

    private static void MigrateFlatTaskQueue(
        string profileName,
        JsonElement source,
        UnifiedProfile profile,
        UnifiedConfig config,
        bool fillMissingOnly,
        ImportReport report)
    {
        if (source.ValueKind != JsonValueKind.Object || !HasFlatTaskConfiguration(source))
        {
            return;
        }

        if (profile.TaskQueue.Count > 0)
        {
            if (fillMissingOnly)
            {
                report.ConflictCount += 1;
            }

            return;
        }

        foreach (var legacyTask in BuildFlatLegacyTasks(source))
        {
            if (!LegacyTaskSchemaConverter.TryConvertLegacyTask(legacyTask, profile, config, out var convertedTask, out var error)
                && !string.IsNullOrWhiteSpace(error))
            {
                report.Errors.Add(error);
            }

            profile.TaskQueue.Add(convertedTask);
            report.MappedFieldCount += 1;
        }

        if (profile.TaskQueue.Count > 0)
        {
            report.Warnings.Add($"Migrated legacy flat task settings in gui.json profile `{profileName}` to TaskQueue.");
        }
    }

    private static bool HasFlatTaskConfiguration(JsonElement source)
    {
        return source.EnumerateObject().Any(static prop =>
            prop.Name.StartsWith("TaskQueue.", StringComparison.OrdinalIgnoreCase)
            || prop.Name.StartsWith("MainFunction.", StringComparison.OrdinalIgnoreCase)
            || prop.Name.StartsWith("AutoRecruit.", StringComparison.OrdinalIgnoreCase)
            || prop.Name.StartsWith("Infrast.", StringComparison.OrdinalIgnoreCase)
            || prop.Name.StartsWith("Mall.", StringComparison.OrdinalIgnoreCase)
            || prop.Name.StartsWith("Visit.", StringComparison.OrdinalIgnoreCase)
            || prop.Name.StartsWith("Mission.", StringComparison.OrdinalIgnoreCase)
            || prop.Name.StartsWith("Roguelike.", StringComparison.OrdinalIgnoreCase)
            || prop.Name.StartsWith("Reclamation.", StringComparison.OrdinalIgnoreCase)
            || prop.Name.StartsWith("Fight.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(prop.Name, "Start.AccountName", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<JsonObject> BuildFlatLegacyTasks(JsonElement source)
    {
        var entries = new List<FlatTaskEntry>
        {
            new("WakeUp", 0, BuildStartUpTask(source)),
            new("Recruiting", 1, BuildRecruitTask(source)),
            new("Base", 2, BuildInfrastTask(source)),
            new("Combat", 3, BuildFightTask(source)),
            new("Mall", 4, BuildMallTask(source)),
            new("Mission", 5, BuildAwardTask(source)),
            new("AutoRoguelike", 6, BuildRoguelikeTask(source)),
            new("Reclamation", 7, BuildReclamationTask(source)),
        };

        foreach (var entry in entries)
        {
            entry.Task["IsEnable"] = ReadBool(source, $"TaskQueue.{entry.OldName}.IsChecked", false);
            entry.Order = ReadInt(source, $"TaskQueue.Order.{entry.OldName}", entry.Order);
        }

        var result = entries
            .OrderBy(static entry => entry.Order)
            .Select(static entry => entry.Task)
            .ToList();

        if (ReadBool(source, "Fight.UseRemainingSanityStage", true)
            && TryReadString(source, "Fight.RemainingSanityStage", out var remainingStage)
            && !string.IsNullOrWhiteSpace(remainingStage))
        {
            var remaining = NewLegacyTask("FightTask", "RemainingSanityStage", ReadBool(source, "TaskQueue.Combat.IsChecked", false));
            remaining["StagePlan"] = new JsonArray(remainingStage.Trim());
            var combatIndex = result.FindIndex(static task => string.Equals(ReadTaskType(task), "FightTask", StringComparison.OrdinalIgnoreCase));
            result.Insert(combatIndex < 0 ? result.Count : combatIndex + 1, remaining);
        }

        return result;
    }

    private static JsonObject BuildStartUpTask(JsonElement source)
    {
        var task = NewLegacyTask("StartUpTask", "StartUp", false);
        AddIfPresent(source, "Start.AccountName", task, "AccountName");
        return task;
    }

    private static JsonObject BuildFightTask(JsonElement source)
    {
        var task = NewLegacyTask("FightTask", "Fight", false);
        AddIfPresent(source, "MainFunction.UseMedicine", task, "UseMedicine");
        AddIfPresent(source, "MainFunction.UseMedicine.Quantity", task, "MedicineCount");
        AddIfPresent(source, "MainFunction.UseStone", task, "UseStone");
        AddIfPresent(source, "MainFunction.UseStone.Quantity", task, "StoneCount");
        AddIfPresent(source, "MainFunction.TimesLimited", task, "EnableTimesLimit");
        AddIfPresent(source, "MainFunction.TimesLimited.Quantity", task, "TimesLimit");
        AddIfPresent(source, "MainFunction.Series.Quantity", task, "Series");
        AddIfPresent(source, "MainFunction.Drops.Enable", task, "EnableTargetDrop");
        AddIfPresent(source, "MainFunction.Drops.ItemId", task, "DropId");
        AddIfPresent(source, "MainFunction.Drops.Quantity", task, "DropCount");
        AddIfPresent(source, "Penguin.IsDrGrandet", task, "IsDrGrandet");
        AddIfPresent(source, "GUI.AllowUseStoneSave", task, "UseStoneAllowSave");
        AddIfPresent(source, "GUI.HideSeries", task, "HideSeries");
        AddIfPresent(source, "Fight.UseExpiringMedicine", task, "UseExpiringMedicine");
        AddIfPresent(source, "MainFunction.Annihilation.Stage", task, "AnnihilationStage");
        AddIfPresent(source, "MainFunction.Annihilation.UseCustom", task, "UseCustomAnnihilation");
        AddIfPresent(source, "GUI.HideUnavailableStage", task, "HideUnavailableStage");
        AddIfPresent(source, "GUI.CustomStageCode", task, "IsStageManually");
        AddIfPresent(source, "GUI.UseAlternateStage", task, "UseOptionalStage");

        var stagePlan = new JsonArray(ReadString(source, "MainFunction.Stage1", string.Empty));
        if (ReadBool(source, "GUI.UseAlternateStage", false))
        {
            stagePlan.Add(ReadString(source, "MainFunction.Stage2", string.Empty));
            stagePlan.Add(ReadString(source, "MainFunction.Stage3", string.Empty));
            stagePlan.Add(ReadString(source, "MainFunction.Stage4", string.Empty));
        }

        task["StagePlan"] = stagePlan;
        task["StageResetMode"] = ReadBool(source, "GUI.HideUnavailableStage", true) ? "Current" : "Ignore";
        return task;
    }

    private static JsonObject BuildRecruitTask(JsonElement source)
    {
        var task = NewLegacyTask("RecruitTask", "Recruit", false);
        AddIfPresent(source, "AutoRecruit.SelectExtraTags", task, "ExtraTagMode");
        AddIfPresent(source, "AutoRecruit.RefreshLevel3", task, "RefreshLevel3");
        AddIfPresent(source, "AutoRecruit.ForceRefresh", task, "ForceRefresh");
        AddIfPresent(source, "AutoRecruit.NotChooseLevel1", task, "Level1NotChoose");
        AddIfPresent(source, "AutoRecruit.MaxTimes", task, "MaxTimes");
        AddIfPresent(source, "AutoRecruit.ChooseLevel3", task, "Level3Choose");
        AddIfPresent(source, "AutoRecruit.ChooseLevel3.Time", task, "Level3Time");
        AddIfPresent(source, "AutoRecruit.ChooseLevel4", task, "Level4Choose");
        AddIfPresent(source, "AutoRecruit.ChooseLevel4.Time", task, "Level4Time");
        AddIfPresent(source, "AutoRecruit.ChooseLevel5", task, "Level5Choose");
        AddIfPresent(source, "AutoRecruit.ChooseLevel5.Time", task, "Level5Time");

        if (TryReadString(source, "AutoRecruit.AutoRecruitFirstList", out var firstTags))
        {
            var tags = new JsonArray();
            foreach (var tag in LegacyConfigValueMappings.SplitLegacyList(firstTags))
            {
                tags.Add(tag);
            }

            task["Level3PreferTags"] = tags;
        }

        return task;
    }

    private static JsonObject BuildInfrastTask(JsonElement source)
    {
        var task = NewLegacyTask("InfrastTask", "Infrast", false);
        AddIfPresent(source, "Infrast.InfrastMode", task, "Mode");
        AddIfPresent(source, "Infrast.UsesOfDrones", task, "UsesOfDrones");
        AddIfPresent(source, "Infrast.ReceptionMessageBoardReceive", task, "ReceptionMessageBoard");
        AddIfPresent(source, "Infrast.ReceptionClueExchange", task, "ReceptionClueExchange");
        AddIfPresent(source, "Infrast.ReceptionSendClue", task, "SendClue");
        AddIfPresent(source, "Infrast.ContinueTraining", task, "ContinueTraining");
        AddIfPresent(source, "Infrast.DormThreshold", task, "DormThreshold");
        AddIfPresent(source, "Infrast.DormFilterNotStationedEnabled", task, "DormFilterNotStationed");
        AddIfPresent(source, "Infrast.DormTrustEnabled", task, "DormTrustEnabled");
        AddIfPresent(source, "Infrast.OriginiumShardAutoReplenishment", task, "OriginiumShardAutoReplenishment");
        AddIfPresent(source, "Infrast.CustomInfrastFile", task, "Filename");
        AddIfPresent(source, "Infrast.CustomInfrastPlanSelect", task, "PlanSelect");

        var roomList = new JsonArray();
        foreach (var room in new[] { "Mfg", "Trade", "Control", "Power", "Reception", "Office", "Dorm", "Processing", "Training" })
        {
            roomList.Add(new JsonObject
            {
                ["Room"] = room,
                ["IsEnabled"] = ReadBool(source, $"Infrast.{room}.IsChecked", true),
                ["Order"] = ReadInt(source, $"Infrast.Order.{room}", roomList.Count),
            });
        }

        task["RoomList"] = new JsonArray(roomList
            .OfType<JsonObject>()
            .OrderBy(static room => ReadJsonInt(room["Order"], 0))
            .Select(static room => new JsonObject
            {
                ["Room"] = room["Room"]?.GetValue<string>() ?? string.Empty,
                ["IsEnabled"] = room["IsEnabled"]?.GetValue<bool>() ?? true,
            })
            .ToArray<JsonNode?>());
        return task;
    }

    private static JsonObject BuildMallTask(JsonElement source)
    {
        var task = NewLegacyTask("MallTask", "Mall", false);
        AddIfPresent(source, "Mall.CreditShopping", task, "Shopping");
        AddIfPresent(source, "Mall.CreditFirstListNew", task, "FirstList");
        AddIfPresent(source, "Mall.CreditBlackListNew", task, "BlackList");
        AddIfPresent(source, "Mall.CreditForceShoppingIfCreditFull", task, "ShoppingIgnoreBlackListWhenFull");
        AddIfPresent(source, "Mall.CreditOnlyBuyDiscount", task, "OnlyBuyDiscount");
        AddIfPresent(source, "Mall.CreidtReserveMaxCredit", task, "ReserveMaxCredit");
        AddIfPresent(source, "Visit.CreditFightTaskEnabled", task, "CreditFight");
        AddIfPresent(source, "Visit.CreditFightOnceADay", task, "CreditFightOnceADay");
        AddIfPresent(source, "Visit.LastCreditFightTaskTime", task, "CreditFightLastTime");
        AddIfPresent(source, "Visit.CreditFightSelectFormation", task, "CreditFightFormation");
        AddIfPresent(source, "Mall.CreditVisitFriendsEnabled", task, "VisitFriends");
        AddIfPresent(source, "Mall.CreditVisitOnceADay", task, "VisitFriendsOnceADay");
        AddIfPresent(source, "Mall.LastCreditVisitFriendsTime", task, "VisitFriendsLastTime");
        return task;
    }

    private static JsonObject BuildAwardTask(JsonElement source)
    {
        var task = NewLegacyTask("AwardTask", "Award", false);
        AddIfPresent(source, "Mission.ReceiveAward", task, "Award");
        AddIfPresent(source, "Mission.ReceiveMail", task, "Mail");
        AddIfPresent(source, "Mission.ReceiveFreeRecruit", task, "FreeGacha");
        AddIfPresent(source, "Mission.ReceiveOrundum", task, "Orundum");
        AddIfPresent(source, "Mission.ReceiveMining", task, "Mining");
        AddIfPresent(source, "Mission.ReceiveSpecialAccess", task, "SpecialAccess");
        return task;
    }

    private static JsonObject BuildRoguelikeTask(JsonElement source)
    {
        var task = NewLegacyTask("RoguelikeTask", "Roguelike", false);
        AddIfPresent(source, "Roguelike.RoguelikeTheme", task, "Theme");
        AddIfPresent(source, "Roguelike.Difficulty", task, "Difficulty");
        AddIfPresent(source, "Roguelike.Mode", task, "Mode");
        AddIfPresent(source, "Roguelike.CoreChar", task, "CoreChar");
        AddIfPresent(source, "Roguelike.Squad", task, "Squad");
        AddIfPresent(source, "Roguelike.CollectibleModeSquad", task, "SquadCollectible");
        AddIfPresent(source, "Roguelike.Roles", task, "Roles");
        AddIfPresent(source, "Roguelike.StartsCount", task, "StartCount");
        AddIfPresent(source, "Roguelike.InvestmentEnabled", task, "Investment");
        AddIfPresent(source, "Roguelike.InvestsCount", task, "InvestCount");
        AddIfPresent(source, "Roguelike.InvestmentEnterSecondFloor", task, "InvestWithMoreScore");
        AddIfPresent(source, "Roguelike.StopWhenInvestmentFull", task, "StopWhenDepositFull");
        AddIfPresent(source, "Roguelike.ExitAtFinalBoss", task, "StopAtFinalBoss");
        AddIfPresent(source, "Roguelike.RoguelikeUseSupportUnit", task, "UseSupport");
        AddIfPresent(source, "Roguelike.RoguelikeEnableNonfriendSupport", task, "UseSupportNonFriend");
        AddIfPresent(source, "Roguelike.RefreshTraderWithDice", task, "RefreshTraderWithDice");
        AddIfPresent(source, "Roguelike.RoguelikeStartWithEliteTwo", task, "StartWithEliteTwo");
        AddIfPresent(source, "Roguelike.RoguelikeOnlyStartWithEliteTwo", task, "StartWithEliteTwoOnly");
        AddIfPresent(source, "Roguelike.Roguelike3FirstFloorFoldartal", task, "SamiFirstFloorFoldartal");
        AddIfPresent(source, "Roguelike.Roguelike3StartFloorFoldartal", task, "SamiFirstFloorFoldartals");
        AddIfPresent(source, "Roguelike.Roguelike3NewSquad2StartingFoldartal", task, "SamiNewSquad2StartingFoldartal");
        AddIfPresent(source, "Roguelike.Roguelike3NewSquad2StartingFoldartals", task, "SamiNewSquad2StartingFoldartals");
        AddIfPresent(source, "Roguelike.RoguelikeStartWithSelectList", task, "CollectibleStartAwards");
        AddIfPresent(source, "Roguelike.CollectibleModeShopping", task, "CollectibleShopping");
        AddIfPresent(source, "Roguelike.RoguelikeExpectedCollapsalParadigms", task, "ExpectedCollapsalParadigms");
        AddIfPresent(source, "Roguelike.StopAtMaxLevel", task, "StopWhenLevelMax");
        AddIfPresent(source, "Roguelike.MonthlySquadAutoIterate", task, "MonthlySquadAutoIterate");
        AddIfPresent(source, "Roguelike.MonthlySquadCheckComms", task, "MonthlySquadCheckComms");
        AddIfPresent(source, "Roguelike.DeepExplorationAutoIterate", task, "DeepExplorationAutoIterate");
        AddIfPresent(source, "Roguelike.FindPlaytimeTarget", task, "FindPlaytimeTarget");
        return task;
    }

    private static JsonObject BuildReclamationTask(JsonElement source)
    {
        var task = NewLegacyTask("ReclamationTask", "Reclamation", false);
        AddIfPresent(source, "Reclamation.Theme", task, "Theme");
        AddIfPresent(source, "Reclamation.Mode", task, "Mode");
        AddIfPresent(source, "Reclamation.ToolToCraft", task, "ToolToCraft");
        AddIfPresent(source, "Reclamation.ReclamationIncrementMode", task, "IncrementMode");
        AddIfPresent(source, "Reclamation.ReclamationMaxCraftCountPerRound", task, "MaxCraftCountPerRound");
        AddIfPresent(source, "Reclamation.ReclamationClearStore", task, "ClearStore");
        return task;
    }

    private static JsonObject NewLegacyTask(string type, string name, bool isEnabled)
    {
        return new JsonObject
        {
            ["$type"] = type,
            ["Name"] = name,
            ["IsEnable"] = isEnabled,
        };
    }

    private static void AddIfPresent(JsonElement source, string sourceKey, JsonObject target, string targetKey)
    {
        if (TryGetPropertyIgnoreCase(source, sourceKey, out var value))
        {
            target[targetKey] = JsonImportMergeHelper.ToJsonNode(value);
        }
    }

    private static bool TryReadString(JsonElement source, string key, out string value)
    {
        if (TryGetPropertyIgnoreCase(source, key, out var element))
        {
            value = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
                _ => string.Empty,
            };
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string ReadString(JsonElement source, string key, string fallback)
    {
        return TryReadString(source, key, out var value) ? value : fallback;
    }

    private static bool ReadBool(JsonElement source, string key, bool fallback)
    {
        if (!TryGetPropertyIgnoreCase(source, key, out var element))
        {
            return fallback;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(element.GetString(), out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(element.GetString(), out var number) => number != 0,
            _ => fallback,
        };
    }

    private static int ReadInt(JsonElement source, string key, int fallback)
    {
        if (!TryGetPropertyIgnoreCase(source, key, out var element))
        {
            return fallback;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(element.GetString(), out var number) => number,
            _ => fallback,
        };
    }

    private static int ReadJsonInt(JsonNode? node, int fallback)
    {
        return node is JsonValue value && value.TryGetValue(out int number)
            ? number
            : fallback;
    }

    private static string? ReadTaskType(JsonObject task)
    {
        return task["$type"] is JsonValue value && value.TryGetValue(out string? text)
            ? text
            : null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out value))
            {
                return true;
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed class FlatTaskEntry
    {
        public FlatTaskEntry(string oldName, int order, JsonObject task)
        {
            OldName = oldName;
            Order = order;
            Task = task;
        }

        public string OldName { get; }

        public int Order { get; set; }

        public JsonObject Task { get; }
    }

    private static void AppendUnique(ICollection<string> collection, string value)
    {
        if (!collection.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            collection.Add(value);
        }
    }
}
