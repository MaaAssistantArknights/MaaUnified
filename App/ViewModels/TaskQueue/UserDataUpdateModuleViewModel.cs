using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.App.ViewModels.TaskQueue;

public sealed class UserDataUpdateModuleViewModel : TaskModuleSettingsViewModelBase
{
    private bool _updateOperBox = true;
    private bool _updateDepot = true;
    private string _triggerInterval = UserDataUpdateTaskParamsDto.TriggerEveryTime;
    private DateTimeOffset? _lastOperBoxSyncTime;
    private DateTimeOffset? _lastDepotSyncTime;
    private IReadOnlyList<StringOption> _triggerIntervalOptions = [];

    public UserDataUpdateModuleViewModel(MAAUnifiedRuntime runtime, LocalizedTextMap texts)
        : base(runtime, texts, "TaskQueue.UserDataUpdate")
    {
        Texts.PropertyChanged += OnTextsChanged;
        RebuildTriggerIntervalOptions();
        RefreshSyncTimesFromConfig();
    }

    public string TitleText => Texts.GetOrDefault("TaskQueue.Module.UserDataUpdate", "Update user data");

    public string UpdateOperBoxText => Texts.GetOrDefault("Toolbox.ToolName.OperBox", "Operator recognition");

    public string UpdateDepotText => Texts.GetOrDefault("Toolbox.ToolName.Depot", "Depot recognition");

    public string TriggerIntervalText => Texts.GetOrDefault("TaskQueue.UserDataUpdate.TriggerInterval", "Trigger interval");

    public IReadOnlyList<StringOption> TriggerIntervalOptions => _triggerIntervalOptions;

    public StringOption? SelectedTriggerIntervalOption
    {
        get => TriggerIntervalOptions.FirstOrDefault(option => string.Equals(option.Value, TriggerInterval, StringComparison.OrdinalIgnoreCase));
        set => TriggerInterval = value?.Value ?? UserDataUpdateTaskParamsDto.TriggerEveryTime;
    }

    public bool UpdateOperBox
    {
        get => _updateOperBox;
        set
        {
            if (!SetProperty(ref _updateOperBox, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public bool UpdateDepot
    {
        get => _updateDepot;
        set
        {
            if (!SetProperty(ref _updateDepot, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public string TriggerInterval
    {
        get => _triggerInterval;
        set
        {
            var normalized = NormalizeTriggerInterval(value);
            if (!SetProperty(ref _triggerInterval, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTriggerIntervalOption));
            QueuePersist();
        }
    }

    public bool HasLastOperBoxSyncTime => _lastOperBoxSyncTime is not null;

    public bool HasLastDepotSyncTime => _lastDepotSyncTime is not null;

    public string LastOperBoxSyncDisplayText => BuildSyncDisplayText(_lastOperBoxSyncTime);

    public string LastDepotSyncDisplayText => BuildSyncDisplayText(_lastDepotSyncTime);

    protected override Task LoadFromParametersAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        RefreshSyncTimesFromConfig();
        UpdateOperBox = ReadBool(parameters, "update_oper_box", fallback: true);
        UpdateDepot = ReadBool(parameters, "update_depot", fallback: true);
        TriggerInterval = ReadString(parameters, "trigger_interval", UserDataUpdateTaskParamsDto.TriggerEveryTime);
        return Task.CompletedTask;
    }

    protected override JsonObject BuildParameters()
    {
        return new JsonObject
        {
            ["update_oper_box"] = UpdateOperBox,
            ["update_depot"] = UpdateDepot,
            ["trigger_interval"] = NormalizeTriggerInterval(TriggerInterval),
        };
    }

    private void OnTextsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizedTextMap.Language) or "Item[]"))
        {
            return;
        }

        RebuildTriggerIntervalOptions();
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(UpdateOperBoxText));
        OnPropertyChanged(nameof(UpdateDepotText));
        OnPropertyChanged(nameof(TriggerIntervalText));
        OnPropertyChanged(nameof(LastOperBoxSyncDisplayText));
        OnPropertyChanged(nameof(LastDepotSyncDisplayText));
    }

    private void RebuildTriggerIntervalOptions()
    {
        _triggerIntervalOptions =
        [
            new StringOption(UserDataUpdateTaskParamsDto.TriggerEveryTime, Texts.GetOrDefault("TaskQueue.UserDataUpdate.Trigger.EveryTime", "Every time")),
            new StringOption(UserDataUpdateTaskParamsDto.TriggerDaily, Texts.GetOrDefault("TaskQueue.UserDataUpdate.Trigger.Daily", "Daily")),
            new StringOption(UserDataUpdateTaskParamsDto.TriggerWeekly, Texts.GetOrDefault("TaskQueue.UserDataUpdate.Trigger.Weekly", "Weekly")),
        ];

        OnPropertyChanged(nameof(TriggerIntervalOptions));
        OnPropertyChanged(nameof(SelectedTriggerIntervalOption));
    }

    private void RefreshSyncTimesFromConfig()
    {
        _lastOperBoxSyncTime = ReadPersistedSyncTime(LegacyConfigurationKeys.OperBoxData);
        _lastDepotSyncTime = ReadPersistedSyncTime(LegacyConfigurationKeys.DepotResult);
        OnPropertyChanged(nameof(HasLastOperBoxSyncTime));
        OnPropertyChanged(nameof(HasLastDepotSyncTime));
        OnPropertyChanged(nameof(LastOperBoxSyncDisplayText));
        OnPropertyChanged(nameof(LastDepotSyncDisplayText));
    }

    private string BuildSyncDisplayText(DateTimeOffset? syncTime)
    {
        if (syncTime is null)
        {
            return string.Empty;
        }

        var formatted = syncTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return string.Format(
            CultureInfo.InvariantCulture,
            Texts.GetOrDefault("Toolbox.Depot.LastSync", "Last sync: {0}"),
            formatted);
    }

    private DateTimeOffset? ReadPersistedSyncTime(string key)
    {
        if (!Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return null;
        }

        var payload = node is JsonValue jsonValue && jsonValue.TryGetValue(out string? raw)
            ? raw
            : node.ToJsonString();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(payload) is not JsonObject obj)
            {
                return null;
            }

            if (obj["syncTime"] is not JsonValue syncValue || !syncValue.TryGetValue(out string? syncText))
            {
                return null;
            }

            return DateTimeOffset.TryParse(syncText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ReadBool(JsonObject parameters, string key, bool fallback)
    {
        if (parameters[key] is JsonValue value && value.TryGetValue(out bool parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static string ReadString(JsonObject parameters, string key, string fallback)
    {
        if (parameters[key] is JsonValue value && value.TryGetValue(out string? parsed) && !string.IsNullOrWhiteSpace(parsed))
        {
            return NormalizeTriggerInterval(parsed);
        }

        return fallback;
    }

    private static string NormalizeTriggerInterval(string? value)
    {
        return value?.Trim() switch
        {
            var text when string.Equals(text, UserDataUpdateTaskParamsDto.TriggerDaily, StringComparison.OrdinalIgnoreCase) => UserDataUpdateTaskParamsDto.TriggerDaily,
            var text when string.Equals(text, UserDataUpdateTaskParamsDto.TriggerWeekly, StringComparison.OrdinalIgnoreCase) => UserDataUpdateTaskParamsDto.TriggerWeekly,
            _ => UserDataUpdateTaskParamsDto.TriggerEveryTime,
        };
    }

    public sealed record StringOption(string Value, string DisplayName);
}
