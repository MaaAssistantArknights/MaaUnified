using System;
using MAAUnified.App.ViewModels.Infrastructure;

namespace MAAUnified.App.ViewModels.Copilot;

public sealed class CopilotItemViewModel : ObservableObject
{
    private string _name;
    private string _type;
    private string _status = "Ready";
    private string _sourcePath = string.Empty;
    private string _inlinePayload = string.Empty;
    private bool _isChecked = true;
    private bool _isRaid;
    private int _copilotId;
    private int _index;
    private int? _tabIndex;
    private string _raidLabel = "突袭";
    private string _inlinePayloadHint = "inline-json";

    public CopilotItemViewModel(
        string name,
        string type,
        string? sourcePath = null,
        string? inlinePayload = null)
    {
        _name = name;
        _type = type;
        _sourcePath = sourcePath ?? string.Empty;
        _inlinePayload = inlinePayload ?? string.Empty;
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string Type
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (SetProperty(ref _sourcePath, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(ExecutionPathHint));
            }
        }
    }

    public string InlinePayload
    {
        get => _inlinePayload;
        set
        {
            if (SetProperty(ref _inlinePayload, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(ExecutionPathHint));
            }
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    public bool IsRaid
    {
        get => _isRaid;
        set
        {
            if (SetProperty(ref _isRaid, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public int CopilotId
    {
        get => _copilotId;
        set => SetProperty(ref _copilotId, Math.Max(0, value));
    }

    // WPF 对齐（4843310bec）：列表序号作为 id 随 Copilot 列表下发，Core 在
    // CopilotListLoadTaskFileSuccess 中回报该值以确定刚完成的条目。
    public int Index
    {
        get => _index;
        set => SetProperty(ref _index, value);
    }

    public int? TabIndex
    {
        get => _tabIndex;
        set => SetProperty(ref _tabIndex, value);
    }

    public string ExecutionPathHint
        => !string.IsNullOrWhiteSpace(SourcePath)
            ? SourcePath
            : !string.IsNullOrWhiteSpace(InlinePayload)
                ? _inlinePayloadHint
                : string.Empty;

    public string DisplayName => IsRaid ? $"{Name} ({_raidLabel})" : Name;

    public void ApplyLocalization(string raidLabel, string inlinePayloadHint)
    {
        var displayNameChanged = !string.Equals(_raidLabel, raidLabel, StringComparison.Ordinal);
        var executionPathHintChanged = !string.Equals(_inlinePayloadHint, inlinePayloadHint, StringComparison.Ordinal);

        _raidLabel = raidLabel;
        _inlinePayloadHint = inlinePayloadHint;

        if (displayNameChanged && IsRaid)
        {
            OnPropertyChanged(nameof(DisplayName));
        }

        if (executionPathHintChanged && string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(InlinePayload))
        {
            OnPropertyChanged(nameof(ExecutionPathHint));
        }
    }
}
