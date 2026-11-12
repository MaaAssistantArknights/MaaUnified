using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.TaskParams;

namespace MAAUnified.App.ViewModels.TaskQueue;

public sealed class CustomModuleViewModel : TypedTaskModuleViewModelBase<CustomTaskParamsDto>
{
    private string _taskNamesText = string.Empty;

    /// <summary>
    /// 附加参数（如像素画的 <c>params.pixel_paint</c>）。编辑器只改任务名，
    /// 其余原样保留，避免保存时把 Core 需要的附加参数抹掉（WPF <c>AsstCustomTask.Params</c>）。
    /// </summary>
    private JsonObject? _attachedParams;

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
        _attachedParams = dto.Params?.DeepClone() as JsonObject;
        TaskNamesText = string.Join(",", dto.TaskNames);
        OnPropertyChanged(nameof(TaskNamesPreview));
    }

    protected override CustomTaskParamsDto BuildDto()
    {
        return new CustomTaskParamsDto
        {
            TaskNames = ParseTaskNames(TaskNamesText),
            Params = _attachedParams?.DeepClone() as JsonObject,
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
