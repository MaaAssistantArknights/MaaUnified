using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;

namespace MAAUnified.App.ViewModels.TaskQueue;

public static class TaskQueueItemStatus
{
    public const string Idle = "Idle";
    public const string Running = "Running";
    public const string Success = "Success";
    public const string Error = "Error";
    public const string Skipped = "Skipped";
}

public sealed class TaskQueueItemViewModel : ObservableObject
{
    private bool? _isEnabled;
    private string _name;
    private string _status = TaskQueueItemStatus.Idle;
    private string _moduleDisplayName;
    private string _statusDisplayName = TaskQueueItemStatus.Idle;
    private string _displayName;
    private string _toolTipText;
    private string _oneShotDisplayName = string.Empty;
    private List<int> _taskIds = [];
    private List<string> _chainStatuses = [];

    public TaskQueueItemViewModel(string type, string name, bool? isEnabled)
    {
        Type = TaskModuleTypes.Normalize(type);
        _name = name;
        _isEnabled = isEnabled;
        _moduleDisplayName = Type;
        _displayName = name;
        _toolTipText = name;
        RefreshToolTipText();
    }

    public string Type { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool? IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(IsOneShot));
                RefreshToolTipText();
            }
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                StatusDisplayName = value;
                OnPropertyChanged(nameof(IsStatusRunning));
                OnPropertyChanged(nameof(IsStatusSuccess));
                OnPropertyChanged(nameof(IsStatusError));
                OnPropertyChanged(nameof(IsStatusSkipped));
                OnPropertyChanged(nameof(IsStatusIdle));
            }
        }
    }

    public string ModuleDisplayName
    {
        get => _moduleDisplayName;
        set => SetProperty(ref _moduleDisplayName, value);
    }

    public string StatusDisplayName
    {
        get => _statusDisplayName;
        set
        {
            if (SetProperty(ref _statusDisplayName, value))
            {
                RefreshToolTipText();
            }
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
            {
                RefreshToolTipText();
            }
        }
    }

    public string ToolTipText
    {
        get => _toolTipText;
        private set => SetProperty(ref _toolTipText, value);
    }

    public string OneShotDisplayName
    {
        get => _oneShotDisplayName;
        set
        {
            if (SetProperty(ref _oneShotDisplayName, value))
            {
                RefreshToolTipText();
            }
        }
    }

    /// <summary>
    /// Gets 本行本轮对应的 Core 任务 id 列表（按 Core 分配顺序）。
    /// WPF dev-v2（9e846609aa、be31b86dbd）把单 id 换成列表：一个队列条目可以展开
    /// 成多条 Core 任务链（数据更新 2 条，库存保持 1 条识别 + 每计划 1 条战斗）。
    /// 未回填时为空列表，此时 <see cref="Status"/> 的写入按单链旧行为直接生效。
    /// </summary>
    public IReadOnlyList<int> TaskIds => _taskIds;

    /// <summary>
    /// Gets 逐链状态列表，与 <see cref="TaskIds"/> 一一对应。
    /// </summary>
    public IReadOnlyList<string> ChainStatuses => _chainStatuses;

    public int ChainCount => _taskIds.Count;

    /// <summary>
    /// Gets 本行已处理（完成或出错）的 chain 数，作为任务栏进度分子的组成单元
    /// （Error chain 视作已处理一格）。与 <see cref="Status"/> 的条目级聚合显示互不相干：
    /// 聚合值不变（如部分 chain 完成后条目仍显示 Running）时本计数仍在推进。
    /// </summary>
    public int CompletedChainCount => _chainStatuses.Count(
        status => IsStatusValue(status, TaskQueueItemStatus.Success) || IsStatusValue(status, TaskQueueItemStatus.Error));

    /// <summary>
    /// 回填本轮的 Core 任务 id（WPF TaskItemViewModel.SetTaskIds）。每个 id 从 Idle 起步，
    /// 逐链状态由 <see cref="TryApplyChainStatus"/> 推进。
    /// </summary>
    /// <param name="taskIds">Core 任务 id 序列；非正 id 不是真实任务，直接忽略。</param>
    public void SetTaskIds(IEnumerable<int>? taskIds)
    {
        var normalized = taskIds is null
            ? []
            : taskIds.Where(static id => id > 0).Distinct().ToList();
        _taskIds = normalized;
        _chainStatuses = [.. Enumerable.Repeat(TaskQueueItemStatus.Idle, normalized.Count)];
        OnPropertyChanged(nameof(TaskIds));
        OnPropertyChanged(nameof(ChainStatuses));
        OnPropertyChanged(nameof(ChainCount));
        OnPropertyChanged(nameof(CompletedChainCount));
    }

    /// <summary>
    /// 清空本轮 Core 任务 id 与逐链状态；回到 Idle 后行状态重新走单链旧行为。
    /// </summary>
    public void ClearTaskIds() => SetTaskIds(null);

    /// <summary>
    /// 应用一条 Core chain 的状态并按 WPF 口径重新聚合整行：
    /// Error &gt; InProgress &gt; 全 Completed &gt; Skipped &gt; Idle。
    /// 单链条目聚合结果与旧行为一致（最后一个事件即聚合值）。
    /// </summary>
    /// <param name="taskId">回调携带的 Core 任务 id。</param>
    /// <param name="status">该 chain 的新状态。</param>
    /// <returns>该 id 属于本行且已应用时为 true；否则 false，调用方回退到单链旧行为。</returns>
    public bool TryApplyChainStatus(int taskId, string status)
    {
        var index = _taskIds.IndexOf(taskId);
        if (index < 0)
        {
            return false;
        }

        if (IsStatusValue(_chainStatuses[index], status))
        {
            return true;
        }

        _chainStatuses[index] = status;
        OnPropertyChanged(nameof(ChainStatuses));
        OnPropertyChanged(nameof(CompletedChainCount));
        Status = AggregateChainStatus();
        return true;
    }

    private string AggregateChainStatus()
    {
        if (_chainStatuses.Count == 0)
        {
            return TaskQueueItemStatus.Idle;
        }

        if (_chainStatuses.Any(status => IsStatusValue(status, TaskQueueItemStatus.Error)))
        {
            return TaskQueueItemStatus.Error;
        }

        if (_chainStatuses.Any(status => IsStatusValue(status, TaskQueueItemStatus.Running)))
        {
            return TaskQueueItemStatus.Running;
        }

        if (_chainStatuses.All(status => IsStatusValue(status, TaskQueueItemStatus.Success)))
        {
            return TaskQueueItemStatus.Success;
        }

        return _chainStatuses.Any(status => IsStatusValue(status, TaskQueueItemStatus.Skipped))
            ? TaskQueueItemStatus.Skipped
            : TaskQueueItemStatus.Idle;
    }

    private static bool IsStatusValue(string? status, string expected)
        => string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);

    public bool IsOneShot => IsEnabled is null;

    public bool IsStatusRunning => IsStatus(TaskQueueItemStatus.Running);

    public bool IsStatusSuccess => IsStatus(TaskQueueItemStatus.Success);

    public bool IsStatusError => IsStatus(TaskQueueItemStatus.Error);

    public bool IsStatusSkipped => IsStatus(TaskQueueItemStatus.Skipped);

    public bool IsStatusIdle =>
        !IsStatusRunning
        && !IsStatusSuccess
        && !IsStatusError
        && !IsStatusSkipped;

    public static TaskQueueItemViewModel FromUnifiedTask(UnifiedTaskItem task)
    {
        return new TaskQueueItemViewModel(task.Type, task.Name, task.IsEnabled);
    }

    public void RefreshLocalizedText(Func<string, string> resolveModuleDisplayName, Func<string, string> resolveStatusDisplayName)
    {
        ModuleDisplayName = resolveModuleDisplayName(Type);
        StatusDisplayName = resolveStatusDisplayName(Status);
        RefreshToolTipText();
    }

    public void RefreshToolTipText()
    {
        var title = string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = ModuleDisplayName;
        }

        var statusText = string.IsNullOrWhiteSpace(StatusDisplayName)
            ? title
            : $"{title} ({StatusDisplayName})";
        ToolTipText = IsOneShot && !string.IsNullOrWhiteSpace(OneShotDisplayName)
            ? $"{statusText}\n{OneShotDisplayName}"
            : statusText;
    }

    private bool IsStatus(string expected)
    {
        return string.Equals(Status, expected, StringComparison.OrdinalIgnoreCase);
    }
}
