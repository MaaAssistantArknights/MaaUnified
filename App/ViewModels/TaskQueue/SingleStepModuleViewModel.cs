using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.TaskParams;

namespace MAAUnified.App.ViewModels.TaskQueue;

public sealed class SingleStepModuleViewModel : TaskModuleSettingsViewModelBase
{
    private JsonObject _parameters = new();
    private string _type = string.Empty;
    private string _subtype = string.Empty;
    private string _detailsText = string.Empty;
    private IReadOnlyList<StringOption> _subtypeOptions = [];

    public SingleStepModuleViewModel(MAAUnifiedRuntime runtime, LocalizedTextMap texts)
        : base(runtime, texts, TaskModuleTypes.SingleStep)
    {
        Texts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LocalizedTextMap.Language) or "Item[]")
            {
                RebuildSubtypeOptions();
            }
        };
        RebuildSubtypeOptions();
    }

    public string TitleText => Texts.GetOrDefault("TaskQueue.Module.SingleStep", "Single Step");

    public string BodyText => Texts.GetOrDefault(
        "TaskQueue.SingleStep.Body",
        "SingleStep is a Core single-step copilot entry. Imported marker tasks need runtime step parameters before they can run.");

    public string Type
    {
        get => _type;
        set
        {
            if (SetTrackedProperty(ref _type, value ?? string.Empty))
            {
                QueuePersist();
            }
        }
    }

    public string Subtype
    {
        get => _subtype;
        set
        {
            if (!SetTrackedProperty(ref _subtype, value ?? string.Empty))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedSubtypeOption));
            OnPropertyChanged(nameof(DetailsRequired));
            QueuePersist();
        }
    }

    public bool DetailsRequired =>
        string.Equals(Subtype, "stage", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Subtype, "action", StringComparison.OrdinalIgnoreCase);

    public string DetailsText
    {
        get => _detailsText;
        set
        {
            if (SetTrackedProperty(ref _detailsText, value ?? string.Empty))
            {
                QueuePersist();
            }
        }
    }

    public IReadOnlyList<StringOption> SubtypeOptions => _subtypeOptions;

    public StringOption? SelectedSubtypeOption
    {
        get => SubtypeOptions.FirstOrDefault(option => string.Equals(option.Value, Subtype, StringComparison.OrdinalIgnoreCase));
        set => Subtype = value?.Value ?? string.Empty;
    }

    protected override Task LoadFromParametersAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        _parameters = parameters.DeepClone() as JsonObject ?? new JsonObject();
        var (dto, _) = TaskParamCompiler.ReadSingleStep(
            new UnifiedTaskItem { Type = TaskModuleTypes.SingleStep, Name = string.Empty, Params = _parameters },
            strict: false);
        Type = string.IsNullOrWhiteSpace(dto.Type) ? "copilot" : dto.Type;
        Subtype = dto.Subtype;
        DetailsText = dto.Details?.ToJsonString() ?? string.Empty;
        return Task.CompletedTask;
    }

    protected override JsonObject BuildParameters()
    {
        var parameters = new JsonObject();
        if (!string.IsNullOrWhiteSpace(Type))
        {
            parameters["type"] = Type.Trim();
        }

        if (!string.IsNullOrWhiteSpace(Subtype))
        {
            parameters["subtype"] = Subtype.Trim();
        }

        if (!string.IsNullOrWhiteSpace(DetailsText))
        {
            parameters["details"] = JsonNode.Parse(DetailsText);
        }

        _parameters = parameters.DeepClone() as JsonObject ?? new JsonObject();
        return parameters;
    }

    private void RebuildSubtypeOptions()
    {
        _subtypeOptions =
        [
            new StringOption("start", Texts.GetOrDefault("TaskQueue.SingleStep.Subtype.Start", "Start")),
            new StringOption("stage", Texts.GetOrDefault("TaskQueue.SingleStep.Subtype.Stage", "Stage")),
            new StringOption("action", Texts.GetOrDefault("TaskQueue.SingleStep.Subtype.Action", "Action")),
        ];

        OnPropertyChanged(nameof(SubtypeOptions));
        OnPropertyChanged(nameof(SelectedSubtypeOption));
    }

    private bool SetTrackedProperty<T>(ref T backingField, T value, string? propertyName = null)
    {
        return SetProperty(ref backingField, value, propertyName);
    }

    public sealed record StringOption(string Value, string DisplayName);
}
