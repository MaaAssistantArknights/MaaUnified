using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services;

namespace MAAUnified.App.ViewModels.TaskQueue;

public sealed class TaskQueueTaskPanelViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _hasLoadError;
    private string _loadErrorMessage = string.Empty;
    private string _validationSummary = string.Empty;
    private bool _hasBlockingValidationIssues;
    private int _validationIssueCount;

    public TaskQueueTaskPanelViewModel(
        TaskQueueItemViewModel task,
        int taskIndex,
        string moduleType,
        ITaskModulePanelViewModel moduleViewModel)
    {
        Task = task;
        TaskIndex = taskIndex;
        ModuleType = moduleType;
        Module = moduleViewModel;
        ModuleViewModel = moduleViewModel;
    }

    public TaskQueueItemViewModel Task { get; }

    public int TaskIndex { get; }

    public string ModuleType { get; }

    public ITaskModulePanelViewModel Module { get; }

    public object ModuleViewModel { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool HasLoadError
    {
        get => _hasLoadError;
        private set => SetProperty(ref _hasLoadError, value);
    }

    public string LoadErrorMessage
    {
        get => _loadErrorMessage;
        private set => SetProperty(ref _loadErrorMessage, value);
    }

    public string LastErrorMessage => HasLoadError ? LoadErrorMessage : Module.LastErrorMessage;

    public string ValidationSummary
    {
        get => _validationSummary;
        private set
        {
            if (SetProperty(ref _validationSummary, value))
            {
                OnPropertyChanged(nameof(HasValidationSummary));
            }
        }
    }

    public bool HasValidationSummary => !string.IsNullOrWhiteSpace(ValidationSummary);

    public bool HasBlockingValidationIssues
    {
        get => _hasBlockingValidationIssues;
        private set => SetProperty(ref _hasBlockingValidationIssues, value);
    }

    public int ValidationIssueCount
    {
        get => _validationIssueCount;
        private set => SetProperty(ref _validationIssueCount, value);
    }

    public void ApplyLoadError(string message)
    {
        HasLoadError = true;
        LoadErrorMessage = message;
    }

    public void ClearLoadError()
    {
        HasLoadError = false;
        LoadErrorMessage = string.Empty;
    }

    public void ApplyValidationReport(TaskValidationReport report, LocalizedTextMap texts)
    {
        var blockingCount = report.Issues.Count(static issue => issue.Blocking);
        ValidationIssueCount = blockingCount;
        HasBlockingValidationIssues = blockingCount > 0;
        ValidationSummary = blockingCount == 0
            ? string.Empty
            : string.Format(
                texts.GetOrDefault("TaskQueue.Validation.BlockingCount", "{0} blocking issue(s)."),
                blockingCount);
    }

    public void ApplyValidationLoadFailure(LocalizedTextMap texts, string? message = null)
    {
        ValidationIssueCount = 0;
        HasBlockingValidationIssues = false;
        ValidationSummary = texts.GetOrDefault(
            "TaskQueue.Validation.LoadFailed",
            "Failed to load validation report.");
        if (!string.IsNullOrWhiteSpace(message))
        {
            ApplyLoadError(message);
        }
    }

    public void ResetValidation()
    {
        ValidationIssueCount = 0;
        HasBlockingValidationIssues = false;
        ValidationSummary = string.Empty;
    }

    public void RefreshValidationSummaryLocalization(LocalizedTextMap texts)
    {
        if (ValidationIssueCount > 0)
        {
            ValidationSummary = string.Format(
                texts.GetOrDefault("TaskQueue.Validation.BlockingCount", "{0} blocking issue(s)."),
                ValidationIssueCount);
            return;
        }

        if (!string.IsNullOrWhiteSpace(ValidationSummary))
        {
            ValidationSummary = texts.GetOrDefault(
                "TaskQueue.Validation.LoadFailed",
                "Failed to load validation report.");
        }
    }
}
