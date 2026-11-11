using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.TaskParams;

namespace MAAUnified.App.ViewModels.TaskQueue;

public sealed class CustomModuleViewModel : TypedTaskModuleViewModelBase<CustomTaskParamsDto>
{
    private string _taskNamesText = string.Empty;

    public CustomModuleViewModel(MAAUnifiedRuntime runtime, LocalizedTextMap texts)
        : base(runtime, texts, "TaskQueue.Custom")
    {
    }

    public string TaskNamesText
    {
        get => _taskNamesText;
        set
        {
            var normalized = (value ?? string.Empty).Replace('，', ',');
            if (!SetTrackedProperty(ref _taskNamesText, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(TaskNamesPreview));
        }
    }

    public string TaskNamesPreview
    {
        get
        {
            var values = ParseTaskNames(TaskNamesText);
            return values.Count == 0 ? string.Empty : string.Join(", ", values);
        }
    }

    protected override Task<UiOperationResult<CustomTaskParamsDto>> LoadDtoAsync(int index, CancellationToken cancellationToken)
    {
        return Runtime.TaskQueueFeatureService.GetCustomParamsAsync(index, cancellationToken);
    }

    protected override Task<UiOperationResult> SaveDtoAsync(int index, CustomTaskParamsDto dto, CancellationToken cancellationToken)
    {
        return Runtime.TaskQueueFeatureService.SaveCustomParamsAsync(index, dto, cancellationToken);
    }

    protected override TaskCompileOutput CompileDto(CustomTaskParamsDto dto, UnifiedProfile profile, UnifiedConfig config)
    {
        return TaskParamCompiler.CompileCustom(dto, profile, config);
    }

    protected override void ApplyDto(CustomTaskParamsDto dto)
    {
        TaskNamesText = string.Join(",", dto.TaskNames);
        OnPropertyChanged(nameof(TaskNamesPreview));
    }

    protected override CustomTaskParamsDto BuildDto()
    {
        return new CustomTaskParamsDto
        {
            TaskNames = ParseTaskNames(TaskNamesText),
        };
    }

    protected override IReadOnlyList<TaskValidationIssue> ValidateBeforeSave()
    {
        return [];
    }

    private static List<string> ParseTaskNames(string value)
    {
        return (value ?? string.Empty)
            .Replace('，', ',')
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .ToList();
    }
}
