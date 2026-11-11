using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MAAUnified.Application.Models;

public enum AchievementCategory
{
    BasicUsage = 0,
    FeatureExploration = 1,
    AutoBattle = 2,
    Humor = 3,
    BugRelated = 4,
    Behavior = 5,
    EasterEgg = 6,
    Rare = 7,
}

public sealed class AchievementStateRecord
{
    public string Id { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsUnlocked { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? UnlockedTime { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Progress { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonNode?>? CustomData { get; set; }

    [JsonIgnore]
    public bool IsNewUnlock { get; set; }
}

public sealed record AchievementTrackerSnapshot(
    string Language,
    AchievementPolicy Policy,
    int UnlockedCount,
    int TotalCount,
    IReadOnlyList<AchievementListItem> Items,
    string DataFilePath);

public sealed record AchievementUnlockedEvent(
    string Id,
    string Title,
    string Description,
    string MedalColor,
    bool AutoClose,
    DateTimeOffset UnlockedAtUtc);

public sealed record AchievementStartupContext(
    DateTimeOffset NowUtc,
    DateTimeOffset NowLocal,
    DateTimeOffset? BuildTimestampUtc = null,
    DateTimeOffset? ResourceTimestampUtc = null,
    string? VersionString = null);
