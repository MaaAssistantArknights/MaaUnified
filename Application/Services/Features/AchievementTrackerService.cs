using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;
using MAAUnified.Compat.Runtime;

namespace MAAUnified.Application.Services.Features;

public interface IAchievementTrackerService
{
    event EventHandler<AchievementUnlockedEvent>? AchievementUnlocked;

    event EventHandler? StateChanged;

    void SetCurrentLanguage(string? language);

    Task<UiOperationResult<AchievementTrackerSnapshot>> GetSnapshotAsync(string? language = null, CancellationToken cancellationToken = default);

    Task<UiOperationResult> BackupAsync(string path, CancellationToken cancellationToken = default);

    Task<UiOperationResult> RestoreAsync(string path, CancellationToken cancellationToken = default);

    Task<UiOperationResult> UnlockAllAsync(CancellationToken cancellationToken = default);

    Task<UiOperationResult> LockAllAsync(CancellationToken cancellationToken = default);

    UiOperationResult RecordStartup(AchievementStartupContext? context = null);

    UiOperationResult Unlock(string id, bool staysOpen = true, bool forceStayOpen = false);

    UiOperationResult Lock(string id);

    UiOperationResult AddProgress(string id, int amount = 1);

    UiOperationResult AddProgressToGroup(string group, int amount = 1);

    UiOperationResult SetProgress(string id, int progress);

    UiOperationResult SetProgressToGroup(string group, int progress);

    int GetProgress(string id);

    int GetProgressToGroup(string group);

    JsonNode? GetCustomData(string id, string key);

    void SetCustomData(string id, string key, JsonNode value);

    void MissionStartCountAdd();

    void UseDailyAdd();

    void ClueObsessionAdd();
}

