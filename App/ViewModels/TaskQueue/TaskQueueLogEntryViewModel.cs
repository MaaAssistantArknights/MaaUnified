using Avalonia.Media;
using MAAUnified.App.ViewModels.Infrastructure;

namespace MAAUnified.App.ViewModels.TaskQueue;

public sealed class TaskQueueLogEntryViewModel : ObservableObject
{
    private string _time;
    private string _content;
    private string _level;
    private string? _color;
    private string _weight;

    public TaskQueueLogEntryViewModel(
        string time,
        string content,
        string level,
        string? color = null,
        string weight = "Regular")
    {
        _time = time;
        _content = content;
        _level = level;
        _color = string.IsNullOrWhiteSpace(color) ? null : color;
        _weight = weight;
    }

    public string Time
    {
        get => _time;
        set
        {
            if (!SetProperty(ref _time, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasTime));
        }
    }

    public string Content
    {
        get => _content;
        set => SetProperty(ref _content, value);
    }

    public string Level
    {
        get => _level;
        set
        {
            if (!SetProperty(ref _level, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(IsWarning));
            OnPropertyChanged(nameof(IsInfo));
            OnPropertyChanged(nameof(IsSuccess));
        }
    }

    // Retains the frozen WPF resource key (UiLogColor) so views can map the
    // semantic color onto theme-aware brushes. Null means no explicit WPF
    // color; level-based state classes decide the foreground instead.
    public string? Color
    {
        get => _color;
        set
        {
            if (!SetProperty(ref _color, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsInfoColor));
            OnPropertyChanged(nameof(IsRareOperatorColor));
            OnPropertyChanged(nameof(IsDownloadColor));
            OnPropertyChanged(nameof(IsMessageColor));
            OnPropertyChanged(nameof(IsSuccessISColor));
            OnPropertyChanged(nameof(IsSafehouseISColor));
            OnPropertyChanged(nameof(IsTraderISColor));
            OnPropertyChanged(nameof(IsTruthISColor));
            OnPropertyChanged(nameof(IsCombatISColor));
            OnPropertyChanged(nameof(IsEmergencyISColor));
            OnPropertyChanged(nameof(IsBossISColor));
            OnPropertyChanged(nameof(IsExplorationAbandonedISColor));
        }
    }

    public string Weight
    {
        get => _weight;
        set
        {
            if (!SetProperty(ref _weight, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsBold));
            OnPropertyChanged(nameof(DisplayFontWeight));
        }
    }

    public bool IsError => IsLevel("ERROR");

    public bool IsWarning => IsLevel("WARN") || IsLevel("WARNING");

    public bool IsInfo => IsLevel("INFO");

    public bool IsSuccess => IsLevel("SUCCESS");

    public bool IsBold => string.Equals(Weight, "Bold", StringComparison.OrdinalIgnoreCase);

    public FontWeight DisplayFontWeight => IsBold ? FontWeight.Bold : FontWeight.Normal;

    public bool IsInfoColor => string.Equals(Color, "InfoLogBrush", StringComparison.Ordinal);

    public bool IsRareOperatorColor =>
        string.Equals(Color, "RareOperatorLogBrush", StringComparison.Ordinal)
        || string.Equals(Color, "Star6OperatorLogBrush", StringComparison.Ordinal);

    public bool IsDownloadColor => string.Equals(Color, "DownloadLogBrush", StringComparison.Ordinal);

    public bool IsMessageColor => IsColor("MessageLogBrush");

    public bool IsSuccessISColor => IsColor("SuccessIS");

    public bool IsSafehouseISColor => IsColor("SafehouseIS");

    public bool IsTraderISColor => IsColor("TraderIS");

    public bool IsTruthISColor => IsColor("TruthIS");

    public bool IsCombatISColor => IsColor("CombatIS");

    public bool IsEmergencyISColor => IsColor("EmergencyIS");

    public bool IsBossISColor => IsColor("BossIS");

    public bool IsExplorationAbandonedISColor => IsColor("ExplorationAbandonedIS");

    public bool HasTime => !string.IsNullOrWhiteSpace(Time);

    private bool IsLevel(string level)
    {
        return string.Equals(Level, level, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsColor(string colorKey)
    {
        return string.Equals(Color, colorKey, StringComparison.Ordinal);
    }
}
