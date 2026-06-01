using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.Services;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Services.TaskParams;

namespace MAAUnified.App.ViewModels.TaskQueue;

public abstract class TypedTaskModuleViewModelBase<TDto> : ObservableObject, ITaskModulePanelViewModel
    where TDto : class, new()
{
    private bool _isAdvancedMode;
    private bool _isTaskBound;
    private bool _isDirty;
    private bool _isApplyingDto;
    private int _boundTaskIndex = -1;
    private string _statusMessage = string.Empty;
    private string _lastErrorMessage = string.Empty;

    protected TypedTaskModuleViewModelBase(MAAUnifiedRuntime runtime, LocalizedTextMap texts, string scope)
    {
        Runtime = runtime;
        Texts = texts;
        Scope = scope;
        Texts.PropertyChanged += OnTextsPropertyChanged;
    }

    protected MAAUnifiedRuntime Runtime { get; }

    public LocalizedTextMap Texts { get; }

    protected string Scope { get; }

    protected int BoundTaskIndex => _boundTaskIndex;

    protected bool IsApplyingDto => _isApplyingDto;

    public ObservableCollection<string> ValidationMessages { get; } = [];

    private void OnTextsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName)
            && !string.Equals(e.PropertyName, nameof(LocalizedTextMap.Language), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, "Item[]", StringComparison.Ordinal))
        {
            return;
        }

        OnPropertyChanged(nameof(Texts));
        // Re-raise all module projections so selected-task views can update without recreating the VM.
        OnPropertyChanged(string.Empty);
    }

    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (!SetProperty(ref _isAdvancedMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsGeneralMode));
        }
    }

    public bool IsGeneralMode => !IsAdvancedMode;

    public bool IsTaskBound
    {
        get => _isTaskBound;
        private set => SetProperty(ref _isTaskBound, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        protected set => SetProperty(ref _isDirty, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        protected set => SetProperty(ref _statusMessage, value);
    }

    public string LastErrorMessage
    {
        get => _lastErrorMessage;
        protected set => SetProperty(ref _lastErrorMessage, value);
    }

    public bool HasValidationIssues => ValidationMessages.Count > 0;

    public async Task BindAsync(int index, CancellationToken cancellationToken = default)
    {
        _boundTaskIndex = index;

        var loaded = await LoadDtoAsync(index, cancellationToken);
        if (!loaded.Success || loaded.Value is null)
        {
            IsTaskBound = false;
            _boundTaskIndex = -1;
            LastErrorMessage = loaded.Message;
            await Runtime.DiagnosticsService.RecordFailedResultAsync(
                $"{Scope}.Load",
                UiOperationResult.Fail(loaded.Error?.Code ?? UiErrorCode.TaskLoadFailed, loaded.Message, loaded.Error?.Details),
                cancellationToken);
            return;
        }

        _isApplyingDto = true;
        try
        {
            ApplyDto(loaded.Value);
        }
        finally
        {
            _isApplyingDto = false;
        }

        ValidationMessages.Clear();
        OnPropertyChanged(nameof(HasValidationIssues));
        IsTaskBound = true;
        IsDirty = false;
        LastErrorMessage = string.Empty;
        StatusMessage = string.Empty;
    }

    public virtual void ClearBinding()
    {
        _boundTaskIndex = -1;
        IsTaskBound = false;
        IsDirty = false;
        ValidationMessages.Clear();
        OnPropertyChanged(nameof(HasValidationIssues));
    }

    public void RebindTaskIndex(int taskIndex)
    {
        if (_boundTaskIndex >= 0)
        {
            _boundTaskIndex = taskIndex;
        }
    }

    public Task<bool> SaveIfDirtyAsync(CancellationToken cancellationToken = default)
    {
        if (!IsTaskBound || !IsDirty)
        {
            return Task.FromResult(true);
        }

        return SaveAsync(cancellationToken);
    }

    public Task<bool> FlushPendingChangesAsync(CancellationToken cancellationToken = default)
    {
        return SaveIfDirtyAsync(cancellationToken);
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        return await ConfigurationSaveTracker.Instance.RunTrackedAsync(
            Scope,
            ResolveSaveDisplayName(),
            $"{Scope}.Save",
            Runtime.DiagnosticsService,
            SaveCoreAsync,
            cancellationToken);
    }

    private async Task<bool> SaveCoreAsync(CancellationToken cancellationToken = default)
    {
        if (!IsTaskBound || _boundTaskIndex < 0)
        {
            return true;
        }

        if (!Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            LastErrorMessage = string.Format(
                Texts.GetOrDefault("TaskQueue.Error.ProfileMissing", "Current profile `{0}` not found."),
                Runtime.ConfigurationService.CurrentConfig.CurrentProfile);
            await Runtime.DiagnosticsService.RecordErrorAsync($"{Scope}.Save", LastErrorMessage, cancellationToken: cancellationToken);
            return false;
        }

        var preValidationIssues = ValidateBeforeSave();
        if (preValidationIssues.Any(i => i.Blocking))
        {
            ApplyValidationIssues(preValidationIssues);
            var preMessage = BuildValidationSummary(preValidationIssues.Where(i => i.Blocking));
            LastErrorMessage = string.Empty;
            await Runtime.DiagnosticsService.RecordFailedResultAsync(
                $"{Scope}.Validate",
                UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, preMessage, BuildValidationDetails(preValidationIssues)),
                cancellationToken);
            return false;
        }

        var dto = BuildDto();
        var compiled = CompileDto(dto, profile, Runtime.ConfigurationService.CurrentConfig);
        var allIssues = preValidationIssues.Count == 0
            ? compiled.Issues
            : preValidationIssues.Concat(compiled.Issues).ToList();
        ApplyValidationIssues(allIssues);
        if (allIssues.Any(i => i.Blocking))
        {
            var message = BuildValidationSummary(allIssues.Where(i => i.Blocking));
            LastErrorMessage = string.Empty;
            await Runtime.DiagnosticsService.RecordFailedResultAsync(
                $"{Scope}.Validate",
                UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, message, BuildValidationDetails(allIssues)),
                cancellationToken);
            return false;
        }

        var save = await SaveDtoAsync(_boundTaskIndex, dto, cancellationToken);
        if (!save.Success)
        {
            LastErrorMessage = save.Message;
            await Runtime.DiagnosticsService.RecordFailedResultAsync($"{Scope}.Save", save, cancellationToken);
            return false;
        }

        IsDirty = false;
        LastErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        return true;
    }

    private string ResolveSaveDisplayName()
    {
        var key = Scope.StartsWith("TaskQueue.", StringComparison.Ordinal)
            ? Scope["TaskQueue.".Length..]
            : Scope;

        return key switch
        {
            "StartUp" => Texts.GetOrDefault("StartUp.Title", "开始唤醒"),
            "Fight" => Texts.GetOrDefault("Fight.Title", "自动战斗"),
            "Recruit" => Texts.GetOrDefault("Recruit.Title", "自动公招"),
            "Roguelike" => Texts.GetOrDefault("Roguelike.Title", "自动肉鸽"),
            "Reclamation" => Texts.GetOrDefault("Reclamation.Title", "生息演算"),
            "Custom" => Texts.GetOrDefault("Custom.Title", "自定义任务"),
            _ => key,
        };
    }

    protected bool SetTrackedProperty<T>(ref T backingField, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref backingField, value, propertyName))
        {
            return false;
        }

        if (!_isApplyingDto)
        {
            IsDirty = true;
        }

        return true;
    }

    protected void MarkDirty()
    {
        if (!_isApplyingDto)
        {
            IsDirty = true;
        }
    }

    protected virtual void ApplyValidationIssues(IReadOnlyList<TaskValidationIssue> issues)
    {
        ValidationMessages.Clear();
        foreach (var issue in issues.Where(static issue => issue.Blocking))
        {
            var localizedMessage = ResolveValidationMessage(issue);
            if (string.IsNullOrWhiteSpace(localizedMessage) || ValidationMessages.Contains(localizedMessage))
            {
                continue;
            }

            ValidationMessages.Add(localizedMessage);
        }

        OnPropertyChanged(nameof(HasValidationIssues));
    }

    private string ResolveValidationMessage(TaskValidationIssue issue)
    {
        var language = string.Equals(
            UiLanguageCatalog.Normalize(Texts.Language),
            UiLanguageCatalog.DefaultLanguage,
            StringComparison.OrdinalIgnoreCase)
            ? UiLanguageCatalog.DefaultLanguage
            : UiLanguageCatalog.FallbackLanguage;
        return Texts.GetOrDefaultForLanguage(language, $"Issue.{issue.Code}", issue.Message);
    }

    protected static string BuildValidationSummary(IEnumerable<TaskValidationIssue> issues)
    {
        return string.Join("; ", issues.Select(i => $"{i.Field}: {i.Message}"));
    }

    protected static string BuildValidationDetails(IEnumerable<TaskValidationIssue> issues)
    {
        return string.Join(" | ", issues.Select(i => $"{i.Code}:{i.Field}:{i.Message}"));
    }

    protected virtual IReadOnlyList<TaskValidationIssue> ValidateBeforeSave()
    {
        return [];
    }

    protected abstract Task<UiOperationResult<TDto>> LoadDtoAsync(int index, CancellationToken cancellationToken);

    protected abstract Task<UiOperationResult> SaveDtoAsync(int index, TDto dto, CancellationToken cancellationToken);

    protected abstract TaskCompileOutput CompileDto(TDto dto, UnifiedProfile profile, UnifiedConfig config);

    protected abstract void ApplyDto(TDto dto);

    protected abstract TDto BuildDto();
}