public sealed class AchievementTrackerService : IAchievementTrackerService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly IReadOnlyDictionary<string, AchievementDefinition> Definitions = BuildDefinitions();

    private readonly object _gate = new();
    private readonly UnifiedConfigurationService? _configService;
    private readonly string _dataFilePath;
    private readonly bool _persistToDataFile;
    private readonly Dictionary<string, AchievementStateRecord> _states = new(StringComparer.Ordinal);
    private string _currentLanguage = UiLanguageCatalog.DefaultLanguage;
    private bool _startupRecorded;

    public AchievementTrackerService()
        : this(configService: null, ResolveDefaultBaseDirectory())
    {
    }

    public AchievementTrackerService(UnifiedConfigurationService? configService, string baseDirectory)
    {
        _configService = configService;
        _dataFilePath = Path.Combine(baseDirectory, "data", "Achievement.json");
        _persistToDataFile = configService is not null;

        foreach (var definition in Definitions.Values)
        {
            _states[definition.Id] = new AchievementStateRecord { Id = definition.Id };
        }

        LoadFromDisk();
    }

    public event EventHandler<AchievementUnlockedEvent>? AchievementUnlocked;

    public event EventHandler? StateChanged;

    public void SetCurrentLanguage(string? language)
    {
        lock (_gate)
        {
            _currentLanguage = UiLanguageCatalog.Normalize(language);
        }
    }

    public Task<UiOperationResult<AchievementTrackerSnapshot>> GetSnapshotAsync(string? language = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AchievementTrackerSnapshot snapshot;
        lock (_gate)
        {
            snapshot = BuildSnapshot(UiLanguageCatalog.Normalize(language ?? _currentLanguage));
        }

        return Task.FromResult(UiOperationResult<AchievementTrackerSnapshot>.Ok(snapshot, "Loaded achievement tracker snapshot."));
    }

    public Task<UiOperationResult> BackupAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.UiOperationFailed, "Achievement backup path is empty."));
        }

        lock (_gate)
        {
            try
            {
                EnsureDirectory(Path.GetDirectoryName(path));
                using var stream = File.Create(path);
                JsonSerializer.Serialize(stream, _states, JsonOptions);
            }
            catch (Exception ex)
            {
                return Task.FromResult(UiOperationResult.Fail(UiErrorCode.UiOperationFailed, $"Achievement backup failed: {ex.Message}", ex.ToString()));
            }
        }

        return Task.FromResult(UiOperationResult.Ok("Achievement backup saved."));
    }

    public Task<UiOperationResult> RestoreAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.UiOperationFailed, "Achievement restore file was not found."));
        }

        Dictionary<string, AchievementStateRecord>? restored;
        try
        {
            using var stream = File.OpenRead(path);
            restored = JsonSerializer.Deserialize<Dictionary<string, AchievementStateRecord>>(stream, JsonOptions);
        }
        catch (Exception ex)
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.UiOperationFailed, $"Achievement restore failed: {ex.Message}", ex.ToString()));
        }

        if (restored is null || restored.Count == 0)
        {
            return Task.FromResult(UiOperationResult.Fail(UiErrorCode.UiOperationFailed, "Achievement restore file is empty or invalid."));
        }

        lock (_gate)
        {
            foreach (var definition in Definitions.Values)
            {
                var target = _states[definition.Id];
                if (!restored.TryGetValue(definition.Id, out var source))
                {
                    target.IsUnlocked = false;
                    target.UnlockedTime = null;
                    target.Progress = 0;
                    target.CustomData = null;
                    target.IsNewUnlock = false;
                    continue;
                }

                target.IsUnlocked = source.IsUnlocked;
                target.UnlockedTime = source.UnlockedTime;
                target.Progress = source.Progress;
                target.CustomData = CloneCustomData(source.CustomData);
                target.IsNewUnlock = false;
            }

            SaveLocked();
        }

        OnStateChanged();
        return Task.FromResult(UiOperationResult.Ok("Achievement restore completed."));
    }

    public async Task<UiOperationResult> UnlockAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<AchievementUnlockedEvent> notifications = [];
        var policy = ReadPolicy();
        lock (_gate)
        {
            foreach (var definition in Definitions.Values.Where(definition => !_states[definition.Id].IsUnlocked))
            {
                var notification = UnlockLocked(definition.Id, staysOpen: false, forceStayOpen: false, saveAfterChange: false);
                if (notification is not null)
                {
                    notifications.Add(notification);
                }
            }

            SaveLocked();
        }

        foreach (var notification in notifications)
        {
            AchievementUnlocked?.Invoke(this, notification);
            if (!policy.PopupDisabled)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        OnStateChanged();
        return UiOperationResult.Ok("All achievements unlocked.");
    }

    public Task<UiOperationResult> LockAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var state in _states.Values)
            {
                state.IsUnlocked = false;
                state.UnlockedTime = null;
                state.Progress = 0;
                state.CustomData = null;
                state.IsNewUnlock = false;
            }

            SaveLocked();
        }

        OnStateChanged();
        return Task.FromResult(UiOperationResult.Ok("All achievements reset."));
    }

    public UiOperationResult RecordStartup(AchievementStartupContext? context = null)
    {
        lock (_gate)
        {
            if (_startupRecorded)
            {
                return UiOperationResult.Ok("Achievement startup state already recorded.");
            }

            _startupRecorded = true;
        }

        var nowLocal = context?.NowLocal ?? DateTimeOffset.Now;
        _ = Unlock("FirstLaunch");

        if (Random.Shared.NextDouble() < 0.00066d)
        {
            _ = Unlock("Lucky");
        }

        if (nowLocal.Hour is >= 0 and < 4)
        {
            _ = Unlock("MidnightLaunch");
        }

        if (nowLocal.Month == 4 && nowLocal.Day == 1)
        {
            _ = Unlock("AprilFools");
        }

        var chineseCalendar = new ChineseLunisolarCalendar();
        if (chineseCalendar.GetMonth(nowLocal.DateTime) == 1
            && chineseCalendar.GetDayOfMonth(nowLocal.DateTime) == 1)
        {
            _ = Unlock("LunarNewYear");
        }

        return UiOperationResult.Ok("Achievement startup state recorded.");
    }

    public UiOperationResult Unlock(string id, bool staysOpen = true, bool forceStayOpen = false)
    {
        AchievementUnlockedEvent? notification;
        lock (_gate)
        {
            notification = UnlockLocked(id, staysOpen, forceStayOpen, saveAfterChange: true);
        }

        if (notification is not null)
        {
            AchievementUnlocked?.Invoke(this, notification);
        }

        if (notification is not null)
        {
            OnStateChanged();
        }

        return notification is null
            ? UiOperationResult.Ok("Achievement already unlocked or unavailable.")
            : UiOperationResult.Ok($"Achievement `{id}` unlocked.");
    }

    public UiOperationResult Lock(string id)
    {
        var changed = false;
        lock (_gate)
        {
            if (!_states.TryGetValue(id, out var state))
            {
                return UiOperationResult.Fail(UiErrorCode.UiOperationFailed, $"Achievement `{id}` was not found.");
            }

            if (state.IsUnlocked || state.Progress != 0 || state.UnlockedTime is not null || state.CustomData is not null)
            {
                state.IsUnlocked = false;
                state.UnlockedTime = null;
                state.Progress = 0;
                state.CustomData = null;
                state.IsNewUnlock = false;
                SaveLocked();
                changed = true;
            }
        }

        if (changed)
        {
            OnStateChanged();
        }

        return UiOperationResult.Ok($"Achievement `{id}` reset.");
    }

    public UiOperationResult AddProgress(string id, int amount = 1)
    {
        AchievementUnlockedEvent? notification = null;
        lock (_gate)
        {
            if (!_states.TryGetValue(id, out var state))
            {
                return UiOperationResult.Fail(UiErrorCode.UiOperationFailed, $"Achievement `{id}` was not found.");
            }

            state.Progress += amount;
            notification = PromoteProgressUnlockLocked(id);
            SaveLocked();
        }

        if (notification is not null)
        {
            AchievementUnlocked?.Invoke(this, notification);
        }

        OnStateChanged();
        return UiOperationResult.Ok($"Achievement progress updated for `{id}`.");
    }

    public UiOperationResult AddProgressToGroup(string group, int amount = 1)
    {
        AchievementUnlockedEvent[] notifications;
        lock (_gate)
        {
            notifications = Definitions.Values
                .Where(definition => string.Equals(definition.Group, group, StringComparison.Ordinal))
                .Select(definition =>
                {
                    _states[definition.Id].Progress += amount;
                    return PromoteProgressUnlockLocked(definition.Id);
                })
                .Where(notification => notification is not null)
                .Cast<AchievementUnlockedEvent>()
                .ToArray();
            SaveLocked();
        }

        foreach (var notification in notifications)
        {
            AchievementUnlocked?.Invoke(this, notification);
        }

        OnStateChanged();
        return UiOperationResult.Ok($"Achievement group progress updated for `{group}`.");
    }

    public UiOperationResult SetProgress(string id, int progress)
    {
        AchievementUnlockedEvent? notification = null;
        lock (_gate)
        {
            if (!_states.TryGetValue(id, out var state))
            {
                return UiOperationResult.Fail(UiErrorCode.UiOperationFailed, $"Achievement `{id}` was not found.");
            }

            state.Progress = Math.Max(0, progress);
            notification = PromoteProgressUnlockLocked(id);
            SaveLocked();
        }

        if (notification is not null)
        {
            AchievementUnlocked?.Invoke(this, notification);
        }

        OnStateChanged();
        return UiOperationResult.Ok($"Achievement progress set for `{id}`.");
    }

    public UiOperationResult SetProgressToGroup(string group, int progress)
    {
        AchievementUnlockedEvent[] notifications;
        lock (_gate)
        {
            notifications = Definitions.Values
                .Where(definition => string.Equals(definition.Group, group, StringComparison.Ordinal))
                .Select(definition =>
                {
                    _states[definition.Id].Progress = Math.Max(0, progress);
                    return PromoteProgressUnlockLocked(definition.Id);
                })
                .Where(notification => notification is not null)
                .Cast<AchievementUnlockedEvent>()
                .ToArray();
            SaveLocked();
        }

        foreach (var notification in notifications)
        {
            AchievementUnlocked?.Invoke(this, notification);
        }

        OnStateChanged();
        return UiOperationResult.Ok($"Achievement group progress set for `{group}`.");
    }

    public int GetProgress(string id)
    {
        lock (_gate)
        {
            return _states.TryGetValue(id, out var state) ? state.Progress : 0;
        }
    }

    public int GetProgressToGroup(string group)
    {
        lock (_gate)
        {
            return Definitions.Values
                .Where(definition => string.Equals(definition.Group, group, StringComparison.Ordinal))
                .Select(definition => _states[definition.Id].Progress)
                .DefaultIfEmpty()
                .Max();
        }
    }

    public JsonNode? GetCustomData(string id, string key)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(id, out var state)
                || state.CustomData is null
                || !state.CustomData.TryGetValue(key, out var value))
            {
                return null;
            }

            return value?.DeepClone();
        }
    }

    public void SetCustomData(string id, string key, JsonNode value)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(id, out var state))
            {
                return;
            }

            state.CustomData ??= new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            state.CustomData[key] = value.DeepClone();
            SaveLocked();
        }

        OnStateChanged();
    }

    public void MissionStartCountAdd()
    {
        const string id = "MissionStartCount";
        const string key = "LastStartDate";
        var today = DateTime.UtcNow.Date;
        var lastDate = ReadCustomDate(id, key);
        if (lastDate.HasValue && lastDate.Value == today)
        {
            _ = AddProgress(id);
        }
        else
        {
            SetCustomData(id, key, JsonValue.Create(today));
            _ = SetProgress(id, 1);
        }
    }

    public void UseDailyAdd()
    {
        const string id = "UseDaily1";
        const string group = "UseDaily";
        const string key = "LastLaunchDate";
        var today = DateTime.UtcNow.Date;
        var lastDate = ReadCustomDate(id, key);
        if (lastDate.HasValue)
        {
            var delta = (today - lastDate.Value).TotalDays;
            if (Math.Abs(delta - 1d) < 0.01d)
            {
                _ = AddProgressToGroup(group);
            }
            else if (delta > 1d)
            {
                _ = SetProgressToGroup(group, 1);
            }
        }
        else
        {
            _ = SetProgressToGroup(group, 1);
        }

        SetCustomData(id, key, JsonValue.Create(today));
    }

    public void ClueObsessionAdd()
    {
        const string id = "ClueObsession";
        const string key = "LastOpeningData";
        var today = DateTime.UtcNow.Date;
        var lastDate = ReadCustomDate(id, key);
        if (lastDate.HasValue)
        {
            var delta = (today - lastDate.Value).TotalDays;
            if (Math.Abs(delta - 1d) < 0.01d)
            {
                _ = AddProgress(id);
            }
            else if (delta > 1d)
            {
                _ = SetProgress(id, 1);
            }
        }
        else
        {
            _ = SetProgress(id, 1);
        }

        SetCustomData(id, key, JsonValue.Create(today));
    }

    private AchievementTrackerSnapshot BuildSnapshot(string language)
    {
        var policy = ReadPolicy();
        var items = Definitions.Values
            .Select(definition => BuildListItem(definition, _states[definition.Id], language))
            .Where(static item => item.CanShow)
            .OrderByDescending(item => item.IsUnlocked)
            .ThenByDescending(item => item.IsNewUnlock)
            .ThenBy(item => item.SortCategory)
            .ThenBy(item => item.SortGroup, StringComparer.Ordinal)
            .ThenBy(item => item.SortGroupIndex)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Cast<AchievementListItem>()
            .ToArray();
        var unlockedCount = _states.Values.Count(state => state.IsUnlocked);
        return new AchievementTrackerSnapshot(
            language,
            policy,
            unlockedCount,
            Definitions.Count,
            items,
            _dataFilePath);
    }

    private AchievementListItem BuildListItem(AchievementDefinition definition, AchievementStateRecord state, string language)
    {
        var isUnlocked = state.IsUnlocked;
        var title = isUnlocked
            ? AchievementTextCatalog.GetString($"Achievement.{definition.Id}.Title", language, definition.Id)
            : "???";
        var description = isUnlocked
            ? AchievementTextCatalog.GetString($"Achievement.{definition.Id}.Description", language, string.Empty)
            : "???";
        var conditions = !definition.IsHidden || isUnlocked
            ? AchievementTextCatalog.GetString($"Achievement.{definition.Id}.Conditions", language, string.Empty)
            : "???";
        var categoryText = BuildStatusText(definition, state, language);
        var unlockedAtText = state.UnlockedTime.HasValue
            ? $"{AchievementTextCatalog.GetString("AchievementUnlockedAt", language, "Unlocked at: ")}{state.UnlockedTime.Value.ToLocalTime():G}"
            : string.Empty;
        return new AchievementListItem(
            Id: definition.Id,
            Title: title,
            Description: description,
            Status: categoryText,
            Conditions: conditions,
            IsUnlocked: isUnlocked,
            IsHidden: definition.IsHidden,
            IsProgressive: definition.Target > 0,
            ShowProgress: definition.Target > 0 && !isUnlocked,
            Progress: state.Progress,
            Target: definition.Target,
            MedalColor: ResolveMedalColor(definition, state),
            UnlockedAtText: unlockedAtText,
            IsNewUnlock: state.IsNewUnlock,
            CanShow: !definition.IsHidden || isUnlocked,
            SortCategory: (int)definition.Category,
            SortGroup: definition.Group,
            SortGroupIndex: definition.GroupIndex);
    }

    private string BuildStatusText(AchievementDefinition definition, AchievementStateRecord state, string language)
    {
        var parts = new List<string> { ResolveCategoryName(definition.Category, language) };
        if (definition.IsRare)
        {
            parts.Add(DialogText(language, "稀有", "Rare"));
        }

        if (definition.IsHidden && !state.IsUnlocked)
        {
            parts.Add(DialogText(language, "隐藏", "Hidden"));
        }

        return string.Join(" / ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private string ResolveCategoryName(AchievementCategory category, string language)
    {
        return category switch
        {
            AchievementCategory.BasicUsage => DialogText(language, "基础使用", "Basic Usage"),
            AchievementCategory.FeatureExploration => DialogText(language, "功能探索", "Feature Exploration"),
            AchievementCategory.AutoBattle => DialogText(language, "自动战斗", "Auto Battle"),
            AchievementCategory.Humor => DialogText(language, "梗图成就", "Humor"),
            AchievementCategory.BugRelated => DialogText(language, "BUG 相关", "Bug Related"),
            AchievementCategory.Behavior => DialogText(language, "行为习惯", "Behavior"),
            AchievementCategory.EasterEgg => DialogText(language, "彩蛋", "Easter Egg"),
            AchievementCategory.Rare => DialogText(language, "稀有", "Rare"),
            _ => category.ToString(),
        };
    }

    private string ResolveMedalColor(AchievementDefinition definition, AchievementStateRecord state)
    {
        if (!state.IsUnlocked)
        {
            return "#6B4C2B";
        }

        if (definition.IsRare)
        {
            return "#EC407A";
        }

        if (definition.IsHidden)
        {
            return "#D4AF37";
        }

        return definition.Category switch
        {
            AchievementCategory.BasicUsage => "#42A5F5",
            AchievementCategory.FeatureExploration => "#66BB6A",
            AchievementCategory.AutoBattle => "#FFA726",
            AchievementCategory.Humor => "#AB47BC",
            AchievementCategory.BugRelated => "#EF5350",
            AchievementCategory.Behavior => "#37474F",
            AchievementCategory.EasterEgg => "#FFEB3B",
            AchievementCategory.Rare => "#EC407A",
            _ => "#B0B0B0",
        };
    }

    private AchievementUnlockedEvent? UnlockLocked(string id, bool staysOpen, bool forceStayOpen, bool saveAfterChange)
    {
        if (!_states.TryGetValue(id, out var state) || state.IsUnlocked || !Definitions.TryGetValue(id, out var definition))
        {
            return null;
        }

        state.IsUnlocked = true;
        state.UnlockedTime = DateTime.UtcNow;
        state.IsNewUnlock = true;
        if (saveAfterChange)
        {
            SaveLocked();
        }

        var policy = ReadPolicy();
        if (policy.PopupDisabled && !forceStayOpen)
        {
            return null;
        }

        var autoClose = !(forceStayOpen || (staysOpen && !policy.PopupAutoClose));
        var language = _currentLanguage;
        return new AchievementUnlockedEvent(
            id,
            AchievementTextCatalog.GetString($"Achievement.{definition.Id}.Title", language, definition.Id),
            AchievementTextCatalog.GetString($"Achievement.{definition.Id}.Description", language, string.Empty),
            ResolveMedalColor(definition, state),
            autoClose,
            state.UnlockedTime.Value);
    }

    private AchievementUnlockedEvent? PromoteProgressUnlockLocked(string id)
    {
        if (!Definitions.TryGetValue(id, out var definition)
            || !_states.TryGetValue(id, out var state)
            || state.IsUnlocked
            || definition.Target <= 0
            || state.Progress < definition.Target)
        {
            return null;
        }

        return UnlockLocked(id, staysOpen: true, forceStayOpen: false, saveAfterChange: false);
    }

    private DateTime? ReadCustomDate(string id, string key)
    {
        var node = GetCustomData(id, key);
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.Deserialize<DateTime>();
        }
        catch
        {
            return DateTime.TryParse(node.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        }
    }

    private void LoadFromDisk()
    {
        if (!_persistToDataFile)
        {
            return;
        }

        if (!File.Exists(_dataFilePath))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(_dataFilePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, AchievementStateRecord>>(stream, JsonOptions);
            if (loaded is null)
            {
                return;
            }

            foreach (var (id, saved) in loaded)
            {
                if (!_states.TryGetValue(id, out var target))
                {
                    continue;
                }

                target.IsUnlocked = saved.IsUnlocked;
                target.UnlockedTime = saved.UnlockedTime;
                target.Progress = saved.Progress;
                target.CustomData = CloneCustomData(saved.CustomData);
                target.IsNewUnlock = false;
            }
        }
        catch
        {
            // Best-effort load; malformed files should not break startup.
        }
    }

    private void SaveLocked()
    {
        if (!_persistToDataFile)
        {
            return;
        }

        EnsureDirectory(Path.GetDirectoryName(_dataFilePath));
        using var stream = File.Create(_dataFilePath);
        JsonSerializer.Serialize(stream, _states, JsonOptions);
    }

    private AchievementPolicy ReadPolicy()
    {
        if (_configService is null)
        {
            return AchievementPolicy.Default;
        }

        var config = _configService.CurrentConfig;
        return new AchievementPolicy(
            PopupDisabled: ReadProfileBool(config, ConfigurationKeys.AchievementPopupDisabled, false),
            PopupAutoClose: ReadProfileBool(config, ConfigurationKeys.AchievementPopupAutoClose, true));
    }

    private static bool ReadProfileBool(UnifiedConfig config, string key, bool fallback)
    {
        if (!string.IsNullOrWhiteSpace(config.CurrentProfile)
            && config.Profiles.TryGetValue(config.CurrentProfile, out var profile)
            && profile.Values.TryGetValue(key, out var profileNode)
            && TryReadBool(profileNode, out var profileValue))
        {
            return profileValue;
        }

        if (config.GlobalValues.TryGetValue(key, out var globalNode)
            && TryReadBool(globalNode, out var globalValue))
        {
            return globalValue;
        }

        return fallback;
    }

    private static bool TryReadBool(JsonNode? node, out bool value)
    {
        value = false;
        if (node is null)
        {
            return false;
        }

        var text = node.ToString();
        if (bool.TryParse(text, out value))
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            value = number != 0;
            return true;
        }

        return false;
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Dictionary<string, JsonNode?>? CloneCustomData(Dictionary<string, JsonNode?>? source)
    {
        if (source is null)
        {
            return null;
        }

        return source.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.DeepClone(),
            StringComparer.Ordinal);
    }

    private static void EnsureDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string ResolveDefaultBaseDirectory()
        => RuntimeLayout.ResolveRuntimeBaseDirectory();

    private static string DialogText(string language, string zh, string en)
    {
        return language switch
        {
            "zh-cn" or "zh-tw" or "pallas" => zh,
            _ => en,
        };
    }

    private static IReadOnlyDictionary<string, AchievementDefinition> BuildDefinitions()
    {
        static AchievementDefinition BasicUsage(string id, string group = "", int target = 0, bool hidden = false, bool rare = false, int groupIndex = int.MaxValue)
            => new(id, group, target, hidden, AchievementCategory.BasicUsage, rare, groupIndex);
        static AchievementDefinition FeatureExploration(string id, string group = "", int target = 0, bool hidden = false, bool rare = false, int groupIndex = int.MaxValue)
            => new(id, group, target, hidden, AchievementCategory.FeatureExploration, rare, groupIndex);
        static AchievementDefinition AutoBattle(string id, string group = "", int target = 0, bool hidden = false, bool rare = false, int groupIndex = int.MaxValue)
            => new(id, group, target, hidden, AchievementCategory.AutoBattle, rare, groupIndex);
        static AchievementDefinition Humor(string id, string group = "", int target = 0, bool hidden = false, bool rare = false, int groupIndex = int.MaxValue)
            => new(id, group, target, hidden, AchievementCategory.Humor, rare, groupIndex);
        static AchievementDefinition BugRelated(string id, string group = "", int target = 0, bool hidden = false, bool rare = false, int groupIndex = int.MaxValue)
            => new(id, group, target, hidden, AchievementCategory.BugRelated, rare, groupIndex);
        static AchievementDefinition Behavior(string id, string group = "", int target = 0, bool hidden = false, bool rare = false, int groupIndex = int.MaxValue)
            => new(id, group, target, hidden, AchievementCategory.Behavior, rare, groupIndex);
        static AchievementDefinition EasterEgg(string id, string group = "", int target = 0, bool hidden = false, bool rare = false, int groupIndex = int.MaxValue)
            => new(id, group, target, hidden, AchievementCategory.EasterEgg, rare, groupIndex);

        AchievementDefinition[] definitions =
        [
            BasicUsage("SanitySpender1", "SanitySpender", 10, groupIndex: 1),
            BasicUsage("SanitySpender2", "SanitySpender", 100, groupIndex: 2),
            BasicUsage("SanitySpender3", "SanitySpender", 1000, groupIndex: 3),
            BasicUsage("SanitySaver1", "SanitySaver", 1, groupIndex: 1),
            BasicUsage("SanitySaver2", "SanitySaver", 10, groupIndex: 2),
            BasicUsage("SanitySaver3", "SanitySaver", 50, groupIndex: 3),
            BasicUsage("RoguelikeGamePass1", "RoguelikeGamePass", 1, groupIndex: 1),
            BasicUsage("RoguelikeGamePass2", "RoguelikeGamePass", 5, groupIndex: 2),
            BasicUsage("RoguelikeGamePass3", "RoguelikeGamePass", 10, groupIndex: 3),
            BasicUsage("RoguelikeN04", "RoguelikeN", groupIndex: 1),
            BasicUsage("RoguelikeN08", "RoguelikeN", groupIndex: 2),
            BasicUsage("RoguelikeN12", "RoguelikeN", groupIndex: 3),
            BasicUsage("RoguelikeN15", "RoguelikeN", hidden: false, rare: true, groupIndex: 4),
            BasicUsage("RoguelikeRetreat", "Roguelike", 100),
            BasicUsage("RoguelikeGoldMax", "Roguelike", 999),
            BasicUsage("FirstLaunch"),
            BasicUsage("SanityExpire", target: 8),
            BasicUsage("OverLimitAgent", target: 100, hidden: true),
            BasicUsage("RecruitGambler", target: 50),
            BasicUsage("ClueCollector", "ClueUse", 20, groupIndex: 1),
            BasicUsage("CluePhilosopher", "ClueUse", 50, groupIndex: 2),
            BasicUsage("ClueObsession", target: 7, rare: true, groupIndex: 3),
            BasicUsage("ClueSharer", "ClueSend", 20, groupIndex: 1),
            BasicUsage("CluePhilanthropist", "ClueSend", 50, groupIndex: 2),

            FeatureExploration("ScheduleMaster1", "ScheduleMaster", 1, groupIndex: 1),
            FeatureExploration("ScheduleMaster2", "ScheduleMaster", 100, groupIndex: 2),
            FeatureExploration("MirrorChyanFirstUse", "MirrorChyan", hidden: true),
            FeatureExploration("MirrorChyanCdkError", "MirrorChyan", hidden: true),
            FeatureExploration("Pioneer1", "Pioneer", groupIndex: 1),
            FeatureExploration("Pioneer2", "Pioneer", hidden: true, groupIndex: 2),
            FeatureExploration("Pioneer3", "Pioneer", hidden: true, groupIndex: 3),
            FeatureExploration("MosquitoLeg", target: 5),
            FeatureExploration("RealGacha", hidden: true),
            FeatureExploration("PeekScreen", hidden: true),
            FeatureExploration("CustomizationMaster", hidden: true),
            FeatureExploration("LogSupervisor"),
            FeatureExploration("TaskChainKing", target: 7),
            FeatureExploration("HotkeyMagician"),
            FeatureExploration("WarehouseMiser", target: 10000),
            FeatureExploration("HrSpecialist", "HrManager", 10, groupIndex: 1),
            FeatureExploration("HrSeniorSpecialist", "HrManager", 20, groupIndex: 2),
            FeatureExploration("NotFound404", hidden: true),
            FeatureExploration("Linguist"),
            FeatureExploration("StartupBoot"),

            AutoBattle("UseCopilot1", "UseCopilot", 1, groupIndex: 1),
            AutoBattle("UseCopilot2", "UseCopilot", 10, groupIndex: 2),
            AutoBattle("UseCopilot3", "UseCopilot", 100, groupIndex: 3),
            AutoBattle("CopilotLikeGiven1", "CopilotLikeGiven", 1, groupIndex: 1),
            AutoBattle("CopilotLikeGiven2", "CopilotLikeGiven", 10, groupIndex: 2),
            AutoBattle("CopilotLikeGiven3", "CopilotLikeGiven", 50, groupIndex: 3),
            AutoBattle("CopilotError"),
            AutoBattle("MapOutdated", hidden: true),
            AutoBattle("Irreplaceable", hidden: true),

            Humor("SnapshotChallenge1", "SnapshotChallenge", hidden: true, groupIndex: 6),
            Humor("SnapshotChallenge2", "SnapshotChallenge", hidden: true, groupIndex: 5),
            Humor("SnapshotChallenge3", "SnapshotChallenge", groupIndex: 1),
            Humor("SnapshotChallenge4", "SnapshotChallenge", groupIndex: 2),
            Humor("SnapshotChallenge5", "SnapshotChallenge", groupIndex: 3),
            Humor("SnapshotChallenge6", "SnapshotChallenge", hidden: true, rare: true, groupIndex: 4),
            Humor("QuickCloser", hidden: true),
            Humor("TacticalRetreat"),
            Humor("Martian", hidden: true),
            Humor("AnnouncementStubbornClick", hidden: true),
            Humor("RecruitNoSixStar", "Recruit", 500, groupIndex: 1),
            Humor("RecruitNoSixStarStreak", "Recruit", 500, hidden: true, groupIndex: 2),
            Humor("Time325", hidden: true),

            BugRelated("CongratulationError", hidden: true),
            BugRelated("UnexpectedCrash", hidden: true),
            BugRelated("ProblemFeedback"),
            BugRelated("CdnTorture", target: 3),

            Behavior("MissionStartCount", target: 3),
            Behavior("LongTaskTimeout"),
            Behavior("ProxyOnline3Hours", hidden: true),
            Behavior("TaskStartCancel", hidden: true),
            Behavior("AfkWatcher"),
            Behavior("UseDaily1", "UseDaily", 7, groupIndex: 1),
            Behavior("UseDaily2", "UseDaily", 30, groupIndex: 2),
            Behavior("UseDaily3", "UseDaily", 365, rare: true, groupIndex: 3),
            Behavior("UpdateObsession", "Update", groupIndex: 1),
            Behavior("UpdateEarlyBird", "Update", hidden: true, groupIndex: 2),

            EasterEgg("Rules", hidden: true),
            EasterEgg("VersionClick", hidden: true),
            EasterEgg("AprilFools", "Login", hidden: true),
            EasterEgg("MidnightLaunch", "Login", hidden: true),
            EasterEgg("LunarNewYear", "Login", hidden: true),
            EasterEgg("Lucky", hidden: true, rare: true),
            EasterEgg("SanityPlanner", rare: true),
            EasterEgg("WarehouseKeeper", hidden: true),
        ];

        return definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
    }

    private sealed record AchievementDefinition(
        string Id,
        string Group,
        int Target,
        bool IsHidden,
        AchievementCategory Category,
        bool IsRare,
        int GroupIndex);
}
